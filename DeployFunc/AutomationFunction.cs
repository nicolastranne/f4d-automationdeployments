using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.Automation.Models;
using Azure.Core;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;


namespace DeployFunc
{
    public class AutomationFunction
    {
        private readonly ILogger<AutomationFunction> _logger;
        private readonly ServiceMapDbContext _db;
        private readonly string? _subscriptionId;
        private readonly string? _automationResourceGroup;
        private readonly string? _automationAccount;
        private static readonly HttpClient _httpClient = new HttpClient();

        public AutomationFunction(ILogger<AutomationFunction> logger, ServiceMapDbContext db)
        {
            _logger = logger;
            _db = db;
            _subscriptionId = Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");
            _automationResourceGroup = Environment.GetEnvironmentVariable("AUTOMATION_RESOURCE_GROUP");
            _automationAccount = Environment.GetEnvironmentVariable("AUTOMATION_ACCOUNT");
        }

        [Function("Test")]
        public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");
            return new OkObjectResult("Welcome to Azure Functions");
        }

        // HTTP trigger to read Azure File Share
        [Function("FileShareReaderFunction")]
        public async Task<IActionResult> RunFileShareReaderHttpAsync(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "readfileshare")] HttpRequest req)
        {
            string connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            string shareName = Environment.GetEnvironmentVariable("FileShareName") ?? "gwshareddata";
            string directoryName = Environment.GetEnvironmentVariable("FileShareDirectory") ?? "etc/haproxy/maps";
            string fileName = Environment.GetEnvironmentVariable("FileShareFileName") ?? "services.map";

            try
            {

                var share = new ShareClient(connectionString, shareName);

                var file = share
                    .GetDirectoryClient(directoryName)
                    .GetFileClient(fileName);

                if (await file.ExistsAsync())
                {
                    var download = await file.DownloadAsync();

                    using var reader = new System.IO.StreamReader(download.Value.Content);
                    var lines = new List<string>();

                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync();
                        if (line != null && !line.TrimStart().StartsWith("#"))
                            lines.Add(line);
                    }

                    string content = string.Join("\n", lines);

                    return new OkObjectResult(content);
                }
                else
                {
                    return new NotFoundObjectResult($"File not found: {fileName}");
                }
            }
            catch (Exception ex)
            {
                return new ObjectResult($"Error reading from Azure File Share: {ex.Message}")
                {
                    StatusCode = 500
                };
            }

        }

        // HTTP trigger to upsert ServiceMap table from file share using EF Core
        [Function("FileShareToServiceMapDbFunction")]
        public async Task<IActionResult> RunFileShareToServiceMapDbAsync(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "upsertservicemap")] HttpRequest req)
        {
            return await RunFileShareToServiceMapDb_Request();
        }

        public async Task<IActionResult> RunFileShareToServiceMapDb_Request()
        {
            string storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            string shareName = Environment.GetEnvironmentVariable("FileShareName") ?? "myfileshare";
            string directoryName = Environment.GetEnvironmentVariable("FileShareDirectory") ?? "";
            string fileName = Environment.GetEnvironmentVariable("FileShareFileName") ?? "services.map";

            try
            {
                var share = new ShareClient(storageConnectionString, shareName);
                var directory = share.GetDirectoryClient(directoryName);
                var file = directory.GetFileClient(fileName);
                if (!await file.ExistsAsync())
                {
                    string msg = $"File not found: {fileName}";
                    _logger.LogWarning(msg);
                    return new NotFoundObjectResult(msg);
                }

                var download = await file.DownloadAsync();
                var lines = new System.Collections.Generic.List<string>();
                using (var reader = new System.IO.StreamReader(download.Value.Content))
                {
                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync();
                        if (line != null && !line.TrimStart().StartsWith("#"))
                            lines.Add(line);
                    }
                }

                int upserted = 0;
                foreach (var line in lines)
                {
                    // Example row: hbmbh-ireport.site.smartquarry.komatsu http://10.250.16.4:21080
                    var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    string hostname = parts[0];
                    string ipAndPort = parts[1];
                    string protocol = "http";
                    string ipaddr = "";
                    int port = 0;
                    string appname = hostname;
                    //string appversion = null;
                    //string customer = null;
                    string? servicetype = hostname.Contains("iris", StringComparison.OrdinalIgnoreCase) ? "iris"
                        : hostname.Contains("goline", StringComparison.OrdinalIgnoreCase) ? "goline"
                        : hostname.Contains("identity", StringComparison.OrdinalIgnoreCase) ? "sesame"
                        : hostname.Contains("icontrol", StringComparison.OrdinalIgnoreCase) ? "icontrol"
                        : hostname.Contains("ireport", StringComparison.OrdinalIgnoreCase) ? "ireport"
                        : hostname.Contains("maps", StringComparison.OrdinalIgnoreCase) ? "maps"
                        : hostname.Contains("filemanager", StringComparison.OrdinalIgnoreCase) ? "filemanager"
                        : null;
                    string environment = hostname.Contains("demo", StringComparison.OrdinalIgnoreCase) ? "demo"
                        : hostname.Contains("test", StringComparison.OrdinalIgnoreCase) ? "test"
                        : "prod";
                    bool active = true;
                    DateTime modified = DateTime.UtcNow;
                    //string notes = null;

                    // Parse protocol, ipaddr, port from ipAndPort
                    try
                    {
                        var uri = new Uri(ipAndPort);
                        protocol = uri.Scheme;
                        ipaddr = uri.Host;
                        port = uri.Port;
                    }
                    catch
                    {
                        // fallback: try to split manually
                        var ipParts = ipAndPort.Replace("http://","").Replace("https://","").Split(':');
                        if (ipParts.Length == 2)
                        {
                            ipaddr = ipParts[0];
                            int.TryParse(ipParts[1], out port);
                        }
                    }

                    // Upsert using EF Core
                    var existing = await _db.ServiceMaps.FirstOrDefaultAsync(x => x.hostname == hostname);
                    if (existing != null)
                    {
                        existing.protocol = protocol;
                        existing.ipaddr = ipaddr;
                        existing.port = port;
                        existing.appname = appname;
                        //existing.appversion = appversion;
                        //existing.customer = customer;
                        existing.environment = environment;
                        existing.servicetype = servicetype;
                        existing.active = active;
                        existing.modified = modified;
                        //existing.notes = notes;
                        _db.ServiceMaps.Update(existing);
                    }
                    else
                    {
                        _db.ServiceMaps.Add(new ServiceMap
                        {
                            protocol = protocol,
                            hostname = hostname,
                            ipaddr = ipaddr,
                            port = port,
                            appname = appname,
                            //appversion = appversion,
                            //customer = customer,
                            environment = environment,
                            servicetype = servicetype,
                            active = active,
                            modified = modified,
                            //notes = notes
                        });
                    }
                    upserted++;
                }
                await _db.SaveChangesAsync();
                return new OkObjectResult($"Upserted {upserted} rows into ServiceMap.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error upserting ServiceMap from Azure File Share");
                return new ObjectResult($"Error upserting ServiceMap: {ex.Message}") { StatusCode = 500 };
            }
        }

        [Function("RunUpdateServices")]
        public async Task<OkObjectResult> RunUpdateServices([TimerTrigger("0 */30 * * * *")] TimerInfo timer)
        {
            await RunFileShareToServiceMapDb_Request();
            await UpdateIrisAppVersions_Request();
            await UpdateAppVersions_Request();
            return new OkObjectResult($"Updated Service map and version");
        }

            // HTTP trigger to update appversion for all 'iris' servicetype
            [Function("UpdateIrisAppVersions")]
        public async Task<IActionResult> UpdateIrisAppVersions(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "updateirisappversions")] HttpRequest req)
        {
            return await UpdateIrisAppVersions_Request();
        }

        public async Task<IActionResult> UpdateAppVersions(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "updateappversions")] HttpRequest req)
        {
            return await UpdateAppVersions_Request();
        }

        public async Task<IActionResult> UpdateAppVersions_Request()
        {
            var Services = await _db.ServiceMaps
                .Where(x => (x.servicetype == "Sesame" || x.servicetype == "Goline" || x.servicetype == "iReport") 
                    && x.hostname != null && x.ipaddr != null)
                .ToListAsync();

            var serviceById = Services.ToDictionary(s => s.id);

            var probeResults = await Task.WhenAll(Services.Select(async service =>
            {
                try
                {
                    
                    var url = $"http://{service.hostname}/live/server";
                    var response = await _httpClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                    {
                        return (serviceId: service.id, success: false, appversion: (string?)null, instance: (string?)null, sqlhostname: (string?)null, dbname: (string?)null, error: $"Status code {(int)response.StatusCode}");
                    }

                    var xml = await response.Content.ReadAsStringAsync();
                    var doc = System.Xml.Linq.XDocument.Parse(xml);
                    var liveElem = doc.Root?.Name.LocalName == "live" ? doc.Root : doc.Root?.Element("live");
                    var serverElem = liveElem?.Element("server");
                    var dbElem = liveElem?.Element("db");

                    var appversion = serverElem?.Attribute("version")?.Value;
                    var instance = serverElem?.Attribute("instance")?.Value;
                    var sqlhostname = dbElem?.Attribute("hostname")?.Value;
                    var dbname = dbElem?.Attribute("catalog")?.Value;

                    return (serviceId: service.id, success: true, appversion, instance, sqlhostname, dbname, error: (string?)null);
                }
                catch (Exception ex)
                {
                    return (serviceId: service.id, success: false, appversion: (string?)null, instance: (string?)null, sqlhostname: (string?)null, dbname: (string?)null, error: ex.Message);
                }
            }));

            int updated = 0;
            foreach (var result in probeResults)
            {
                if (!serviceById.TryGetValue(result.serviceId, out var service))
                {
                    continue;
                }

                if (!result.success)
                {
                    _logger.LogWarning("Failed to update appversion for {Hostname}: {Error}", service.hostname, result.error);
                    continue;
                }

                var changed = false;

                if (!string.IsNullOrWhiteSpace(result.appversion) && !string.Equals(service.appversion, result.appversion, StringComparison.OrdinalIgnoreCase))
                {
                    service.appversion = result.appversion;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(result.instance) && !string.Equals(service.instance, result.instance, StringComparison.OrdinalIgnoreCase))
                {
                    service.instance = result.instance;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(result.sqlhostname) && !string.Equals(service.sqlhostname, result.sqlhostname, StringComparison.OrdinalIgnoreCase))
                {
                    service.sqlhostname = result.sqlhostname;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(result.dbname) && !string.Equals(service.dbname, result.dbname, StringComparison.OrdinalIgnoreCase))
                {
                    service.dbname = result.dbname;
                    changed = true;
                }

                if (changed)
                {
                    _db.ServiceMaps.Update(service);
                    updated++;
                }
            }
            await _db.SaveChangesAsync();
            return new OkObjectResult($"Updated appversion for {updated} services.");
        }


        public async Task<IActionResult> UpdateIrisAppVersions_Request()
        {
            var irisServices = await _db.ServiceMaps
                .Where(x => x.servicetype == "iris" && x.hostname != null && x.ipaddr != null)
                .ToListAsync();

            var serviceById = irisServices.ToDictionary(s => s.id);

            var probeResults = await Task.WhenAll(irisServices.Select(async service =>
            {
                try
                {
                    var icontrolHost = service.hostname.Replace("iris", "icontrol", StringComparison.OrdinalIgnoreCase);
                    var url = $"http://{icontrolHost}/live/server";
                    var response = await _httpClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                    {
                        return (serviceId: service.id, success: false, appversion: (string?)null, instance: (string?)null, sqlhostname: (string?)null, dbname: (string?)null, error: $"Status code {(int)response.StatusCode}");
                    }

                    var xml = await response.Content.ReadAsStringAsync();
                    var doc = System.Xml.Linq.XDocument.Parse(xml);
                    var liveElem = doc.Root?.Name.LocalName == "live" ? doc.Root : doc.Root?.Element("live");
                    var serverElem = liveElem?.Element("server");
                    var dbElem = liveElem?.Element("db");

                    var appversion = serverElem?.Attribute("version")?.Value;
                    var instance = serverElem?.Attribute("instance")?.Value;
                    var sqlhostname = dbElem?.Attribute("hostname")?.Value;
                    var dbname = dbElem?.Attribute("catalog")?.Value;

                    return (serviceId: service.id, success: true, appversion, instance, sqlhostname, dbname, error: (string?)null);
                }
                catch (Exception ex)
                {
                    return (serviceId: service.id, success: false, appversion: (string?)null, instance: (string?)null, sqlhostname: (string?)null, dbname: (string?)null, error: ex.Message);
                }
            }));

            int updated = 0;
            foreach (var result in probeResults)
            {
                if (!serviceById.TryGetValue(result.serviceId, out var service))
                {
                    continue;
                }

                if (!result.success)
                {
                    _logger.LogWarning("Failed to update appversion for {Hostname}: {Error}", service.hostname, result.error);
                    continue;
                }

                var changed = false;

                if (!string.IsNullOrWhiteSpace(result.appversion) && !string.Equals(service.appversion, result.appversion, StringComparison.OrdinalIgnoreCase))
                {
                    service.appversion = result.appversion;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(result.instance) && !string.Equals(service.instance, result.instance, StringComparison.OrdinalIgnoreCase))
                {
                    service.instance = result.instance;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(result.sqlhostname) && !string.Equals(service.sqlhostname, result.sqlhostname, StringComparison.OrdinalIgnoreCase))
                {
                    service.sqlhostname = result.sqlhostname;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(result.dbname) && !string.Equals(service.dbname, result.dbname, StringComparison.OrdinalIgnoreCase))
                {
                    service.dbname = result.dbname;
                    changed = true;
                }

                if (changed)
                {
                    _db.ServiceMaps.Update(service);
                    updated++;
                }
            }
            await _db.SaveChangesAsync();
            return new OkObjectResult($"Updated appversion for {updated} iris services.");
        }

        // HTTP trigger to start an Azure Automation runbook
        [Function("TriggerRunbook_F4DUpdateServices")]
        public async Task<IActionResult> TriggerRunbook_F4DUpdateServices_Async(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "trigger-runbook-f4dupdateservices")] HttpRequest req)
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonDocument.Parse(body).RootElement;

            static string? GetStringProperty(System.Text.Json.JsonElement element, params string[] names)
            {
                foreach (var name in names)
                {
                    if (element.TryGetProperty(name, out var value) && value.ValueKind != System.Text.Json.JsonValueKind.Null)
                    {
                        return value.ValueKind == System.Text.Json.JsonValueKind.String
                            ? value.GetString()
                            : value.ToString();
                    }
                }
                return null;
            }

            static bool GetBoolPropertyOrDefault(System.Text.Json.JsonElement element, bool defaultValue, params string[] names)
            {
                foreach (var name in names)
                {
                    if (!element.TryGetProperty(name, out var value) || value.ValueKind == System.Text.Json.JsonValueKind.Null)
                        continue;

                    if (value.ValueKind == System.Text.Json.JsonValueKind.True || value.ValueKind == System.Text.Json.JsonValueKind.False)
                        return value.GetBoolean();

                    if (value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var s = value.GetString();
                        if (bool.TryParse(s, out var b)) return b;
                        if (s == "1") return true;
                        if (s == "0") return false;
                    }

                    if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var i))
                        return i != 0;
                }

                return defaultValue;
            }

            string destinationAddressPrefix = GetStringProperty(data, "DestinationAddressPrefix", "destinationAddressPrefix", "VMipaddr", "vMipaddr") ?? string.Empty;
            string version = GetStringProperty(data, "Version", "version") ?? string.Empty;
            string serviceName = GetStringProperty(data, "ServiceName", "serviceName") ?? string.Empty;
            string sqlServer = GetStringProperty(data, "SqlServer", "sqlServer") ?? string.Empty;
            string database = GetStringProperty(data, "Database", "database") ?? string.Empty;
            bool forceDownload = GetBoolPropertyOrDefault(data, true, "ForceDownload", "forceDownload");
            bool install = GetBoolPropertyOrDefault(data, false, "Install", "install");
            string serviceType = GetStringProperty(data, "ServiceType", "serviceType") ?? "iris";
            bool isNoSqlDbType = string.Equals(serviceType, "sesame", StringComparison.OrdinalIgnoreCase)
                || string.Equals(serviceType, "goline", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(destinationAddressPrefix)
                || string.IsNullOrWhiteSpace(version)
                || string.IsNullOrWhiteSpace(serviceName)
                || (!isNoSqlDbType && (string.IsNullOrWhiteSpace(sqlServer)
                    || string.IsNullOrWhiteSpace(database))))
            {
                return new BadRequestObjectResult("Missing required runbook parameters.");
            }

            string runbookName = "automation-F4DUpdateServices";

            if (string.IsNullOrWhiteSpace(_subscriptionId)
                || string.IsNullOrWhiteSpace(_automationResourceGroup)
                || string.IsNullOrWhiteSpace(_automationAccount)
                || string.IsNullOrWhiteSpace(runbookName))
            {
                return new BadRequestObjectResult("Missing automation configuration environment variables.");
            }

            var parameters = new Dictionary<string, object>
            {
                { "DestinationAddressPrefix", destinationAddressPrefix },
                { "Version", version },
                { "ServiceName", serviceName },
                { "SqlServer", sqlServer },
                { "Database", database },
                { "ForceDownload", forceDownload },
                { "Install", install },
                { "ServiceType", serviceType }
            };

            var credential = new DefaultAzureCredential();
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }));

            var jobName = Guid.NewGuid().ToString();
            var requestUri = $"https://management.azure.com/subscriptions/{_subscriptionId}/resourceGroups/{_automationResourceGroup}/providers/Microsoft.Automation/automationAccounts/{_automationAccount}/jobs/{jobName}?api-version=2023-11-01";

            var payload = new
            {
                properties = new
                {
                    runbook = new { name = runbookName },
                    parameters
                }
            };

            using var httpClient = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to start runbook. Status: {StatusCode}, Body: {Body}", response.StatusCode, responseContent);
                return new ObjectResult(responseContent) { StatusCode = (int)response.StatusCode };
            }

            return new OkObjectResult(new { jobId = jobName, status = "Submitted", details = responseContent });
        }

        [Function("TriggerRunbook_F4DMigrateInfra")]
        public async Task<IActionResult> TriggerRunbook_F4DMigrateInfra_Async(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "trigger-runbook-f4dmigrateinfra")] HttpRequest req)
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonDocument.Parse(body).RootElement;

            static string? GetStringProperty(System.Text.Json.JsonElement element, params string[] names)
            {
                foreach (var name in names)
                {
                    if (element.TryGetProperty(name, out var value) && value.ValueKind != System.Text.Json.JsonValueKind.Null)
                    {
                        return value.ValueKind == System.Text.Json.JsonValueKind.String
                            ? value.GetString()
                            : value.ToString();
                    }
                }
                return null;
            }

            static int? GetIntProperty(System.Text.Json.JsonElement element, params string[] names)
            {
                foreach (var name in names)
                {
                    if (!element.TryGetProperty(name, out var value) || value.ValueKind == System.Text.Json.JsonValueKind.Null)
                        continue;

                    if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var intValue))
                        return intValue;

                    if (value.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(value.GetString(), out var parsedValue))
                        return parsedValue;
                }

                return null;
            }

            var customerName = GetStringProperty(data, "CustomerName", "customerName") ?? string.Empty;
            var siteName = GetStringProperty(data, "SiteName", "siteName") ?? string.Empty;
            var destinationAddressPrefix = GetStringProperty(data, "DestinationAddressPrefix", "destinationAddressPrefix") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(customerName)
                || string.IsNullOrWhiteSpace(siteName)
                || string.IsNullOrWhiteSpace(destinationAddressPrefix))
            {
                return new BadRequestObjectResult("Missing required parameters: CustomerName, SiteName, DestinationAddressPrefix.");
            }

            var ruleName = GetStringProperty(data, "RuleName", "ruleName");
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                ruleName = $"{customerName}/{siteName}";
            }

            var parentZoneName = GetStringProperty(data, "ParentZoneName", "parentZoneName") ?? string.Empty;
            var regionPrefix = GetStringProperty(data, "RegionPrefix", "regionPrefix") ?? string.Empty;
            var hostname = GetStringProperty(data, "Hostname", "hostname") ?? string.Empty;
            var port = GetIntProperty(data, "Port", "port");

            const string runbookName = "automation-F4DMigrateInfrastructure";

            if (string.IsNullOrWhiteSpace(_subscriptionId)
                || string.IsNullOrWhiteSpace(_automationResourceGroup)
                || string.IsNullOrWhiteSpace(_automationAccount))
            {
                return new BadRequestObjectResult("Missing automation configuration environment variables.");
            }

            var parameters = new Dictionary<string, object>
            {
                { "CustomerName", customerName },
                { "SiteName", siteName },
                { "RuleName", ruleName },
                { "DestinationAddressPrefix", destinationAddressPrefix },
                { "ParentZoneName", parentZoneName },
                { "RegionPrefix", regionPrefix },
                { "Hostname", hostname }
            };

            if (port.HasValue)
            {
                parameters["Port"] = port.Value;
            }

            var credential = new DefaultAzureCredential();
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }));

            var jobName = Guid.NewGuid().ToString();
            var requestUri = $"https://management.azure.com/subscriptions/{_subscriptionId}/resourceGroups/{_automationResourceGroup}/providers/Microsoft.Automation/automationAccounts/{_automationAccount}/jobs/{jobName}?api-version=2023-11-01";

            var payload = new
            {
                properties = new
                {
                    runbook = new { name = runbookName },
                    parameters
                }
            };

            using var httpClient = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to start migrate infra runbook. Status: {StatusCode}, Body: {Body}", response.StatusCode, responseContent);
                return new ObjectResult(responseContent) { StatusCode = (int)response.StatusCode };
            }

            return new OkObjectResult(new { jobId = jobName, status = "Submitted", details = responseContent });
        }

        [Function("TriggerRunbook_F4DProvisionSite")]
        public async Task<IActionResult> TriggerRunbook_F4DProvisionSite_Async(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "trigger-runbook-f4dSiteDeployment")] HttpRequest req)
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonDocument.Parse(body).RootElement;

            static string? GetStringProperty(System.Text.Json.JsonElement element, params string[] names)
            {
                foreach (var name in names)
                {
                    if (element.TryGetProperty(name, out var value) && value.ValueKind != System.Text.Json.JsonValueKind.Null)
                    {
                        return value.ValueKind == System.Text.Json.JsonValueKind.String
                            ? value.GetString()
                            : value.ToString();
                    }
                }
                return null;
            }

            var isNewCustomer = GetStringProperty(data, "IsNewCustomer", "isNewCustomer") ?? string.Empty;
            var isSetupNotification = GetStringProperty(data, "IsSetupNotification", "isSetupNotification") ?? "true";
            var isSetupFilemanager = GetStringProperty(data, "IsSetupFilemanager", "isSetupFilemanager") ?? "false";
            var regionPrefix = GetStringProperty(data, "RegionPrefix", "regionPrefix") ?? string.Empty;
            var customerName = GetStringProperty(data, "CustomerName", "customerName") ?? string.Empty;
            var siteName = GetStringProperty(data, "SiteName", "siteName") ?? string.Empty;
            var port = GetStringProperty(data, "Port", "port") ?? string.Empty;
            var sourceAddressPrefix = GetStringProperty(data, "SourceAddressPrefix", "sourceAddressPrefix") ?? string.Empty;
            var meshSubnet = GetStringProperty(data, "MeshSubnet", "meshSubnet") ?? string.Empty;
            var destinationAddressPrefix = GetStringProperty(data, "DestinationAddressPrefix", "destinationAddressPrefix") ?? string.Empty;
            var parentZoneName = GetStringProperty(data, "ParentZoneName", "parentZoneName") ?? string.Empty;
            var irisTemplateDbName = GetStringProperty(data, "IrisTemplateDBName", "irisTemplateDBName") ?? "Z-iris-masterdb-25-1-202501013";
            var sesameTemplateDbName = GetStringProperty(data, "SesameTemplateDBName", "sesameTemplateDBName") ?? string.Empty;
            var siteCode = GetStringProperty(data, "SiteCode", "siteCode") ?? string.Empty;
            var resourceGroupName = GetStringProperty(data, "ResourceGroupName", "resourceGroupName") ?? "Q4D-ResourceGroup-1";
            var subscription = GetStringProperty(data, "Subscription", "subscription") ?? "Fleet4D";
            var keyVaultName = GetStringProperty(data, "KeyVaultName", "keyVaultName") ?? "eau1-f4d-prod-kv-sql01";

            if (string.IsNullOrWhiteSpace(isNewCustomer)
                || string.IsNullOrWhiteSpace(customerName)
                || string.IsNullOrWhiteSpace(siteName)
                || string.IsNullOrWhiteSpace(port)
                || string.IsNullOrWhiteSpace(sourceAddressPrefix)
                || string.IsNullOrWhiteSpace(destinationAddressPrefix)
                || string.IsNullOrWhiteSpace(parentZoneName)
                || string.IsNullOrWhiteSpace(siteCode))
            {
                return new BadRequestObjectResult("Missing required parameters: IsNewCustomer, CustomerName, SiteName, Port, SourceAddressPrefix, DestinationAddressPrefix, ParentZoneName, SiteCode.");
            }

            string runbookName = "automation-F4DSiteDeployment"
                ?? GetStringProperty(data, "RunbookName", "runbookName")
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(_subscriptionId)
                || string.IsNullOrWhiteSpace(_automationResourceGroup)
                || string.IsNullOrWhiteSpace(_automationAccount)
                || string.IsNullOrWhiteSpace(runbookName))
            {
                return new BadRequestObjectResult("Missing automation configuration environment variables or runbook name (AUTOMATION_RUNBOOK_PROVISION_SITE_NAME). Also supports RunbookName in body.");
            }

            var parameters = new Dictionary<string, object>
            {
                { "IsNewCustomer", isNewCustomer },
                { "IsSetupNotification", isSetupNotification },
                { "IsSetupFilemanager", isSetupFilemanager },
                { "RegionPrefix", regionPrefix },
                { "CustomerName", customerName },
                { "SiteName", siteName },
                { "Port", port },
                { "SourceAddressPrefix", sourceAddressPrefix },
                { "DestinationAddressPrefix", destinationAddressPrefix },
                { "ParentZoneName", parentZoneName },
                { "IrisTemplateDBName", irisTemplateDbName },
                { "SesameTemplateDBName", sesameTemplateDbName },
                { "SiteCode", siteCode },
                { "ResourceGroupName", resourceGroupName },
                { "Subscription", subscription },
                { "KeyVaultName", keyVaultName }
            };

            if (!string.IsNullOrWhiteSpace(meshSubnet))
            {
                parameters["MeshSubnet"] = meshSubnet;
            }

            var credential = new DefaultAzureCredential();
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }));

            var jobName = Guid.NewGuid().ToString();
            var requestUri = $"https://management.azure.com/subscriptions/{_subscriptionId}/resourceGroups/{_automationResourceGroup}/providers/Microsoft.Automation/automationAccounts/{_automationAccount}/jobs/{jobName}?api-version=2023-11-01";

            var payload = new
            {
                properties = new
                {
                    runbook = new { name = runbookName },
                    parameters
                }
            };

            using var httpClient = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to start provision site runbook. Status: {StatusCode}, Body: {Body}", response.StatusCode, responseContent);
                return new ObjectResult(responseContent) { StatusCode = (int)response.StatusCode };
            }

            return new OkObjectResult(new { jobId = jobName, status = "Submitted", runbookName, details = responseContent });
        }

        // HTTP trigger to check status of an Azure Automation job
        [Function("CheckRunbookJobStatus")]
        public async Task<IActionResult> CheckRunbookJobStatusAsync(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "check-runbook-job-status/{jobId}")] HttpRequest req, string jobId)
        {
            if (string.IsNullOrWhiteSpace(_subscriptionId)
                || string.IsNullOrWhiteSpace(_automationResourceGroup)
                || string.IsNullOrWhiteSpace(_automationAccount)
                || string.IsNullOrWhiteSpace(jobId))
            {
                return new BadRequestObjectResult("Missing required parameters or environment variables.");
            }

            var credential = new DefaultAzureCredential();
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }));

            var requestUri = $"https://management.azure.com/subscriptions/{_subscriptionId}/resourceGroups/{_automationResourceGroup}/providers/Microsoft.Automation/automationAccounts/{_automationAccount}/jobs/{jobId}?api-version=2023-11-01";

            using var httpClient = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get job status. Status: {StatusCode}, Body: {Body}", response.StatusCode, responseContent);
                return new ObjectResult(responseContent) { StatusCode = (int)response.StatusCode };
            }

            return new OkObjectResult(System.Text.Json.JsonDocument.Parse(responseContent).RootElement);
        }
    }
}
