using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using DeployFront.Pages.ServiceMap;

namespace DeployFront.Pages.Kpi
{
    public class IndexModel : PageModel
    {
        private readonly ServiceMapDbContext _db;

        public List<RegionMonthlyKpiRow> Rows { get; set; } = new();

        public IndexModel(ServiceMapDbContext db)
        {
            _db = db;
        }

        public async Task OnGetAsync()
        {
            var activeVmMappings = await _db.VmIpMappings
                .Where(x => x.active)
                .ToDictionaryAsync(x => x.ipAddress, x => x.vmName);

            var services = await _db.ServiceMaps
                .Where(s => s.active)
                .Select(s => new { s.id, s.ipaddr })
                .ToListAsync();

            var serviceRegionById = services
                .Select(s => new
                {
                    ServiceId = s.id,
                    Region = ResolveRegion(s.ipaddr, activeVmMappings)
                })
                .Where(x => x.Region != null)
                .ToDictionary(x => x.ServiceId, x => x.Region!);

            var serviceIds = serviceRegionById.Keys.ToList();
            if (serviceIds.Count == 0)
            {
                return;
            }

            var monthlyByService = await _db.ServiceHealthCheckLogs
                .Where(h => serviceIds.Contains(h.serviceId))
                .GroupBy(h => new { h.serviceId, h.checkTime.Year, h.checkTime.Month })
                .Select(g => new
                {
                    g.Key.serviceId,
                    g.Key.Year,
                    g.Key.Month,
                    Total = g.Count(),
                    Healthy = g.Count(x => x.isHealthy)
                })
                .ToListAsync();

            var perRegionMonth = monthlyByService
                .Select(x => new
                {
                    Region = serviceRegionById.TryGetValue(x.serviceId, out var region) ? region : null,
                    x.Year,
                    x.Month,
                    Uptime = x.Total == 0 ? 0 : (x.Healthy * 100.0) / x.Total
                })
                .Where(x => x.Region is "AU" or "US" or "EU")
                .GroupBy(x => new { x.Region, x.Year, x.Month })
                .Select(g => new
                {
                    g.Key.Region,
                    g.Key.Year,
                    g.Key.Month,
                    AverageUptime = g.Average(x => x.Uptime)
                })
                .ToList();

            var months = perRegionMonth
                .Select(x => new { x.Year, x.Month })
                .Distinct()
                .OrderByDescending(x => x.Year)
                .ThenByDescending(x => x.Month)
                .ToList();

            foreach (var month in months)
            {
                double? au = perRegionMonth
                    .Where(x => x.Region == "AU" && x.Year == month.Year && x.Month == month.Month)
                    .Select(x => (double?)x.AverageUptime)
                    .FirstOrDefault();

                double? us = perRegionMonth
                    .Where(x => x.Region == "US" && x.Year == month.Year && x.Month == month.Month)
                    .Select(x => (double?)x.AverageUptime)
                    .FirstOrDefault();

                double? eu = perRegionMonth
                    .Where(x => x.Region == "EU" && x.Year == month.Year && x.Month == month.Month)
                    .Select(x => (double?)x.AverageUptime)
                    .FirstOrDefault();

                Rows.Add(new RegionMonthlyKpiRow
                {
                    Year = month.Year,
                    Month = month.Month,
                    MonthLabel = new DateTime(month.Year, month.Month, 1).ToString("MMMM yyyy"),
                    AU = au,
                    US = us,
                    EU = eu
                });
            }
        }

        private static string? ResolveRegion(string ipAddress, IReadOnlyDictionary<string, string> vmMappings)
        {
            if (!vmMappings.TryGetValue(ipAddress, out var vmName) || string.IsNullOrWhiteSpace(vmName))
            {
                return null;
            }

            if (vmName.StartsWith("eau", StringComparison.OrdinalIgnoreCase)) return "AU";
            if (vmName.StartsWith("wus", StringComparison.OrdinalIgnoreCase)) return "US";
            if (vmName.StartsWith("neu", StringComparison.OrdinalIgnoreCase)) return "EU";

            return null;
        }

        public class RegionMonthlyKpiRow
        {
            public int Year { get; set; }
            public int Month { get; set; }
            public string MonthLabel { get; set; } = string.Empty;
            public double? AU { get; set; }
            public double? US { get; set; }
            public double? EU { get; set; }
        }
    }
}
