using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Azure.Core;
using Azure.Identity;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using DeployFunc;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace DeployFront.Pages.ServiceMap
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly ServiceMapDbContext _db;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly IAuthorizationService _authorizationService;
        private readonly ILogger<IndexModel> _logger;
        public List<ServiceMap> ServiceMaps { get; set; } = new();

        public List<ServiceMapWithVm> ServiceMapsWithVm { get; set; } = new();
        public Dictionary<string, string> LatestAppVersions { get; set; } = new();
        public Dictionary<string, List<string>> AvailableVersions { get; set; } = new();
        public string DefaultSqlServer { get; set; } = string.Empty;
        public string DefaultDatabase { get; set; } = string.Empty;
        [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }

        [TempData]
        public string? ForceSyncMessage { get; set; }

        public bool CanWrite { get; set; }

        public IndexModel(ServiceMapDbContext db, IHttpClientFactory httpClientFactory, IConfiguration configuration, IAuthorizationService authorizationService, ILogger<IndexModel> logger)
        {
            _db = db;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _authorizationService = authorizationService;
            _logger = logger;
        }

        public async Task OnGetAsync()
        {
            CanWrite = (await _authorizationService.AuthorizeAsync(User, "CanWrite")).Succeeded;

            var allMaps = await _db.ServiceMaps.ToListAsync();
            var vmMappings = await _db.VmIpMappings
                .Where(x => x.active)
                .ToDictionaryAsync(x => x.ipAddress, x => x.vmName);
            // Get latest appversion for each servicetype from ServiceVersion table
            var allVersions = await _db.ServiceVersions
                .Where(v => v.active && v.appversion != null && v.servicetype != null)
                .ToListAsync();
            LatestAppVersions = allVersions
                .GroupBy(v => v.servicetype)
                .Select(g => new { ServiceType = g.Key, Latest = g.Max(x => x.appversion) })
                .ToDictionary(x => x.ServiceType!, x => x.Latest!, StringComparer.OrdinalIgnoreCase);

            // Build available versions dictionary for each servicetype
            AvailableVersions = allVersions
                .GroupBy(v => v.servicetype!)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.appversion!).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );

            DefaultSqlServer = _configuration["UpgradeDefaults:SqlServer"] ?? string.Empty;
            DefaultDatabase = _configuration["UpgradeDefaults:Database"] ?? string.Empty;

            ServiceMapsWithVm = allMaps
                .Select(s => {
                    var vmName = GetVmName(s.ipaddr, vmMappings);
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
                    || (x.ServiceMap.customer?.Contains(SearchTerm, System.StringComparison.OrdinalIgnoreCase) ?? false)
                    || (x.ServiceMap.site?.Contains(SearchTerm, System.StringComparison.OrdinalIgnoreCase) ?? false)
                    || (x.ServiceMap.port.ToString().Contains(SearchTerm, StringComparison.OrdinalIgnoreCase))
                )
                .OrderBy(x => x.ServiceMap.customer)
                .ThenBy(x => x.ServiceMap.hostname)
                .ToList();
        }

        public async Task<IActionResult> OnPostForceSyncAsync()
        {
            if (!(await _authorizationService.AuthorizeAsync(User, "CanWrite")).Succeeded)
            {
                return Forbid();
            }

            var baseUrl = _configuration["FunctionApp:BaseUrl"]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                ForceSyncMessage = "Function App URL is not configured.";
                return RedirectToPage();
            }

            var token = await GetFunctionAppAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                ForceSyncMessage = "Function App scope/client ID is not configured.";
                return RedirectToPage();
            }

            var client = _httpClientFactory.CreateClient();

            using var upsertRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/upsertservicemap");
            upsertRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var upsertResponse = await client.SendAsync(upsertRequest);
            if (!upsertResponse.IsSuccessStatusCode)
            {
                ForceSyncMessage = $"Force Sync failed on upsert: {upsertResponse.StatusCode}";
                return RedirectToPage();
            }

            using var versionRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/updateirisappversions");
            versionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var versionResponse = await client.SendAsync(versionRequest);
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
            if (!(await _authorizationService.AuthorizeAsync(User, "CanWrite")).Succeeded)
                return new JsonResult(new { success = false, error = "Forbidden" }) { StatusCode = StatusCodes.Status403Forbidden };

            if (model == null)
                return new JsonResult(new { success = false, error = "Invalid data" });
            var entity = await _db.ServiceMaps.FirstOrDefaultAsync(x => x.id == model.id);
            if (entity == null)
                return new JsonResult(new { success = false, error = "Not found" });
            entity.customer = model.customer;
            entity.site = model.site;
            entity.irishostname = model.irishostname;
            entity.notes = model.notes;
            entity.active = model.active;
            entity.excludefromstats = model.excludefromstats;
            await _db.SaveChangesAsync();
            return new JsonResult(new { success = true });
        }

        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> OnPostUpgradeAsync([FromBody] UpgradeRequestModel model)
        {
            if (!(await _authorizationService.AuthorizeAsync(User, "CanWrite")).Succeeded)
                return new JsonResult(new { success = false, error = "Forbidden" }) { StatusCode = StatusCodes.Status403Forbidden };

            if (model == null
                || model.id <= 0
                || string.IsNullOrWhiteSpace(model.serviceType)
                || string.IsNullOrWhiteSpace(model.targetVersion)
                || string.IsNullOrWhiteSpace(model.destinationAddressPrefix)
                || string.IsNullOrWhiteSpace(model.serviceName)
                || (!(string.Equals(model.serviceType, "sesame", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(model.serviceType, "goline", StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrWhiteSpace(model.sqlServer)
                        || string.IsNullOrWhiteSpace(model.database))))
                return new JsonResult(new { success = false, error = "Invalid data" });

            var entity = await _db.ServiceMaps.FirstOrDefaultAsync(x => x.id == model.id);
            if (entity == null)
                return new JsonResult(new { success = false, error = "Service not found" });

            var baseUrl = _configuration["FunctionApp:BaseUrl"]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
                return new JsonResult(new { success = false, error = "Function App URL is not configured." });

            var token = await GetFunctionAppAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                return new JsonResult(new { success = false, error = "Function App scope/client ID is not configured." });

            var client = _httpClientFactory.CreateClient();
            var payload = new
            {
                DestinationAddressPrefix = model.destinationAddressPrefix,
                Version = model.targetVersion,
                ServiceName = model.serviceName,
                ServiceType = model.serviceType,
                SqlServer = model.sqlServer,
                Database = model.database,
                ForceDownload = model.forceDownload
            };

            var requestUrl = $"{baseUrl}/trigger-runbook-f4dupdateservices";
            var requestBody = JsonSerializer.Serialize(payload);

            async Task LogUpgradeActionAsync(int? statusCode, string? resultBody)
            {
                _db.UpgradeActionLogs.Add(new UpgradeActionLog
                {
                    requestUrl = requestUrl,
                    requestType = HttpMethod.Post.Method,
                    requestBody = requestBody,
                    result = resultBody,
                    statusCode = statusCode,
                    createdAt = DateTime.UtcNow
                });

                await _db.SaveChangesAsync();
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            await LogUpgradeActionAsync((int)response.StatusCode, body);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return new JsonResult(new
                {
                    success = false,
                    error = "Unauthorized to call Function App.",
                    details = body,
                    hint = "Set FunctionApp:Scope to the exact API audience/.default expected by Function App auth (for example api://<function-app-app-registration-client-id>/.default)."
                });
            }

            if (!response.IsSuccessStatusCode)
            {
                return new JsonResult(new
                {
                    success = false,
                    error = "Failed to trigger runbook.",
                    details = body
                });
            }

            object? responseData = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    responseData = JsonSerializer.Deserialize<object>(body);
                }
                catch
                {
                    responseData = body;
                }
            }

            return new JsonResult(new
            {
                success = true,
                message = "Runbook trigger submitted.",
                runbookResponse = responseData
            });
        }

        public async Task<IActionResult> OnGetAuthDiagnosticsAsync()
        {
            if (!(await _authorizationService.AuthorizeAsync(User, "CanWrite")).Succeeded)
                return Forbid();

            var scope = _configuration["FunctionApp:Scope"];
            if (string.IsNullOrWhiteSpace(scope))
            {
                var clientId = _configuration["FunctionApp:ClientID"] ?? _configuration["FunctionApp:ClientId"];
                scope = string.IsNullOrWhiteSpace(clientId) ? null : $"api://{clientId}/.default";
            }

            if (string.IsNullOrWhiteSpace(scope))
            {
                return new JsonResult(new
                {
                    success = false,
                    error = "FunctionApp:Scope or FunctionApp:ClientID is missing."
                });
            }

            var diagnostics = new List<object>();
            AccessToken? acquired = null;

            async Task TryCredentialAsync(string name, TokenCredential credential)
            {
                try
                {
                    var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { scope! }), default);
                    diagnostics.Add(new { credential = name, success = true, expiresOn = token.ExpiresOn });
                    acquired ??= token;
                }
                catch (Exception ex)
                {
                    diagnostics.Add(new { credential = name, success = false, error = ex.Message });
                }
            }

            await TryCredentialAsync("EnvironmentCredential", new EnvironmentCredential());
            await TryCredentialAsync("ManagedIdentityCredential", new ManagedIdentityCredential());
            await TryCredentialAsync("VisualStudioCredential", new VisualStudioCredential());
            await TryCredentialAsync("AzureCliCredential", new AzureCliCredential());
            await TryCredentialAsync("AzurePowerShellCredential", new AzurePowerShellCredential());

            string? aud = null;
            string? tid = null;
            if (acquired.HasValue)
            {
                try
                {
                    var parts = acquired.Value.Token.Split('.');
                    if (parts.Length >= 2)
                    {
                        var payload = parts[1].Replace('-', '+').Replace('_', '/');
                        var padding = 4 - (payload.Length % 4);
                        if (padding < 4) payload += new string('=', padding);
                        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("aud", out var audProp)) aud = audProp.GetString();
                        if (doc.RootElement.TryGetProperty("tid", out var tidProp)) tid = tidProp.GetString();
                    }
                }
                catch
                {
                }
            }

            return new JsonResult(new
            {
                success = acquired.HasValue,
                requestedScope = scope,
                tokenAudience = aud,
                tenantId = tid,
                credentials = diagnostics
            });
        }

        public class EditFieldsModel
        {
            public int id { get; set; }
            public string? customer { get; set; }
            public string? site { get; set; }
            public string? irishostname { get; set; }
            public string? notes { get; set; }
            public bool active { get; set; }
            public bool excludefromstats { get; set; }
        }

        public class UpgradeRequestModel
        {
            public int id { get; set; }
            public string? serviceType { get; set; }
            public string? targetVersion { get; set; }
            public string? destinationAddressPrefix { get; set; }
            public string? serviceName { get; set; }
            public string? sqlServer { get; set; }
            public string? database { get; set; }
            public bool forceDownload { get; set; } = true;
        }

        public class ServiceMapWithVm
        {
            public ServiceMap ServiceMap { get; set; } = default!;
            public string VMName { get; set; } = string.Empty;
            public string Region { get; set; } = string.Empty;
        }

        private string GetVmName(string ip, Dictionary<string, string> vmMappings)
        {
            return vmMappings.TryGetValue(ip, out var vmName) ? vmName : string.Empty;
        }

        private async Task<string?> GetFunctionAppAccessTokenAsync()
        {
            try
            {
                var scope = _configuration["FunctionApp:Scope"];
                if (string.IsNullOrWhiteSpace(scope))
                {
                    var clientId = _configuration["FunctionApp:ClientID"] ?? _configuration["FunctionApp:ClientId"];
                    if (string.IsNullOrWhiteSpace(clientId))
                    {
                        return null;
                    }

                    scope = $"api://{clientId}/.default";
                }

                var credential = new DefaultAzureCredential();
                var token = await credential.GetTokenAsync(
                    new TokenRequestContext(new[]
                    {
                        scope
                    }));

                return token.Token;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to acquire Function App access token.");
                return null;
            }
        }
    }
}
