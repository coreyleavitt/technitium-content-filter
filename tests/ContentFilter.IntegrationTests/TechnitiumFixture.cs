using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DnsClient;
using DnsClient.Protocol;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace ContentFilter.IntegrationTests;

/// <summary>
/// Shared test fixture that starts a Technitium DNS container, installs the plugin,
/// and provides helpers for configuring and querying DNS.
/// </summary>
public sealed class TechnitiumFixture : IAsyncLifetime
{
    private const string AdminPassword = "integration-test-password";
    private const string PluginName = "ContentFilter";
    private const string ClassPath = "ContentFilter.App";

    private IContainer _container = null!;
    private HttpClient _http = null!;
    private string _apiToken = "";
    private string _dnsHost = "";

    /// <summary>Port mapped to Technitium's DNS port (53/tcp).</summary>
    public int DnsPort { get; private set; }

    /// <summary>Port mapped to Technitium's web API (5380).</summary>
    public int ApiPort { get; private set; }

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder()
            .WithImage("technitium/dns-server:latest")
            .WithEnvironment("DNS_SERVER_DOMAIN", "test.local")
            .WithEnvironment("DNS_SERVER_ADMIN_PASSWORD", AdminPassword)
            .WithPortBinding(0, 53)
            .WithPortBinding(0, 5380)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPort(5380)
                    .ForPath("/")
                    .ForStatusCode(HttpStatusCode.OK)))
            .Build();

        await _container.StartAsync();

        DnsPort = _container.GetMappedPublicPort(53);
        ApiPort = _container.GetMappedPublicPort(5380);
        _dnsHost = _container.Hostname;

        _http = new HttpClient { BaseAddress = new Uri($"http://{_dnsHost}:{ApiPort}") };

        // Authenticate and get API token
        var loginResponse = await _http.PostAsync("/api/user/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["user"] = "admin",
                ["pass"] = AdminPassword
            }));
        loginResponse.EnsureSuccessStatusCode();
        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        _apiToken = loginJson.GetProperty("token").GetString()!;

        // Install the plugin
        await InstallPluginAsync();
    }

    private async Task InstallPluginAsync()
    {
        var zipPath = FindPluginZip();

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(_apiToken), "token");
        form.Add(new StringContent(PluginName), "name");
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(zipPath)), "appZip", "ContentFilter.zip");

        var response = await _http.PostAsync("/api/apps/install", form);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (body.GetProperty("status").GetString() != "ok")
            throw new Exception($"Plugin install failed: {body}");
    }

    /// <summary>
    /// Sets the plugin config and waits briefly for Technitium to reload.
    /// </summary>
    public async Task SetConfigAsync(object config)
    {
        var configJson = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var response = await _http.PostAsync("/api/apps/config/set",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = _apiToken,
                ["name"] = PluginName,
                ["classPath"] = ClassPath,
                ["config"] = configJson
            }));
        response.EnsureSuccessStatusCode();

        // Flush DNS cache so previous test results don't leak
        await FlushCacheAsync();

        // Brief delay to let Technitium process the config change
        await Task.Delay(1000);
    }

    /// <summary>
    /// Flushes the Technitium DNS cache to prevent cross-test contamination.
    /// </summary>
    private async Task FlushCacheAsync()
    {
        var response = await _http.PostAsync("/api/cache/flush",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = _apiToken
            }));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Queries the Technitium DNS container using DnsClient and returns the parsed response.
    /// </summary>
    public async Task<DnsResponse> QueryAsync(string domain, ushort queryType = 1 /* A */)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(_dnsHost == "localhost" ? "127.0.0.1" : _dnsHost), DnsPort);
        var options = new LookupClientOptions(endpoint)
        {
            UseCache = false,
            Retries = 0,
            Timeout = TimeSpan.FromSeconds(5),
            UseTcpOnly = true,
        };
        var lookup = new LookupClient(options);

        var qType = (QueryType)queryType;
        var result = await lookup.QueryAsync(domain, qType);

        var rcode = (byte)result.Header.ResponseCode;
        var answers = new List<DnsAnswer>();

        foreach (var record in result.Answers)
        {
            var type = (ushort)record.RecordType;
            var ttl = (uint)record.InitialTimeToLive;

            string rdata = record switch
            {
                ARecord a => a.Address.ToString(),
                AaaaRecord aaaa => aaaa.Address.ToString(),
                CNameRecord cname => cname.CanonicalName.Value.TrimEnd('.'),
                _ => record.ToString() ?? $"[type={type}]"
            };

            answers.Add(new DnsAnswer(type, ttl, rdata));
        }

        return new DnsResponse(rcode, answers);
    }

    private static string FindPluginZip()
    {
        var candidates = new[]
        {
            "/src/app/dist/ContentFilter.zip", // Docker build path
        };

        foreach (var path in candidates)
            if (File.Exists(path))
                return path;

        // Walk up from test binary
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "dist", "ContentFilter.zip");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir)!;
        }
        throw new FileNotFoundException(
            "Plugin ZIP not found. Run: docker build -f Dockerfile.build -o dist . from the project root first.");
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

/// <summary>Parsed DNS response.</summary>
public record DnsResponse(byte ResponseCode, List<DnsAnswer> Answers)
{
    public bool IsNxDomain => ResponseCode == 3;
    public bool IsNoError => ResponseCode == 0;
    public DnsAnswer? FirstAnswer => Answers.Count > 0 ? Answers[0] : null;
    public string? CnameTarget => Answers.Where(a => a.Type == 5).Select(a => a.Data).FirstOrDefault();
}

/// <summary>Parsed DNS answer record.</summary>
public record DnsAnswer(ushort Type, uint Ttl, string Data)
{
    public bool IsA => Type == 1;
    public bool IsCname => Type == 5;
    public bool IsAaaa => Type == 28;
}
