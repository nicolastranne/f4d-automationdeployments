using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using DeployFront.Pages.ServiceMap;

namespace DeployFront.Pages.Dashboard
{
    public class IndexModel : PageModel
    {
        private readonly ServiceMapDbContext _db;

        [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }

        [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
        public bool OutagesOnly { get; set; }

        [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
        public string? ServiceTypeFilter { get; set; }

        public int CurrentOutages { get; set; }
        public int TrackedServiceCount { get; set; }
        public int IrisServiceCount { get; set; }
        public int IreportServiceCount { get; set; }
        public int SesameServiceCount { get; set; }
        public int GolineServiceCount { get; set; }
        public int IcontrolServiceCount { get; set; }
        public int UnhealthyChecksLastHour { get; set; }
        public double AverageResponseMsLastHour { get; set; }
        public List<ServiceUptimeCard> IrisUptimeCards
        {
            get => ServiceUptimeCards;
            set => ServiceUptimeCards = value;
        }
        public List<ServiceUptimeCard> ServiceUptimeCards { get; set; } = new();

        public IndexModel(ServiceMapDbContext db)
        {
            _db = db;
        }

        public async Task OnGetAsync()
        {
            var now = DateTime.UtcNow;
            var oneHourAgo = now.AddHours(-1);
            var sevenDaysAgo = now.AddDays(-7);

            var trackedServiceTypes = new[] { "iris", "ireport", "sesame", "goline", "icontrol" };

            var servicesQuery = _db.ServiceMaps
                .Where(s => s.active && s.servicetype != null && trackedServiceTypes.Contains(s.servicetype));

            if (!string.IsNullOrWhiteSpace(SearchTerm))
            {
                servicesQuery = servicesQuery
                    .Where(s => s.hostname.Contains(SearchTerm));
            }

            if (!string.IsNullOrWhiteSpace(ServiceTypeFilter))
            {
                servicesQuery = servicesQuery
                    .Where(s => s.servicetype == ServiceTypeFilter);
            }

            var services = await servicesQuery
                .Select(s => new { s.id, s.hostname, s.protocol, s.port, s.appversion, s.servicetype, s.excludefromstats })
                .ToListAsync();

            var serviceIds = services.Select(s => s.id).ToList();

            var currentOutageServiceIds = await _db.Outages
                .Where(o => o.isOngoing && serviceIds.Contains(o.serviceId))
                .Select(o => o.serviceId)
                .Distinct()
                .ToListAsync();

            if (OutagesOnly)
            {
                services = services
                    .Where(s => currentOutageServiceIds.Contains(s.id))
                    .ToList();
                serviceIds = services.Select(s => s.id).ToList();
            }

            TrackedServiceCount = serviceIds.Count;
            IrisServiceCount = services.Count(s => string.Equals(s.servicetype, "iris", StringComparison.OrdinalIgnoreCase));
            IreportServiceCount = services.Count(s => string.Equals(s.servicetype, "ireport", StringComparison.OrdinalIgnoreCase));
            SesameServiceCount = services.Count(s => string.Equals(s.servicetype, "sesame", StringComparison.OrdinalIgnoreCase));
            GolineServiceCount = services.Count(s => string.Equals(s.servicetype, "goline", StringComparison.OrdinalIgnoreCase));
            IcontrolServiceCount = services.Count(s => string.Equals(s.servicetype, "icontrol", StringComparison.OrdinalIgnoreCase));

            CurrentOutages = currentOutageServiceIds.Count;

            UnhealthyChecksLastHour = await _db.ServiceHealthCheckLogs
                .Where(h => serviceIds.Contains(h.serviceId) && h.checkTime >= oneHourAgo && !h.isHealthy)
                .CountAsync();

            var avgResponse = await _db.ServiceHealthCheckLogs
                .Where(h => serviceIds.Contains(h.serviceId) && h.checkTime >= oneHourAgo && h.responseTimeMs != null)
                .AverageAsync(h => (double?)h.responseTimeMs);

            AverageResponseMsLastHour = avgResponse ?? 0;

            var logsLast7Days = await _db.ServiceHealthCheckLogs
                .Where(h => serviceIds.Contains(h.serviceId) && h.checkTime >= sevenDaysAgo)
                .Select(h => new { h.serviceId, h.isHealthy, h.responseTimeMs })
                .ToListAsync();

            ServiceUptimeCards = services
                .Select(s =>
                {
                    var serviceLogs = logsLast7Days.Where(l => l.serviceId == s.id).ToList();
                    var total = serviceLogs.Count;
                    var healthy = serviceLogs.Count(l => l.isHealthy);
                    var uptimePercent = total == 0 ? 0 : Math.Round((healthy * 100.0) / total, 2);
                    var avgResponse7d = serviceLogs
                        .Where(l => l.responseTimeMs.HasValue)
                        .Select(l => (double)l.responseTimeMs!.Value)
                        .DefaultIfEmpty(0)
                        .Average();

                    return new ServiceUptimeCard
                    {
                        ServiceId = s.id,
                        Hostname = s.hostname,
                        AppVersion = s.appversion,
                        ServiceType = s.servicetype,
                        ExcludeFromStats = s.excludefromstats,
                        ServiceUrl = BuildServiceUrl(s.protocol, s.hostname, s.port),
                        UptimePercent = uptimePercent,
                        AvgResponseMsLast7Days = Math.Round(avgResponse7d, 0),
                        TotalChecks = total,
                        HealthyChecks = healthy
                    };
                })
                .OrderBy(c => c.UptimePercent)
                .ToList();
        }

        private static string BuildServiceUrl(string? protocol, string hostname, int port)
        {
            var scheme = string.IsNullOrWhiteSpace(protocol) ? "http" : protocol;
            return $"{scheme}://{hostname}";
        }

        public class ServiceUptimeCard
        {
            public int ServiceId { get; set; }
            public string Hostname { get; set; } = string.Empty;
            public string? AppVersion { get; set; }
            public string? ServiceType { get; set; }
            public bool ExcludeFromStats { get; set; }
            public string ServiceUrl { get; set; } = string.Empty;
            public double UptimePercent { get; set; }
            public double AvgResponseMsLast7Days { get; set; }
            public int TotalChecks { get; set; }
            public int HealthyChecks { get; set; }
        }
    }
}
