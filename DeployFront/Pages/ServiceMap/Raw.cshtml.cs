using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Net.Http.Headers;

namespace DeployFront.Pages.ServiceMap;

[Authorize]
public class RawModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RawModel> _logger;

    public string RawContent { get; private set; } = string.Empty;
    public string? ErrorMessage { get; private set; }

    public RawModel(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<RawModel> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task OnGetAsync()
    {
        var baseUrl = _configuration["FunctionApp:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ErrorMessage = "Function App URL is not configured.";
            return;
        }

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/readfileshare");

        var token = await GetFunctionAppAccessTokenAsync();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                ErrorMessage = $"Failed to read file share content: {(int)response.StatusCode} {response.ReasonPhrase}";
                RawContent = body;
                return;
            }

            RawContent = body;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve raw service map content.");
            ErrorMessage = "Unexpected error while reading file share content.";
        }
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
            _logger.LogWarning(ex, "Failed to acquire Function App access token for raw file read.");
            return null;
        }
    }
}
