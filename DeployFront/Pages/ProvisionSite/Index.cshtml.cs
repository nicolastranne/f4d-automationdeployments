using Azure.Core;
using Azure.Identity;
using DeployFront.Pages.ServiceMap;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DeployFront.Pages.ProvisionSite;

[Authorize(Policy = "CanWrite")]
public class IndexModel : PageModel
{
    private readonly ServiceMapDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<IndexModel> _logger;

    [BindProperty]
    public ProvisionSiteInputModel Input { get; set; } = new();

    public bool CanWrite { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public IndexModel(
        ServiceMapDbContext db,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IAuthorizationService authorizationService,
        ILogger<IndexModel> logger)
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
        if (!CanWrite)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Input.IsSetupNotification)) Input.IsSetupNotification = "true";
        if (string.IsNullOrWhiteSpace(Input.IsSetupFilemanager)) Input.IsSetupFilemanager = "false";
        if (string.IsNullOrWhiteSpace(Input.IrisTemplateDBName)) Input.IrisTemplateDBName = "Z-iris-masterdb-25-1-202501013";
        if (string.IsNullOrWhiteSpace(Input.ResourceGroupName)) Input.ResourceGroupName = "Q4D-ResourceGroup-1";
        if (string.IsNullOrWhiteSpace(Input.Subscription)) Input.Subscription = "Fleet4D";
        if (string.IsNullOrWhiteSpace(Input.KeyVaultName)) Input.KeyVaultName = "eau1-f4d-prod-kv-sql01";
        if (string.IsNullOrWhiteSpace(Input.CountryCode)) Input.CountryCode = "au";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        CanWrite = (await _authorizationService.AuthorizeAsync(User, "CanWrite")).Succeeded;
        if (!CanWrite)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct()
                .ToArray();

            ErrorMessage = errors.Length > 0
                ? $"Model is not valid: {string.Join(" | ", errors)}"
                : "Model is not valid.";

            return Page();
        }

        var baseUrl = _configuration["FunctionApp:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ErrorMessage = "Function App URL is not configured.";
            return Page();
        }

        var token = await GetFunctionAppAccessTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
        {
            ErrorMessage = "Function App scope/client ID is not configured.";
            return Page();
        }

       
        var payload = new
        {
            IsNewCustomer = Input.IsNewCustomer,
            IsSetupNotification = Input.IsSetupNotification,
            IsSetupFilemanager = Input.IsSetupFilemanager,
            RegionPrefix = Input.RegionPrefix,
            CountryCode = Input.CountryCode,
            CustomerName = Input.CustomerName,
            SiteName = Input.SiteName,
            Port = Input.Port,
            SourceAddressPrefix = Input.SourceAddressPrefix,
            MeshSubnet = Input.MeshSubnet,
            DestinationAddressPrefix = Input.DestinationAddressPrefix,
            ParentZoneName = Input.ParentZoneName,
            IrisTemplateDBName = Input.IrisTemplateDBName,
            SesameTemplateDBName = Input.SesameTemplateDBName,
            SiteCode = Input.SiteCode,
            ResourceGroupName = Input.ResourceGroupName,
            Subscription = Input.Subscription,
            KeyVaultName = Input.KeyVaultName
        };

      
        var client = _httpClientFactory.CreateClient();

        var requestUrl = $"{baseUrl}/trigger-runbook-f4dSiteDeployment";
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
                createdAt = DateTime.UtcNow,
                userName = User.FindFirst("preferred_username")?.Value ?? User.Identity?.Name,
            });

            await _db.SaveChangesAsync();
        }
      

        using (var insertRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/insertfileshareentry")
        {
            Content = JsonContent.Create(payload)
        })
        {
            insertRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var insertResponse = await client.SendAsync(insertRequest);
            var insertBody = await insertResponse.Content.ReadAsStringAsync();

            if (!insertResponse.IsSuccessStatusCode)
            {
                ErrorMessage = $"Failed to insert file share entry: {(int)insertResponse.StatusCode} {insertResponse.ReasonPhrase}. {insertBody}";
                return Page();
            }
        }


        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        await LogUpgradeActionAsync((int)response.StatusCode, body);

        if (!response.IsSuccessStatusCode)
        {
            ErrorMessage = $"Failed to deploy infra runbook: {(int)response.StatusCode} {response.ReasonPhrase}. {body}";
            return Page();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var jobId = root.TryGetProperty("jobId", out var jobIdProp) ? jobIdProp.GetString() : null;
            StatusMessage = string.IsNullOrWhiteSpace(jobId)
                ? "Deploy infra runbook submitted successfully."
                : $"Deploy infra runbook submitted successfully. Job ID: {jobId}";
        }
        catch
        {
            StatusMessage = "Deploy infra runbook submitted successfully.";
        }

        return RedirectToPage();
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
            var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { scope }));
            return token.Token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to acquire Function App access token for provision trigger.");
            return null;
        }
    }

    public class ProvisionSiteInputModel
    {
        [Required]
        public string IsNewCustomer { get; set; } = string.Empty;

        public string? IsSetupNotification { get; set; } = "true";

        public string? IsSetupFilemanager { get; set; } = "false";

        public string? RegionPrefix { get; set; } = string.Empty;
        public string? CountryCode { get; set; } = string.Empty;

        [Required]
        public string CustomerName { get; set; } = string.Empty;

        [Required]
        public string SiteName { get; set; } = string.Empty;

        [Required]
        public string Port { get; set; } = string.Empty;

        [Required]
        public string SourceAddressPrefix { get; set; } = string.Empty;

        public string? MeshSubnet { get; set; } = string.Empty;

        [Required]
        public string DestinationAddressPrefix { get; set; } = string.Empty;

        [Required]
        public string ParentZoneName { get; set; } = string.Empty;

        public string? IrisTemplateDBName { get; set; } = "Z-iris-masterdb-25-1-202501013";

        public string? SesameTemplateDBName { get; set; } = string.Empty;

        [Required]
        public string SiteCode { get; set; } = string.Empty;

        public string? ResourceGroupName { get; set; } = "Q4D-ResourceGroup-1";

        public string? Subscription { get; set; } = "Fleet4D";

        public string? KeyVaultName { get; set; } = "eau1-f4d-prod-kv-sql01";
    }
}
