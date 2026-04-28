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

namespace DeployFunc
{
    public class AutomationFunction
    {
        private readonly ILogger<AutomationFunction> _logger;
        private readonly ServiceMapDbContext _db;

        public AutomationFunction(ILogger<AutomationFunction> logger, ServiceMapDbContext db)
        {
            _logger = logger;
            _db = db;
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
            string shareName = Environment.GetEnvironmentVariable("FileShareName") ?? "myfileshare";
            string directoryName = Environment.GetEnvironmentVariable("FileShareDirectory") ?? "";
            string fileName = Environment.GetEnvironmentVariable("FileShareFileName") ?? "services.map";

            try
            {
                var share = new ShareClient(connectionString, shareName);
                var directory = share.GetDirectoryClient(directoryName);
                var file = directory.GetFileClient(fileName);
                if (await file.ExistsAsync())
                {
                    var download = await file.DownloadAsync();
                    using var reader = new System.IO.StreamReader(download.Value.Content);
                    var lines = new System.Collections.Generic.List<string>();
                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync();
                        if (line != null && !line.TrimStart().StartsWith("#"))
                            lines.Add(line);
                    }
                    string content = string.Join("\n", lines);
                    _logger.LogInformation($"File content from Azure File Share: {content}");
                    return new OkObjectResult(content);
                }
                else
                {
                    string msg = $"File not found: {fileName}";
                    _logger.LogWarning(msg);
                    return new NotFoundObjectResult(msg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading from Azure File Share");
                return new ObjectResult($"Error reading from Azure File Share: {ex.Message}") { StatusCode = 500 };
            }
        }

        // HTTP trigger to upsert ServiceMap table from file share using EF Core
        [Function("FileShareToServiceMapDbFunction")]
        public async Task<IActionResult> RunFileShareToServiceMapDbAsync(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "upsertservicemap")] HttpRequest req)
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

        // HTTP trigger to update appversion for all 'iris' servicetype
        [Function("UpdateIrisAppVersions")]
        public async Task<IActionResult> UpdateIrisAppVersions(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "updateirisappversions")] HttpRequest req)
        {
            var irisServices = await _db.ServiceMaps
                .Where(x => x.servicetype == "iris" && x.hostname != null && x.ipaddr != null)
                .ToListAsync();
            int updated = 0;
            foreach (var service in irisServices)
            {
                try
                {
                    // Replace 'iris' with 'icontrol' in the hostname
                    var icontrolHost = service.hostname.Replace("iris", "icontrol", StringComparison.OrdinalIgnoreCase);
                    // Build the URL
                    var url = $"http://{icontrolHost}/live/server";
                    using var httpClient = new System.Net.Http.HttpClient();
                    var response = await httpClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                        continue;
                    var xml = await response.Content.ReadAsStringAsync();
                    // Parse XML
                    var doc = System.Xml.Linq.XDocument.Parse(xml);
                    var serverElem = doc.Root?.Element("server");
                    var appversion = serverElem?.Attribute("version")?.Value;
                    if (!string.IsNullOrEmpty(appversion))
                    {
                        service.appversion = appversion;
                        _db.ServiceMaps.Update(service);
                        updated++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to update appversion for {service.hostname}");
                }
            }
            await _db.SaveChangesAsync();
            return new OkObjectResult($"Updated appversion for {updated} iris services.");
        }
    }
}
