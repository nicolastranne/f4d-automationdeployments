using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using DeployFunc;
using System.Net.Http;

namespace DeployFront.Pages.ServiceMap
{
    public class IndexModel : PageModel
    {
        private readonly ServiceMapDbContext _db;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        public List<ServiceMap> ServiceMaps { get; set; } = new();

        public List<ServiceMapWithVm> ServiceMapsWithVm { get; set; } = new();
        public Dictionary<string, string> LatestAppVersions { get; set; } = new();
        public Dictionary<string, List<string>> AvailableVersions { get; set; } = new();
        [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }

        [TempData]
        public string? ForceSyncMessage { get; set; }

        public IndexModel(ServiceMapDbContext db, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _db = db;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        public async Task OnGetAsync()
        {
            var allMaps = await _db.ServiceMaps.ToListAsync();
            // Get latest appversion for each servicetype from ServiceVersion table
            var allVersions = await _db.ServiceVersions
                .Where(v => v.active && v.appversion != null && v.servicetype != null)
                .ToListAsync();
            LatestAppVersions = allVersions
                .GroupBy(v => v.servicetype)
                .Select(g => new { ServiceType = g.Key, Latest = g.Max(x => x.appversion) })
                .ToDictionary(x => x.ServiceType!, x => x.Latest!);

            // Build available versions dictionary for each servicetype
            AvailableVersions = allVersions
                .GroupBy(v => v.servicetype!)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.appversion!).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
                );

            ServiceMapsWithVm = allMaps
                .Select(s => {
                    var vmName = GetVmName(s.ipaddr);
                    var region = string.Empty;
                    if (!string.IsNullOrEmpty(vmName))
                    {
                        if (vmName.StartsWith("eau", System.StringComparison.OrdinalIgnoreCase)) region = "AU";
                        else if (vmName.StartsWith("neu", System.StringComparison.OrdinalIgnoreCase)) region = "EU";
                        else if (vmName.StartsWith("wus", System.StringComparison.OrdinalIgnoreCase)) region = "US";
                    }
                    return new ServiceMapWithVm
                    {
                        ServiceMap = s,
                        VMName = vmName,
                        Region = region
                    };
                })
                .Where(x => string.IsNullOrWhiteSpace(SearchTerm)
                    || (x.ServiceMap.hostname?.Contains(SearchTerm, System.StringComparison.OrdinalIgnoreCase) ?? false)
                    || (x.ServiceMap.appname?.Contains(SearchTerm, System.StringComparison.OrdinalIgnoreCase) ?? false)
                    || (x.ServiceMap.environment?.Contains(SearchTerm, System.StringComparison.OrdinalIgnoreCase) ?? false)
                    || (x.VMName.Contains(SearchTerm, System.StringComparison.OrdinalIgnoreCase))
                    || (x.Region.Contains(SearchTerm, System.StringComparison.OrdinalIgnoreCase))
                )
                .ToList();
        }

        public async Task<IActionResult> OnPostForceSyncAsync()
        {
            var baseUrl = _configuration["FunctionApp:BaseUrl"]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                ForceSyncMessage = "Function App URL is not configured.";
                return RedirectToPage();
            }

            var client = _httpClientFactory.CreateClient();

            var upsertResponse = await client.GetAsync($"{baseUrl}/upsertservicemap");
            if (!upsertResponse.IsSuccessStatusCode)
            {
                ForceSyncMessage = $"Force Sync failed on upsert: {upsertResponse.StatusCode}";
                return RedirectToPage();
            }

            var versionResponse = await client.GetAsync($"{baseUrl}/updateirisappversions");
            if (!versionResponse.IsSuccessStatusCode)
            {
                ForceSyncMessage = $"Force Sync failed on version update: {versionResponse.StatusCode}";
                return RedirectToPage();
            }

            ForceSyncMessage = "Force Sync completed successfully.";
            return RedirectToPage();
        }

        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> OnPostEditFieldsAsync([FromBody] EditFieldsModel model)
        {
            if (model == null)
                return new JsonResult(new { success = false, error = "Invalid data" });
            var entity = await _db.ServiceMaps.FirstOrDefaultAsync(x => x.id == model.id);
            if (entity == null)
                return new JsonResult(new { success = false, error = "Not found" });
            entity.customer = model.customer;
            entity.notes = model.notes;
            await _db.SaveChangesAsync();
            return new JsonResult(new { success = true });
        }

        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> OnPostUpgradeAsync([FromBody] UpgradeRequestModel model)
        {
            if (model == null || model.id <= 0 || string.IsNullOrWhiteSpace(model.targetVersion))
                return new JsonResult(new { success = false, error = "Invalid data" });

            var entity = await _db.ServiceMaps.FirstOrDefaultAsync(x => x.id == model.id);
            if (entity == null)
                return new JsonResult(new { success = false, error = "Service not found" });

            return new JsonResult(new
            {
                success = true,
                message = "Upgrade request received. No action executed.",
                service = new
                {
                    entity.id,
                    entity.hostname,
                    entity.ipaddr,
                    entity.port,
                    entity.appname,
                    entity.appversion,
                    entity.environment,
                    entity.servicetype,
                    entity.customer,
                    entity.notes,
                    entity.active,
                    entity.modified
                },
                requestedVersion = model.targetVersion
            });
        }

        public class EditFieldsModel
        {
            public int id { get; set; }
            public string? customer { get; set; }
            public string? notes { get; set; }
        }

        public class UpgradeRequestModel
        {
            public int id { get; set; }
            public string? targetVersion { get; set; }
        }

        public class ServiceMapWithVm
        {
            public ServiceMap ServiceMap { get; set; } = default!;
            public string VMName { get; set; } = string.Empty;
            public string Region { get; set; } = string.Empty;
        }

        private string GetVmName(string ip)
        {
            return ip switch
            {
                "10.254.1.8" => "eau1-f4d-prod-vm-07-emecoappsrv01",
                "10.254.1.15" => "eau1-f4d-qa-vm-12-appsrv01",
                "10.250.32.6" => "eau1-f4d-test-vm-appsrv02",
                "10.254.1.7" => "eau1-f4d-prod-vm-02-appsrv02",
                "10.254.1.9" => "eau1-f4d-prod-vm-08-appsrv03",
                "10.254.1.4" => "eau1-f4d-prod-vm-10-appsrv04",
                "10.250.16.4" => "eau1-f4d-prod-vm-appsrv05",
                "10.250.16.6" => "eau1-f4d-prod-vm-appsrv06",
                "10.250.16.7" => "eau1-f4d-prod-vm-appsrv07",
                "10.250.16.9" => "eau1-f4d-prod-vm-appsrv08",
                "10.248.16.4" => "neu1-f4d-prod-vm-appsrv01",
                "10.254.1.11" => "wus3-f4d-prod-vm-09-appsrv01",
                "10.250.16.5" => "wus3-f4d-prod-vm-appsrv03",
                "10.250.16.8" => "wus3-f4d-prod-vm-appsrv04",
                "10.249.16.4" => "wus3-f4d-prod-vm-appsrv05",
                // Add more mappings here as needed
                _ => string.Empty
            };
        }
    }
}
