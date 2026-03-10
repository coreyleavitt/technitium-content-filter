using System.Net;
using BenchmarkDotNet.Attributes;
using ContentFilter.Models;
using ContentFilter.Services;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace ContentFilter.Benchmarks;

/// <summary>
/// Benchmarks the full IsAllowed evaluation path and individual steps.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class FilteringServiceBenchmarks
{
    private Dictionary<string, DnsRewriteConfig> _rewrites = null!;
    private Dictionary<string, DnsRewriteConfig> _emptyRewrites = null!;
    private FilteringService _filteringService = null!;
    private DnsDatagram _blockedRequest = null!;
    private DnsDatagram _allowedRequest = null!;
    private IPEndPoint _ep = null!;
    private AppConfig _cidrConfig = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rewrites = new Dictionary<string, DnsRewriteConfig>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 100; i++)
            _rewrites[$"rewrite{i}.example.com"] = new DnsRewriteConfig
            {
                Domain = $"rewrite{i}.example.com",
                Answer = $"10.0.0.{i % 256}"
            };

        _emptyRewrites = new Dictionary<string, DnsRewriteConfig>();

        // Full IsAllowed path setup
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        var configSvc = new ConfigService(Path.GetTempPath());
        configSvc.Load(System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        }));
        _filteringService = new FilteringService(configSvc);

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 10_000; i++)
            blocked.Add($"blocked{i}.example.com");

        _filteringService.UpdateCompiledProfiles(new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(blocked, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        });

        _blockedRequest = new DnsDatagram(
            1, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, new[] { new DnsQuestionRecord("blocked5000.example.com", DnsResourceRecordType.A, DnsClass.IN) });

        _allowedRequest = new DnsDatagram(
            2, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, new[] { new DnsQuestionRecord("allowed.test.com", DnsResourceRecordType.A, DnsClass.IN) });

        _ep = new IPEndPoint(IPAddress.Parse("192.168.1.50"), 53);

        // CIDR resolution config
        _cidrConfig = new AppConfig
        {
            Clients =
            [
                new ClientConfig { Ids = ["192.168.0.0/16"], Profile = "broad" },
                new ClientConfig { Ids = ["192.168.1.0/24"], Profile = "narrow" }
            ]
        };
    }

    [Benchmark]
    public DnsRewriteConfig? GetRewrite_Hit() =>
        FilteringService.GetRewrite(_rewrites, "rewrite50.example.com");

    [Benchmark]
    public DnsRewriteConfig? GetRewrite_SubdomainHit() =>
        FilteringService.GetRewrite(_rewrites, "www.rewrite50.example.com");

    [Benchmark]
    public DnsRewriteConfig? GetRewrite_Miss() =>
        FilteringService.GetRewrite(_rewrites, "notfound.example.com");

    [Benchmark]
    public DnsRewriteConfig? GetRewrite_EmptyDict() =>
        FilteringService.GetRewrite(_emptyRewrites, "anything.com");

    [Benchmark]
    public bool MatchesCidr_IPv4() =>
        FilteringService.MatchesCidr(IPAddress.Parse("192.168.1.50"), "192.168.1.0/24", out _);

    [Benchmark]
    public bool MatchesCidr_IPv6() =>
        FilteringService.MatchesCidr(IPAddress.Parse("fd00::1"), "fd00::/64", out _);

    [Benchmark]
    public string? ResolveProfile_ExactIp()
    {
        var config = new AppConfig
        {
            Clients = [new ClientConfig { Ids = ["192.168.1.50"], Profile = "kids" }]
        };
        return FilteringService.ResolveProfile(config, null, IPAddress.Parse("192.168.1.50"));
    }

    [Benchmark]
    public string? ResolveProfile_CidrMatch() =>
        FilteringService.ResolveProfile(_cidrConfig, null, IPAddress.Parse("192.168.1.50"));

    [Benchmark]
    public bool IsAllowed_FullPath_Blocked() =>
        _filteringService.IsAllowed(_blockedRequest, _ep, "blocked5000.example.com", out _, out _);

    [Benchmark]
    public bool IsAllowed_FullPath_Allowed() =>
        _filteringService.IsAllowed(_allowedRequest, _ep, "allowed.test.com", out _, out _);
}
