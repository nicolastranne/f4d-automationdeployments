using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using DeployFront.Pages.ServiceMap;
using Microsoft.AspNetCore.Mvc;

namespace DeployFront.Pages.Kpi
{
    public class OutagesModel : PageModel
    {
        private readonly ServiceMapDbContext _db;

        public string Region { get; set; } = string.Empty;
        public string PeriodLabel { get; set; } = string.Empty;
        public List<OutageRow> Rows { get; set; } = new();

        [TempData]
        public string? SaveMessage { get; set; }

        public OutagesModel(ServiceMapDbContext db)
        {
            _db = db;
        }

        public async Task OnGetAsync(int year, int month, string region)
        {
            if (year <= 0 || month is < 1 or > 12 || string.IsNullOrWhiteSpace(region))
            {
                return;
            }

            Region = region.Trim().ToUpperInvariant();
            if (Region is not ("AU" or "US" or "EU"))
            {
                return;
            }

            var start = new DateTime(year, month, 1);
            var end = start.AddMonths(1);
            PeriodLabel = start.ToString("MMMM yyyy");

            var activeVmMappings = await _db.VmIpMappings
                .Where(x => x.active)
                .ToDictionaryAsync(x => x.ipAddress, x => x.vmName);

            var services = await _db.ServiceMaps
                .Where(s => s.active)
                .Select(s => new { s.id, s.hostname, s.ipaddr, s.servicetype })
                .ToListAsync();

            var serviceIdsInRegion = services
                .Where(s => ResolveRegion(s.ipaddr, activeVmMappings) == Region)
                .Select(s => s.id)
                .ToList();

            if (serviceIdsInRegion.Count == 0)
            {
                return;
            }

            var outages = await _db.Outages
                .Where(o => serviceIdsInRegion.Contains(o.serviceId)
                    && o.startTime >= start
                    && o.startTime < end)
                .OrderByDescending(o => o.startTime)
                .ToListAsync();

            var serviceById = services.ToDictionary(s => s.id, s => s);

            Rows = outages
                .Select(o =>
                {
                    serviceById.TryGetValue(o.serviceId, out var service);

                    return new OutageRow
                    {
                        OutageId = o.id,
                        ServiceId = o.serviceId,
                        Hostname = service?.hostname ?? string.Empty,
                        ServiceType = service?.servicetype,
                        StartTime = o.startTime,
                        EndTime = o.endTime,
                        IsOngoing = o.isOngoing,
                        FailureCount = o.failureCount,
                        DurationSeconds = o.durationSeconds,
                        Reason = o.reason,
                        Excluded = o.excluded
                    };
                })
                .ToList();
        }

        public async Task<IActionResult> OnPostUpdateReasonAsync(long outageId, string? reason, int year, int month, string region)
        {
            var outage = await _db.Outages.FirstOrDefaultAsync(o => o.id == outageId);
            if (outage != null)
            {
                outage.reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
                _db.Outages.Update(outage);
                await _db.SaveChangesAsync();
                SaveMessage = "Outage reason saved.";
            }
            else
            {
                SaveMessage = "Outage not found.";
            }

            return RedirectToPage(new { year, month, region });
        }

        public async Task<IActionResult> OnPostToggleExcludeAsync(long outageId, bool excluded, int year, int month, string region)
        {
            var outage = await _db.Outages.FirstOrDefaultAsync(o => o.id == outageId);
            if (outage != null)
            {
                outage.excluded = excluded;
                _db.Outages.Update(outage);
                await _db.SaveChangesAsync();
                SaveMessage = excluded ? "Outage excluded from uptime calculations." : "Outage included in uptime calculations.";
            }
            else
            {
                SaveMessage = "Outage not found.";
            }

            return RedirectToPage(new { year, month, region });
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

        public class OutageRow
        {
            public long OutageId { get; set; }
            public int ServiceId { get; set; }
            public string Hostname { get; set; } = string.Empty;
            public string? ServiceType { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public bool IsOngoing { get; set; }
            public int FailureCount { get; set; }
            public int? DurationSeconds { get; set; }
            public string? Reason { get; set; }
            public bool Excluded { get; set; }
        }
    }
}
