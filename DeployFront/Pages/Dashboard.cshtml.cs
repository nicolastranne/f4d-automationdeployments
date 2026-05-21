using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using DeployFront.Pages.ServiceMap;

namespace DeployFront.Pages
{
    public class DashboardModel : PageModel
    {
        private readonly ServiceMapDbContext _db;

        [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }

        [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
        public bool OutagesOnly { get; set; }

        public int CurrentOutages { get; set; }
        public int IrisServiceCount { get; set; }
        public int UnhealthyChecksLastHour { get; set; }
        public double AverageResponseMsLastHour { get; set; }
        public List<IrisUptimeCard> IrisUptimeCards { get; set; } = new();

        public DashboardModel(ServiceMapDbContext db)
        {
            _db = db;
        }

        public async Task OnGetAsync()
        {
            var now = DateTime.UtcNow;
            var oneHourAgo = now.AddHours(-1);
            var oneDayAgo = now.AddDays(-1);

            var irisServicesQuery = _db.ServiceMaps
                .Where(s => s.active && s.servicetype == "iris");

            if (!string.IsNullOrWhiteSpace(SearchTerm))
            {
                irisServicesQuery = irisServicesQuery
                    .Where(s => s.hostname.Contains(SearchTerm));
            }

            var irisServices = await irisServicesQuery
                .Select(s => new { s.id, s.hostname, s.protocol, s.port, s.appversion })
                .ToListAsync();

            var irisServiceIds = irisServices.Select(s => s.id).ToList();

            var currentOutageServiceIds = await _db.Outages
                .Where(o => o.isOngoing && irisServiceIds.Contains(o.serviceId))
                .Select(o => o.serviceId)
                .Distinct()
                .ToListAsync();

            if (OutagesOnly)
            {
                irisServices = irisServices
                    .Where(s => currentOutageServiceIds.Contains(s.id))
                    .ToList();
                irisServiceIds = irisServices.Select(s => s.id).ToList();
            }

            IrisServiceCount = irisServiceIds.Count;

            CurrentOutages = currentOutageServiceIds.Count;

            UnhealthyChecksLastHour = await _db.ServiceHealthCheckLogs
                .Where(h => irisServiceIds.Contains(h.serviceId) && h.checkTime >= oneHourAgo && !h.isHealthy)
                .CountAsync();

            var avgResponse = await _db.ServiceHealthCheckLogs
                .Where(h => irisServiceIds.Contains(h.serviceId) && h.checkTime >= oneHourAgo && h.responseTimeMs != null)
                .AverageAsync(h => (double?)h.responseTimeMs);

            AverageResponseMsLastHour = avgResponse ?? 0;

            var logsLastDay = await _db.ServiceHealthCheckLogs
                .Where(h => irisServiceIds.Contains(h.serviceId) && h.checkTime >= oneDayAgo)
                .Select(h => new { h.serviceId, h.isHealthy })
                .ToListAsync();

            IrisUptimeCards = irisServices
                .Select(s =>
                {
                    var serviceLogs = logsLastDay.Where(l => l.serviceId == s.id).ToList();
                    var total = serviceLogs.Count;
                    var healthy = serviceLogs.Count(l => l.isHealthy);
                    var uptimePercent = total == 0 ? 0 : Math.Round((healthy * 100.0) / total, 2);

                    return new IrisUptimeCard
                    {
                        ServiceId = s.id,
                        Hostname = s.hostname,
                        AppVersion = s.appversion,
                        ServiceUrl = BuildServiceUrl(s.protocol, s.hostname, s.port),
                        UptimePercent = uptimePercent,
                        TotalChecks = total,
                        HealthyChecks = healthy
                    };
                })
                .OrderByDescending(c => c.UptimePercent)
                .ToList();
        }

        private static string BuildServiceUrl(string? protocol, string hostname, int port)
        {
            var scheme = string.IsNullOrWhiteSpace(protocol) ? "http" : protocol;
            return $"{scheme}://{hostname}";
        }

        public class IrisUptimeCard
        {
            public int ServiceId { get; set; }
            public string Hostname { get; set; } = string.Empty;
            public string? AppVersion { get; set; }
            public string ServiceUrl { get; set; } = string.Empty;
            public double UptimePercent { get; set; }
            public int TotalChecks { get; set; }
            public int HealthyChecks { get; set; }
        }
    }
}
