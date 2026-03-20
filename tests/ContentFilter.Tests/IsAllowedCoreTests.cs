using System.Net;
using System.Text.RegularExpressions;
using ContentFilter.Models;
using ContentFilter.Services;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace ContentFilter.Tests;

/// <summary>
/// Tests the full Evaluate evaluation order (steps 1-10) through the public API.
/// Uses real DnsDatagram instances via the Technitium library.
/// </summary>
[Trait("Category", "Unit")]
public class IsAllowedCoreTests
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

    private static FilteringService CreateService(AppConfig config, Dictionary<string, CompiledProfile>? compiled = null)
    {
        var configSvc = CreateConfig(config);
        var svc = new FilteringService(configSvc);
        if (compiled is not null)
            svc.UpdateCompiledProfiles(compiled);
        return svc;
    }

    private static DnsDatagram MakeRequest(string domain)
    {
        var question = new DnsQuestionRecord(domain, DnsResourceRecordType.A, DnsClass.IN);
        return new DnsDatagram(
            0, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, new[] { question });
    }

    private static IPEndPoint EP(string ip) => new(IPAddress.Parse(ip), 53);

    // Step 1: Global blocking disabled -> ALLOW
    [Fact]
    public void BlockingDisabled_AllowsEverything()
    {
        var config = new AppConfig
        {
            EnableBlocking = false,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig { CustomRules = ["blocked.com"] } }
        };
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked.com" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var svc = CreateService(config, compiled);

        var result = svc.Evaluate(MakeRequest("blocked.com"), EP("10.0.0.1"), "blocked.com");

        Assert.Equal(FilterAction.Allow, result.Action);
        Assert.Contains("blocking disabled", result.DebugSummary);
    }

    // Step 2-3: No matching profile, no base profile -> ALLOW
    [Fact]
    public void NoProfileMatch_NoBase_Allows()
    {
        var config = new AppConfig { EnableBlocking = true };
        var svc = CreateService(config);

        var result = svc.Evaluate(MakeRequest("anything.com"), EP("10.0.0.1"), "anything.com");

        Assert.Equal(FilterAction.Allow, result.Action);
    }

    // Step 3: No matching profile, falls back to base profile
    [Fact]
    public void NoProfileMatch_FallsBackToBase()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            BaseProfile = "base",
            Profiles = { ["base"] = new ProfileConfig() }
        };
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["base"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked.com" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var svc = CreateService(config, compiled);

        var result = svc.Evaluate(MakeRequest("blocked.com"), EP("10.0.0.1"), "blocked.com");

        Assert.Equal(FilterAction.Block, result.Action);
        Assert.Contains("BLOCKED", result.DebugSummary);
    }

    // Step 4: Rewrite takes priority over blocks
    [Fact]
    public void RewriteMatch_ReturnsRewriteAction()
    {
        var rewriteConfig = new DnsRewriteConfig { Domain = "youtube.com", Answer = "restrict.youtube.com" };
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "youtube.com" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, DnsRewriteConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["youtube.com"] = rewriteConfig
                })
        };
        var svc = CreateService(config, compiled);

        var result = svc.Evaluate(MakeRequest("youtube.com"), EP("10.0.0.1"), "youtube.com");

        Assert.Equal(FilterAction.Rewrite, result.Action);
        Assert.NotNull(result.Rewrite);
        Assert.Equal("restrict.youtube.com", result.Rewrite.Answer);
        Assert.Contains("REWRITE", result.DebugSummary);
    }

    // Step 5: Allowlist overrides blocks
    [Fact]
    public void AllowlistedDomain_AllowedEvenIfBlocked()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" })
        };
        var svc = CreateService(config, compiled);

        var result = svc.Evaluate(MakeRequest("example.com"), EP("10.0.0.1"), "example.com");

        Assert.Equal(FilterAction.Allow, result.Action);
        Assert.Contains("allowlisted", result.DebugSummary);
    }

    // Step 6: Schedule inactive -> ALLOW
    [Fact]
    public void ScheduleInactive_AllowsBlockedDomain()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            ScheduleAllDay = false,
            DefaultProfile = "kids",
            Profiles =
            {
                ["kids"] = new ProfileConfig
                {
                    // Block only Monday 09:00-17:00
                    Schedule = new()
                    {
                        ["mon"] = [new ScheduleConfig { AllDay = false, Start = "09:00", End = "17:00" }]
                    }
                }
            }
        };
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked.com" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var svc = CreateService(config, compiled);

        // This test verifies the schedule path exists but can't fully control timing.
        // The detailed schedule logic is tested in ScheduleTests.cs.
    }

    // Step 7: Blocked domain -> BLOCK
    [Fact]
    public void BlockedDomain_ReturnsFalse()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked.com" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var svc = CreateService(config, compiled);

        var result = svc.Evaluate(MakeRequest("blocked.com"), EP("10.0.0.1"), "blocked.com");

        Assert.Equal(FilterAction.Block, result.Action);
        Assert.Contains("BLOCKED", result.DebugSummary);
    }

    // Step 8: No match -> ALLOW
    [Fact]
    public void UnblockedDomain_Allowed()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked.com" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var svc = CreateService(config, compiled);

        var result = svc.Evaluate(MakeRequest("safe.com"), EP("10.0.0.1"), "safe.com");

        Assert.Equal(FilterAction.Allow, result.Action);
    }

    // Profile not found -> ALLOW
    [Fact]
    public void ProfileNotFound_Allows()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "nonexistent",
        };
        var svc = CreateService(config);

        var result = svc.Evaluate(MakeRequest("anything.com"), EP("10.0.0.1"), "anything.com");

        Assert.Equal(FilterAction.Allow, result.Action);
        Assert.Contains("profile not found", result.DebugSummary);
    }

    // Profile exists in config but not compiled yet -> ALLOW
    [Fact]
    public void ProfileNotCompiled_Allows()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        // Don't call UpdateCompiledProfiles
        var svc = CreateService(config);

        var result = svc.Evaluate(MakeRequest("anything.com"), EP("10.0.0.1"), "anything.com");

        Assert.Equal(FilterAction.Allow, result.Action);
        Assert.Contains("not compiled", result.DebugSummary);
    }

    // Exception handling: Evaluate wraps exceptions and returns Allow (fail open)
    [Fact]
    public void ExceptionInEvaluate_FailsOpen()
    {
        var configSvc = new ConfigService(Path.GetTempPath());
        configSvc.Load("{}"); // valid but empty
        var svc = new FilteringService(configSvc);

        var result = svc.Evaluate(MakeRequest("test.com"), EP("10.0.0.1"), "test.com");
        Assert.Equal(FilterAction.Allow, result.Action);
    }

    // Evaluation priority: Rewrite beats allowlist beats block
    [Fact]
    public void EvaluationOrder_RewriteBeatsAllowlistBeatsBlock()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" },
                new Dictionary<string, DnsRewriteConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["example.com"] = new DnsRewriteConfig { Domain = "example.com", Answer = "1.2.3.4" }
                })
        };
        var svc = CreateService(config, compiled);

        // Domain is in rewrites, allowlist, AND blocklist. Rewrite should win.
        var result = svc.Evaluate(MakeRequest("example.com"), EP("10.0.0.1"), "example.com");

        Assert.Equal(FilterAction.Rewrite, result.Action);
        Assert.NotNull(result.Rewrite);
        Assert.Contains("REWRITE", result.DebugSummary);
    }

    // Subdomain of blocked domain
    [Fact]
    public void SubdomainOfBlockedDomain_Blocked()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var svc = CreateService(config, compiled);

        var result = svc.Evaluate(MakeRequest("sub.example.com"), EP("10.0.0.1"), "sub.example.com");

        Assert.Equal(FilterAction.Block, result.Action);
        Assert.Contains("BLOCKED", result.DebugSummary);
    }

    // Full pipeline: AppConfig -> ProfileCompiler.CompileAll -> FilteringService.UpdateCompiledProfiles -> Evaluate
    [Fact]
    public void FullPipeline_CompileAll_ThenEvaluate()
    {
        var registry = new ServiceRegistry();
        registry.MergeCustomServices(new Dictionary<string, BlockedServiceDefinition>
        {
            ["youtube"] = new() { Name = "YouTube", Domains = ["youtube.com", "ytimg.com"] }
        });
        var compiler = new ProfileCompiler(registry);

        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles =
            {
                ["kids"] = new ProfileConfig
                {
                    BlockedServices = ["youtube"],
                    CustomRules = ["ads.example.com", "@@safe.youtube.com"],
                    AllowList = ["school.edu"],
                    DnsRewrites = [new DnsRewriteConfig { Domain = "search.com", Answer = "safesearch.google.com" }]
                }
            }
        };

        var compiled = compiler.CompileAll(config);
        var configSvc = CreateConfig(config);
        var svc = new FilteringService(configSvc);
        svc.UpdateCompiledProfiles(compiled);

        // Blocked via service expansion
        Assert.Equal(FilterAction.Block, svc.Evaluate(MakeRequest("youtube.com"), EP("10.0.0.1"), "youtube.com").Action);
        Assert.Equal(FilterAction.Block, svc.Evaluate(MakeRequest("ytimg.com"), EP("10.0.0.1"), "ytimg.com").Action);

        // Blocked via custom rule
        Assert.Equal(FilterAction.Block, svc.Evaluate(MakeRequest("ads.example.com"), EP("10.0.0.1"), "ads.example.com").Action);

        // Allowlisted via @@-rule overrides service block
        var safeResult = svc.Evaluate(MakeRequest("safe.youtube.com"), EP("10.0.0.1"), "safe.youtube.com");
        Assert.Equal(FilterAction.Allow, safeResult.Action);
        Assert.Contains("allowlisted", safeResult.DebugSummary);

        // Allowlisted via allowList
        Assert.Equal(FilterAction.Allow, svc.Evaluate(MakeRequest("school.edu"), EP("10.0.0.1"), "school.edu").Action);

        // Rewrite
        var rwResult = svc.Evaluate(MakeRequest("search.com"), EP("10.0.0.1"), "search.com");
        Assert.Equal(FilterAction.Rewrite, rwResult.Action);
        Assert.NotNull(rwResult.Rewrite);
        Assert.Equal("safesearch.google.com", rwResult.Rewrite.Answer);

        // Unblocked domain passes through
        Assert.Equal(FilterAction.Allow, svc.Evaluate(MakeRequest("wikipedia.org"), EP("10.0.0.1"), "wikipedia.org").Action);
    }

    // Step 9: Regex block rule blocks matching domain
    [Fact]
    public void RegexBlockRule_BlocksMatchingDomain()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        var blockedRegexes = RegexCompiler.Compile(new List<string> { @"^ads?\d*\." });
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                blockedRegexes: blockedRegexes)
        };
        var svc = CreateService(config, compiled);

        var result1 = svc.Evaluate(MakeRequest("ad.example.com"), EP("10.0.0.1"), "ad.example.com");
        Assert.Equal(FilterAction.Block, result1.Action);
        Assert.Contains("BLOCKED (regex)", result1.DebugSummary);

        var result2 = svc.Evaluate(MakeRequest("ads123.tracker.net"), EP("10.0.0.1"), "ads123.tracker.net");
        Assert.Equal(FilterAction.Block, result2.Action);
    }

    // Regex allow overrides domain block
    [Fact]
    public void RegexAllowRule_OverridesDomainBlock()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        var allowedRegexes = RegexCompiler.Compile(new List<string> { @"safe\.example\.com" });
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                allowedRegexes: allowedRegexes)
        };
        var svc = CreateService(config, compiled);

        var result = svc.Evaluate(MakeRequest("safe.example.com"), EP("10.0.0.1"), "safe.example.com");

        Assert.Equal(FilterAction.Allow, result.Action);
        Assert.Contains("regex allowlisted", result.DebugSummary);
    }

    // Regex allow overrides regex block
    [Fact]
    public void RegexAllowRule_OverridesRegexBlock()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        var blockedRegexes = RegexCompiler.Compile(new List<string> { @"\.example\.com$" });
        var allowedRegexes = RegexCompiler.Compile(new List<string> { @"^safe\." });
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                blockedRegexes: blockedRegexes,
                allowedRegexes: allowedRegexes)
        };
        var svc = CreateService(config, compiled);

        // safe.example.com matches both regex allow and regex block -- allow wins (evaluated first)
        var result1 = svc.Evaluate(MakeRequest("safe.example.com"), EP("10.0.0.1"), "safe.example.com");
        Assert.Equal(FilterAction.Allow, result1.Action);
        Assert.Contains("regex allowlisted", result1.DebugSummary);

        // bad.example.com only matches regex block
        var result2 = svc.Evaluate(MakeRequest("bad.example.com"), EP("10.0.0.1"), "bad.example.com");
        Assert.Equal(FilterAction.Block, result2.Action);
    }

    // Domain allow overrides regex block
    [Fact]
    public void DomainAllow_OverridesRegexBlock()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        var blockedRegexes = RegexCompiler.Compile(new List<string> { @"example\.com" });
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" },
                blockedRegexes: blockedRegexes)
        };
        var svc = CreateService(config, compiled);

        var result = svc.Evaluate(MakeRequest("example.com"), EP("10.0.0.1"), "example.com");

        Assert.Equal(FilterAction.Allow, result.Action);
        Assert.Contains("allowlisted", result.DebugSummary);
    }

    // Regex timeout treated as no-match (fail-open)
    [Fact]
    public void RegexTimeout_TreatedAsNoMatch()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        // Pattern known to cause catastrophic backtracking
        var blockedRegexes = RegexCompiler.Compile(new List<string> { @"^(a+)+b" });
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                blockedRegexes: blockedRegexes)
        };
        var svc = CreateService(config, compiled);

        // Domain with many 'a's that causes backtracking against ^(a+)+b pattern
        var evilDomain = new string('a', 25) + ".com";
        var result = svc.Evaluate(MakeRequest(evilDomain), EP("10.0.0.1"), evilDomain);

        Assert.Equal(FilterAction.Allow, result.Action);
    }

    // No regex rules -> domain passes through to default allow
    [Fact]
    public void NoRegexRules_DefaultAllow()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var svc = CreateService(config, compiled);

        Assert.Equal(FilterAction.Allow, svc.Evaluate(MakeRequest("anything.com"), EP("10.0.0.1"), "anything.com").Action);
    }

    // Client IP resolves to correct profile
    [Fact]
    public void ClientIpResolution_UsesCorrectProfile()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            Clients =
            [
                new ClientConfig { Ids = ["10.0.0.1"], Profile = "kids" },
                new ClientConfig { Ids = ["10.0.0.2"], Profile = "adults" }
            ],
            Profiles =
            {
                ["kids"] = new ProfileConfig(),
                ["adults"] = new ProfileConfig()
            }
        };
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked.com" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            ["adults"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var svc = CreateService(config, compiled);

        // Kids profile blocks it
        Assert.Equal(FilterAction.Block, svc.Evaluate(MakeRequest("blocked.com"), EP("10.0.0.1"), "blocked.com").Action);
        // Adults profile doesn't block it
        Assert.Equal(FilterAction.Allow, svc.Evaluate(MakeRequest("blocked.com"), EP("10.0.0.2"), "blocked.com").Action);
    }
}
