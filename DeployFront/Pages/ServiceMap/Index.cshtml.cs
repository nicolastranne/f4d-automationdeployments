using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using DeployFunc;

namespace DeployFront.Pages.ServiceMap
{
    public class IndexModel : PageModel
    {
        private readonly ServiceMapDbContext _db;
        public List<ServiceMap> ServiceMaps { get; set; } = new();

        public List<ServiceMapWithVm> ServiceMapsWithVm { get; set; } = new();
        [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }

        public IndexModel(ServiceMapDbContext db)
        {
            _db = db;
        }

        public async Task OnGetAsync()
        {
            var allMaps = await _db.ServiceMaps.ToListAsync();
            ServiceMapsWithVm = allMaps
                .Select(s => new ServiceMapWithVm
                {
                    ServiceMap = s,
                    VMName = GetVmName(s.ipaddr)
                })
                .Where(x => string.IsNullOrWhiteSpace(SearchTerm)
                    || (x.ServiceMap.hostname?.Contains(SearchTerm, System.StringComparison.OrdinalIgnoreCase) ?? false)
                    || (x.ServiceMap.appname?.Contains(SearchTerm, System.StringComparison.OrdinalIgnoreCase) ?? false)
                    || (x.ServiceMap.environment?.Contains(SearchTerm, System.StringComparison.OrdinalIgnoreCase) ?? false)
                    || (x.VMName.Contains(SearchTerm, System.StringComparison.OrdinalIgnoreCase))
                )
                .ToList();
        }

        public class ServiceMapWithVm
        {
            public ServiceMap ServiceMap { get; set; } = default!;
            public string VMName { get; set; } = string.Empty;
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
