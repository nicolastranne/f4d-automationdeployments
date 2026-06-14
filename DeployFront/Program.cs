using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.AspNetCore.HttpOverrides;

var webApplicationOptions = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
        ? AppContext.BaseDirectory
        : Directory.GetCurrentDirectory()
};

var builder = WebApplication.CreateBuilder(webApplicationOptions);
builder.Host.UseWindowsService();
var requiredGroupId = builder.Configuration["Authorization:RequiredGroupId"]?.Trim();
var host = builder.Configuration["Hosting:Host"]?.Trim();
var port = builder.Configuration.GetValue<int?>("Hosting:Port") ?? 7010;
var aspNetCoreUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");

if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(aspNetCoreUrls))
{
    builder.WebHost.UseUrls($"http://{(string.IsNullOrWhiteSpace(host) ? "localhost" : host)}:{port}");
}

if (string.IsNullOrWhiteSpace(requiredGroupId))
{
    throw new InvalidOperationException("Authorization:RequiredGroupId is not configured.");
}

// Add services to the container.
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
{
    options.ResponseType = "code";
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireF4DAutomationGroup", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            var groupClaimTypes = new[]
            {
                "groups",
                "http://schemas.microsoft.com/ws/2008/06/identity/claims/groups"
            };

            return context.User.Claims.Any(c =>
                groupClaimTypes.Contains(c.Type, StringComparer.OrdinalIgnoreCase)
                && string.Equals(c.Value?.Trim(), requiredGroupId, StringComparison.OrdinalIgnoreCase));
        });
    });
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/", "RequireF4DAutomationGroup");
});
builder.Services.AddHttpClient();
// Register EF Core DbContext
builder.Services.AddDbContext<DeployFront.Pages.ServiceMap.ServiceMapDbContext>(options =>
options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"))

);

var app = builder.Build();

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseForwardedHeaders(forwardedHeadersOptions);
app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.Run();
