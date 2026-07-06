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
                .Where(s => !s.excludefromstats)
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

            var monthWindows = monthlyByService
                .Select(x => new { x.Year, x.Month })
                .Distinct()
                .ToList();

            var monthlyAdjustmentsByService = new Dictionary<(int serviceId, int Year, int Month), int>();
            foreach (var window in monthWindows)
            {
                var monthStart = new DateTime(window.Year, window.Month, 1);
                var monthEnd = monthStart.AddMonths(1);

                var overlaps = await _db.Outages
                    .Where(o => serviceIds.Contains(o.serviceId)
                        && o.startTime < monthEnd
                        && (o.endTime == null || o.endTime > monthStart)
                        && (o.excluded || o.startTime < monthStart))
                    .Select(o => new { o.serviceId, o.startTime, o.endTime })
                    .ToListAsync();

                foreach (var group in overlaps.GroupBy(x => x.serviceId))
                {
                    var seconds = group.Sum(o => CalculateOverlapSeconds(o.startTime, o.endTime, monthStart, monthEnd));
                    monthlyAdjustmentsByService[(group.Key, window.Year, window.Month)] = seconds;
                }
            }

            var perRegionMonth = monthlyByService
                .Select(x => new
                {
                    Region = serviceRegionById.TryGetValue(x.serviceId, out var region) ? region : null,
                    x.Year,
                    x.Month,
                    Uptime = CalculateAdjustedUptimePercent(
                        x.Total,
                        x.Healthy,
                        monthlyAdjustmentsByService.TryGetValue((x.serviceId, x.Year, x.Month), out var adjustmentSeconds)
                            ? adjustmentSeconds
                            : 0)
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

        private static double CalculateAdjustedUptimePercent(int totalChecks, int healthyChecks, int excludedOutageSeconds)
        {
            if (totalChecks <= 0)
            {
                return 0;
            }

            var unhealthyChecks = Math.Max(0, totalChecks - healthyChecks);
            var estimatedExcludedChecks = Math.Max(0, (int)Math.Round(excludedOutageSeconds / 180.0, MidpointRounding.AwayFromZero));
            var adjustedUnhealthyChecks = Math.Max(0, unhealthyChecks - estimatedExcludedChecks);
            var adjustedHealthyChecks = totalChecks - adjustedUnhealthyChecks;

            return (adjustedHealthyChecks * 100.0) / totalChecks;
        }

        private static int CalculateOverlapSeconds(DateTime outageStart, DateTime? outageEnd, DateTime windowStart, DateTime windowEnd)
        {
            var effectiveStart = outageStart > windowStart ? outageStart : windowStart;
            var effectiveEnd = (outageEnd ?? windowEnd) < windowEnd ? (outageEnd ?? windowEnd) : windowEnd;
            if (effectiveEnd <= effectiveStart)
            {
                return 0;
            }

            return (int)(effectiveEnd - effectiveStart).TotalSeconds;
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
