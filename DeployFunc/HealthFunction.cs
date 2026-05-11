using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;

namespace DeployFunc
{
    public class HealthFunction
    {
        private readonly ILogger<HealthFunction> _logger;
        private readonly ServiceMapDbContext _db;
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        public HealthFunction(ILogger<HealthFunction> logger, ServiceMapDbContext db)
        {
            _logger = logger;
            _db = db;
        }

        [Function("RunIrisServiceHealthChecks")]
        public async Task RunIrisServiceHealthChecks([TimerTrigger("0 */3 * * * *")] TimerInfo timer)
        {
            var now = DateTime.UtcNow;

            var irisServices = await _db.ServiceMaps
                .Where(x => x.active && x.servicetype == "iris" && x.ipaddr != null)
                .ToListAsync();

            foreach (var service in irisServices)
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
                    var url = $"{protocol}://{service.hostname}";///live/server".Replace("iris", "icontrol");
                    Console.WriteLine($"Checking health for service {service.id} at {url}");
                    var sw = Stopwatch.StartNew();
                    var response = await _httpClient.GetAsync(url);
                    sw.Stop();

                    healthLog.statusCode = (int)response.StatusCode;
                    healthLog.responseTimeMs = (int)sw.ElapsedMilliseconds;
                    healthLog.isHealthy = response.IsSuccessStatusCode;

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
                            durationSeconds = 0
                        });
                    }
                    else
                    {
                        ongoingOutage.failureCount += 1;
                        ongoingOutage.lastUpdated = now;
                        ongoingOutage.durationSeconds = (int)(now - ongoingOutage.startTime).TotalSeconds;
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
            _logger.LogInformation("Health check cycle completed for {ServiceCount} iris services.", irisServices.Count);
        }
    }
}
