using Azure.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace DeployFunc
{
    public class HealthFunction
    {
        private readonly ILogger<HealthFunction> _logger;
        private readonly ServiceMapDbContext _db;
        private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(10);
        private static readonly int OutageNotificationThreshold = 3;
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
                        ongoingOutage = new ServiceOutage
                        {
                            serviceId = service.id,
                            startTime = now,
                            isOngoing = true,
                            failureCount = 1,
                            lastUpdated = now,
                            durationSeconds = 0,
                            reason = healthLog.errorMessage
                        };
                        _db.Outages.Add(ongoingOutage);
                    }
                    else
                    {
                        ongoingOutage.failureCount += 1;
                        ongoingOutage.lastUpdated = now;
                        ongoingOutage.durationSeconds = (int)(now - ongoingOutage.startTime).TotalSeconds;
                        ongoingOutage.reason = healthLog.errorMessage;
                        _db.Outages.Update(ongoingOutage);
                    }

                    if (ongoingOutage.failureCount == OutageNotificationThreshold)
                    {
                        await NotifyOutageThresholdReachedAsync(service, ongoingOutage, healthLog);
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

                        await NotifyServiceBackOnlineAsync(service, ongoingOutage, healthLog);
                    }
                }
            }

            await _db.SaveChangesAsync();
            _logger.LogInformation("Health check cycle completed for {ServiceCount} services.", services.Count);
        }

        private async Task NotifyOutageThresholdReachedAsync(ServiceMap service, ServiceOutage outage, ServiceHealthCheckLog healthLog)
        {
            var title = $"Service outage alert: {service.customer} {service.site}";
            var message = $"Service '{service.hostname}' ({service.servicetype}) has {outage.failureCount} consecutive failed health checks.\n" +
                          $"IP: {service.ipaddr}:{service.port}\n" +
                          $"Started: {outage.startTime:yyyy-MM-dd HH:mm:ss} UTC\n" +
                          $"Last Error: {healthLog.errorMessage ?? "Unknown"}";

            await SendNotificationAsync(service.id, title, message, "Outage threshold reached");
        }

        private async Task NotifyServiceBackOnlineAsync(ServiceMap service, ServiceOutage outage, ServiceHealthCheckLog healthLog)
        {
            var title = $"Service recovered: {service.customer} {service.site}";
            var message = $"Service '{service.hostname}' ({service.servicetype}) is back online.\n" +
                          $"IP: {service.ipaddr}:{service.port}\n" +
                          $"Outage Start: {outage.startTime:yyyy-MM-dd HH:mm:ss} UTC\n" +
                          $"Outage End: {outage.endTime:yyyy-MM-dd HH:mm:ss} UTC\n" +
                          $"Duration Seconds: {outage.durationSeconds ?? 0}\n" +
                          $"Recovered Status Code: {(healthLog.statusCode?.ToString() ?? "N/A")}";

            await SendNotificationAsync(service.id, title, message, "Service recovered");
        }

        private async Task SendNotificationAsync(int serviceId, string title, string message, string logContext)
        {
            var teamsWebhookUrl = Environment.GetEnvironmentVariable("TeamsWebhookUrl");
            var alertEmailTo = Environment.GetEnvironmentVariable("AlertEmailTo");

            if (string.IsNullOrWhiteSpace(teamsWebhookUrl) && string.IsNullOrWhiteSpace(alertEmailTo))
            {
                _logger.LogWarning("{Context} for service {ServiceId} but no TeamsWebhookUrl or AlertEmailTo is configured.", logContext, serviceId);
                return;
            }

            if (!string.IsNullOrWhiteSpace(teamsWebhookUrl))
            {
                try
                {
                    var payload = new
                    {
                        title,
                        text = message.Replace("\n", "<br/>")
                    };

                    using var response = await _httpClient.PostAsJsonAsync(teamsWebhookUrl, payload);
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        _logger.LogWarning("Failed sending Teams webhook notification for service {ServiceId}. Status: {StatusCode}, Body: {Body}", serviceId, response.StatusCode, body);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending Teams webhook notification for service {ServiceId}.", serviceId);
                }
            }

            if (!string.IsNullOrWhiteSpace(alertEmailTo))
            {
                var recipientEmails = alertEmailTo
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (recipientEmails.Length == 0)
                {
                    _logger.LogWarning("AlertEmailTo is configured but no valid recipient emails were found for service {ServiceId}.", serviceId);
                }
                else
                {
                    await SendEmailWithSendGridAsync(recipientEmails, title, message, serviceId);
                }
            }
        }

        private async Task SendEmailWithSendGridAsync(string[] toEmails, string subject, string plainMessage, int serviceId)
        {
            var sendGridApiKey = Environment.GetEnvironmentVariable("SendGridApiKey");
            var sendGridFromEmail = Environment.GetEnvironmentVariable("SendGridFromEmail");
            var sendGridFromName = Environment.GetEnvironmentVariable("SendGridFromName") ?? "Health Monitor";

            if (string.IsNullOrWhiteSpace(sendGridApiKey)
                || string.IsNullOrWhiteSpace(sendGridFromEmail))
            {
                _logger.LogWarning("SendGrid email requested for service {ServiceId} but SendGridApiKey or SendGridFromEmail is not configured.", serviceId);
                return;
            }

            try
            {
                var toList = toEmails.Select(email => new { email }).ToArray();
                var payload = new
                {
                    personalizations = new[]
                    {
                        new
                        {
                            to = toList
                        }
                    },
                    from = new
                    {
                        email = sendGridFromEmail,
                        name = sendGridFromName
                    },
                    subject,
                    content = new[]
                    {
                        new
                        {
                            type = "text/plain",
                            value = plainMessage
                        }
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send")
                {
                    Content = JsonContent.Create(payload)
                };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sendGridApiKey);

                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Failed sending SendGrid email for service {ServiceId}. Status: {StatusCode}, Body: {Body}", serviceId, response.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending SendGrid email for service {ServiceId}.", serviceId);
            }
        }
    }
}
