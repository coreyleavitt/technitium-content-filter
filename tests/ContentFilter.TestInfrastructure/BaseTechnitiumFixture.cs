using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DnsClient;
using DnsClient.Protocol;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace ContentFilter.TestInfrastructure;

/// <summary>
/// Abstract base fixture that starts a Technitium DNS container, authenticates,
/// and provides helpers for configuring and querying DNS. Subclasses handle
/// plugin installation.
/// </summary>
public abstract class BaseTechnitiumFixture : IAsyncLifetime
{
    private const string AdminPassword = "integration-test-password";

    private IContainer _container = null!;
    private HttpClient _http = null!;
    private string _apiToken = "";
    private string _dnsHost = "";

    /// <summary>Port mapped to Technitium's DNS port (53/tcp).</summary>
    public int DnsPort { get; private set; }

    /// <summary>Port mapped to Technitium's web API (5380).</summary>
    public int ApiPort { get; private set; }

    /// <summary>Display name for this plugin (used in reports).</summary>
    public abstract string PluginDisplayName { get; }

    /// <summary>Plugin name as registered in Technitium.</summary>
    protected abstract string PluginName { get; }

    /// <summary>Full class path for the plugin entry point.</summary>
    protected abstract string ClassPath { get; }

    /// <summary>Installs the plugin into the running Technitium instance.</summary>
    protected abstract Task InstallPluginAsync(HttpClient http, string apiToken);

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

        await InstallPluginAsync(_http, _apiToken);
    }

    /// <summary>
    /// Sets the plugin config and waits briefly for Technitium to reload.
    /// </summary>
    public async Task SetConfigAsync(string configJson)
    {
        var response = await _http.PostAsync("/api/apps/config/set",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = _apiToken,
                ["name"] = PluginName,
                ["classPath"] = ClassPath,
                ["config"] = configJson
            }));
        response.EnsureSuccessStatusCode();

        await FlushCacheAsync();

        // Brief delay to let Technitium process the config change
        await Task.Delay(1000);
    }

    /// <summary>
    /// Sets the plugin config from an object (serialized with camelCase).
    /// </summary>
    public Task SetConfigAsync(object config)
    {
        var configJson = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        return SetConfigAsync(configJson);
    }

    /// <summary>
    /// Flushes the Technitium DNS cache to prevent cross-test contamination.
    /// </summary>
    public async Task FlushCacheAsync()
    {
        var response = await _http.PostAsync("/api/cache/flush",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = _apiToken
            }));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Queries the Technitium DNS container and returns the parsed response.
    /// </summary>
    public async Task<DnsResponse> QueryAsync(string domain, ushort queryType = 1 /* A */)
    {
        var endpoint = new IPEndPoint(
            IPAddress.Parse(_dnsHost == "localhost" ? "127.0.0.1" : _dnsHost), DnsPort);
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

    /// <summary>
    /// Queries DNS with timing, returning the response code and latency.
    /// </summary>
    public async Task<(byte Rcode, TimeSpan Latency)> TimedQueryAsync(
        string domain, ushort queryType = 1)
    {
        var start = Stopwatch.GetTimestamp();
        var response = await QueryAsync(domain, queryType);
        var elapsed = Stopwatch.GetElapsedTime(start);
        return (response.ResponseCode, elapsed);
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
