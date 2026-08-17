using Azure.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace DeployFunc
{
    public class HealthFunction
    {
        private readonly ILogger<HealthFunction> _logger;
        private readonly ServiceMapDbContext _db;
        private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(10);
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = HealthCheckTimeout
        };

        public HealthFunction(ILogger<HealthFunction> logger, ServiceMapDbContext db)
        {
            _logger = logger;
            _db = db;
        }

        [Function("RunAllServiceHealthChecks")]
        public async Task RunAllServiceHealthChecks([TimerTrigger("0 */3 * * * *")] TimerInfo timer)
        {
            var now = DateTime.UtcNow;
            var trackedServiceTypes = new[] { "iris", "ireport", "goline", "sesame", "icontrol" };

            var services = await _db.ServiceMaps
                .Where(x => x.active
                    && x.ipaddr != null
                    && x.servicetype != null
                    && trackedServiceTypes.Contains(x.servicetype.ToLower()))
                .ToListAsync();

            var healthCheckResults = await Task.WhenAll(services.Select(async service =>
            {
                var healthLog = new ServiceHealthCheckLog
                {
                    serviceId = service.id,
                    checkTime = now,
                    isHealthy = false
                };

                try
                {
                    var protocol = string.IsNullOrWhiteSpace(service.protocol) ? "http" : service.protocol;
                    var url = $"{protocol}://{service.hostname}";
                    var normalizedServiceType = (service.servicetype ?? string.Empty).ToLowerInvariant();
                    if (normalizedServiceType == "icontrol")
                        url = $"{url}/live/server";

                    Console.WriteLine($"Checking health for service {service.id} at {url}");
                    var sw = Stopwatch.StartNew();

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("Special-Agent", @"-\\_(-_-)_/-");
                    using var cts = new CancellationTokenSource(HealthCheckTimeout);
                    using var response = await _httpClient.SendAsync(request, cts.Token);

                    sw.Stop();

                    healthLog.statusCode = (int)response.StatusCode;
                    healthLog.responseTimeMs = (int)sw.ElapsedMilliseconds;
                    healthLog.isHealthy = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized; 

                    if (!healthLog.isHealthy)
                    {
                        healthLog.errorMessage = $"Non-success status code: {(int)response.StatusCode}";
                    }
                }
                catch (Exception ex)
                {
                    healthLog.isHealthy = false;
                    healthLog.errorMessage = ex.Message;
                }

                return (service, healthLog);
            }));

            foreach (var result in healthCheckResults)
            {
                var service = result.service;
                var healthLog = result.healthLog;

                _db.ServiceHealthCheckLogs.Add(healthLog);

                if (!healthLog.isHealthy)
                {
                    var ongoingOutage = await _db.Outages
                        .Where(o => o.serviceId == service.id && o.isOngoing)
                        .OrderByDescending(o => o.startTime)
                        .FirstOrDefaultAsync();

                    if (ongoingOutage == null)
                    {
                        _db.Outages.Add(new ServiceOutage
                        {
                            serviceId = service.id,
                            startTime = now,
                            isOngoing = true,
                            failureCount = 1,
                            lastUpdated = now,
                            durationSeconds = 0,
                            reason = healthLog.errorMessage
                        });
                    }
                    else
                    {
                        ongoingOutage.failureCount += 1;
                        ongoingOutage.lastUpdated = now;
                        ongoingOutage.durationSeconds = (int)(now - ongoingOutage.startTime).TotalSeconds;
                        ongoingOutage.reason = healthLog.errorMessage;
                        _db.Outages.Update(ongoingOutage);
                    }
                }
                else
                {
                    var ongoingOutage = await _db.Outages
                        .Where(o => o.serviceId == service.id && o.isOngoing)
                        .OrderByDescending(o => o.startTime)
                        .FirstOrDefaultAsync();

                    if (ongoingOutage != null)
                    {
                        ongoingOutage.isOngoing = false;
                        ongoingOutage.endTime = now;
                        ongoingOutage.lastUpdated = now;
                        ongoingOutage.durationSeconds = (int)(now - ongoingOutage.startTime).TotalSeconds;
                        _db.Outages.Update(ongoingOutage);
                    }
                }
            }

            await _db.SaveChangesAsync();
            _logger.LogInformation("Health check cycle completed for {ServiceCount} services.", services.Count);
        }
    }
}
