using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using DeployFront.Pages.ServiceMap;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeployFront.Pages.Kpi
{
    public class OutagesModel : PageModel
    {
        private readonly ServiceMapDbContext _db;
        private readonly IAuthorizationService _authorizationService;

        public string Region { get; set; } = string.Empty;
        public string PeriodLabel { get; set; } = string.Empty;
        public List<OutageRow> Rows { get; set; } = new();

        [TempData]
        public string? SaveMessage { get; set; }

        public bool CanWrite { get; set; }

        public OutagesModel(ServiceMapDbContext db, IAuthorizationService authorizationService)
        {
            _db = db;
            _authorizationService = authorizationService;
        }

        public string CurrentSort { get; set; } = "";

        public int Year { get; set; }
        public int Month { get; set; }


        public async Task OnGetAsync(int year, int month, string region, string? sort)
        {
            CurrentSort = sort ?? "";

            Year = year;
            Month = month;


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
            CanWrite = (await _authorizationService.AuthorizeAsync(User, "CanWrite")).Succeeded;

            var activeVmMappings = await _db.VmIpMappings
                .Where(x => x.active)
                .ToDictionaryAsync(x => x.ipAddress, x => x.vmName);

            var services = await _db.ServiceMaps
                .Where(s => s.active)
                .Where(s=>!s.excludefromstats)
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

            var query = _db.Outages //await
                .Where(o => serviceIdsInRegion.Contains(o.serviceId)
                    && o.startTime >= start
                    && o.startTime < end);
            //.OrderByDescending(o => o.startTime)
            //.ToListAsync();

            query = sort switch
            {
                "failure_desc" => query.OrderByDescending(o => o.failureCount),
                "failure_asc" => query.OrderBy(o => o.failureCount),
                "start_desc" => query.OrderByDescending(o => o.startTime),
                "start_asc" => query.OrderBy(o => o.startTime),
                _ => query.OrderByDescending(o => o.startTime) // default
            };

            var outages = await query.ToListAsync();

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
                //.OrderByDescending(x => x.FailureCount)
                //.ThenByDescending(x => x.StartTime)
                .ToList();
        }

        public async Task<IActionResult> OnPostUpdateReasonAsync(long outageId, string? reason, int year, int month, string region)
        {
            if (!(await _authorizationService.AuthorizeAsync(User, "CanWrite")).Succeeded)
            {
                return Forbid();
            }

            var outage = await _db.Outages.FirstOrDefaultAsync(o => o.id == outageId);
            if (outage == null)
            {
                if (string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
                {
                    return new JsonResult(new { success = false, error = "Outage not found." }) { StatusCode = StatusCodes.Status404NotFound };
                }

                SaveMessage = "Outage not found.";
                return RedirectToPage(new { year, month, region });
            }

            outage.reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            _db.Outages.Update(outage);
            await _db.SaveChangesAsync();

            if (string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
            {
                return new JsonResult(new { success = true, message = "Outage reason saved." });
            }

            SaveMessage = "Outage reason saved.";
            return RedirectToPage(new { year, month, region });
        }

        public async Task<IActionResult> OnPostToggleExcludeAsync(long outageId, bool excluded, int year, int month, string region)
        {
            if (!(await _authorizationService.AuthorizeAsync(User, "CanWrite")).Succeeded)
            {
                return Forbid();
            }

            var outage = await _db.Outages.FirstOrDefaultAsync(o => o.id == outageId);
            if (outage == null)
            {
                if (string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
                {
                    return new JsonResult(new { success = false, error = "Outage not found." }) { StatusCode = StatusCodes.Status404NotFound };
                }

                SaveMessage = "Outage not found.";
                return RedirectToPage(new { year, month, region });
            }

            if (excluded && string.IsNullOrWhiteSpace(outage.reason))
            {
                const string errorMessage = "Please add a reason before excluding this outage.";
                if (string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
                {
                    return new JsonResult(new { success = false, error = errorMessage }) { StatusCode = StatusCodes.Status400BadRequest };
                }

                SaveMessage = errorMessage;
                return RedirectToPage(new { year, month, region });
            }

            outage.excluded = excluded;
            _db.Outages.Update(outage);
            await _db.SaveChangesAsync();

            var message = excluded ? "Outage excluded from uptime calculations." : "Outage included in uptime calculations.";

            if (string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
            {
                return new JsonResult(new { success = true, excluded, message });
            }

            SaveMessage = message;
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
