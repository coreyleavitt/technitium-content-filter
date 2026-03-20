using System.Diagnostics;
using System.Net;
using ContentFilter.Models;
using ContentFilter.Services;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace ContentFilter.Tests;

/// <summary>
/// Issue #38: Performance tests for hot-path filtering.
/// Since BenchmarkDotNet is only in the Benchmarks project, these use
/// simple timing assertions to verify performance doesn't regress.
/// The actual benchmarks are in ContentFilter.Benchmarks.
/// </summary>
[Trait("Category", "Unit")]
public class FilteringPerformanceTests
{
    private static ConfigService CreateConfig(AppConfig config)
    {
        var svc = new ConfigService(Path.GetTempPath());
        svc.Load(System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        }));
        return svc;
    }

    private static DnsDatagram MakeRequest(string domain)
    {
        var question = new DnsQuestionRecord(domain, DnsResourceRecordType.A, DnsClass.IN);
        return new DnsDatagram(
            0, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, new[] { question });
    }

    private static IPEndPoint EP(string ip = "192.168.1.50") => new(IPAddress.Parse(ip), 53);

    [Fact]
    public void Evaluate_10kBlockedDomains_Throughput()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        var configSvc = CreateConfig(config);
        var svc = new FilteringService(configSvc);

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 10_000; i++)
            blocked.Add($"blocked{i}.example.com");

        svc.UpdateCompiledProfiles(new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(blocked, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        });

        var blockedReq = MakeRequest("blocked5000.example.com");
        var allowedReq = MakeRequest("allowed.test.com");

        // Warmup
        for (int i = 0; i < 100; i++)
        {
            svc.Evaluate(blockedReq, EP(), "blocked5000.example.com");
            svc.Evaluate(allowedReq, EP(), "allowed.test.com");
        }

        // Timed run: 10k iterations of blocked + allowed queries
        var sw = Stopwatch.StartNew();
        const int iterations = 10_000;
        for (int i = 0; i < iterations; i++)
        {
            svc.Evaluate(blockedReq, EP(), "blocked5000.example.com");
            svc.Evaluate(allowedReq, EP(), "allowed.test.com");
        }
        sw.Stop();

        // 20k queries in under 1 second on any reasonable hardware
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"10k blocked + 10k allowed queries took {sw.ElapsedMilliseconds}ms, expected < 1000ms");
    }

    [Fact]
    public void DomainMatcher_100kDomains_Throughput()
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 100_000; i++)
            domains.Add($"domain{i}.example.com");

        // Warmup
        for (int i = 0; i < 100; i++)
            DomainMatcher.Matches(domains, "domain50000.example.com");

        var sw = Stopwatch.StartNew();
        const int iterations = 100_000;
        for (int i = 0; i < iterations; i++)
        {
            DomainMatcher.Matches(domains, $"domain{i % 100_000}.example.com");
        }
        sw.Stop();

        // 100k lookups in under 2 seconds
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"100k domain lookups took {sw.ElapsedMilliseconds}ms, expected < 2000ms");
    }

    [Fact]
    public void SubdomainWalking_DeepNesting_Performance()
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" };

        // Deep subdomain: 20 levels
        var deep = string.Join('.', Enumerable.Range(0, 20).Select(i => $"sub{i}")) + ".example.com";

        // Warmup
        for (int i = 0; i < 100; i++)
            DomainMatcher.Matches(domains, deep);

        var sw = Stopwatch.StartNew();
        const int iterations = 100_000;
        for (int i = 0; i < iterations; i++)
        {
            DomainMatcher.Matches(domains, deep);
        }
        sw.Stop();

        // 100k lookups of 20-level deep subdomain in under 2 seconds
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"100k deep subdomain lookups took {sw.ElapsedMilliseconds}ms, expected < 2000ms");
    }

    [Fact]
    public void GetRewrite_100Rewrites_Throughput()
    {
        var rewrites = new Dictionary<string, DnsRewriteConfig>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 100; i++)
            rewrites[$"rewrite{i}.example.com"] = new DnsRewriteConfig
            {
                Domain = $"rewrite{i}.example.com",
                Answer = $"10.0.0.{i % 256}"
            };

        // Warmup
        for (int i = 0; i < 100; i++)
            FilteringService.GetRewrite(rewrites, "rewrite50.example.com");

        var sw = Stopwatch.StartNew();
        const int iterations = 100_000;
        for (int i = 0; i < iterations; i++)
        {
            FilteringService.GetRewrite(rewrites, "rewrite50.example.com");
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"100k rewrite lookups took {sw.ElapsedMilliseconds}ms, expected < 2000ms");
    }

    [Fact]
    public void ProfileCompiler_CompileAll_Performance()
    {
        var registry = new ServiceRegistry();
        var compiler = new ProfileCompiler(registry);

        // Config with 50 profiles, each with 100 custom rules
        var config = new AppConfig { BaseProfile = "base" };
        config.Profiles["base"] = new ProfileConfig
        {
            CustomRules = Enumerable.Range(0, 100).Select(i => $"base-block{i}.com").ToList()
        };
        for (int p = 0; p < 50; p++)
        {
            config.Profiles[$"profile{p}"] = new ProfileConfig
            {
                CustomRules = Enumerable.Range(0, 100).Select(i => $"p{p}-block{i}.com").ToList(),
                AllowList = Enumerable.Range(0, 10).Select(i => $"p{p}-allow{i}.com").ToList(),
                DnsRewrites = Enumerable.Range(0, 5).Select(i =>
                    new DnsRewriteConfig { Domain = $"p{p}-rw{i}.com", Answer = "1.2.3.4" }).ToList()
            };
        }

        // Warmup
        compiler.CompileAll(config);

        var sw = Stopwatch.StartNew();
        const int iterations = 100;
        for (int i = 0; i < iterations; i++)
        {
            compiler.CompileAll(config);
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"100 compilations of 50 profiles took {sw.ElapsedMilliseconds}ms, expected < 5000ms");
    }

    [Fact]
    public void CidrMatch_Throughput()
    {
        var ip = IPAddress.Parse("192.168.1.50");

        // Warmup
        for (int i = 0; i < 100; i++)
            FilteringService.MatchesCidr(ip, "192.168.1.0/24", out _);

        var sw = Stopwatch.StartNew();
        const int iterations = 100_000;
        for (int i = 0; i < iterations; i++)
        {
            FilteringService.MatchesCidr(ip, "192.168.1.0/24", out _);
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"100k CIDR matches took {sw.ElapsedMilliseconds}ms, expected < 2000ms");
    }
}
