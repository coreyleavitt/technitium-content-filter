using System.Net;
using ContentFilter.Models;
using ContentFilter.Services;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace ContentFilter.Tests;

/// <summary>
/// Issue #34: Integration test for the full 8-step filtering pipeline.
/// Exercises all evaluation steps end-to-end using real objects (no mocks for services).
/// </summary>
[Trait("Category", "Unit")]
public class FullPipelineTests
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

    private static (FilteringService Service, Dictionary<string, CompiledProfile> Compiled)
        CreateFullPipeline(AppConfig config)
    {
        var registry = new ServiceRegistry();
        registry.MergeCustomServices(config.CustomServices);
        var compiler = new ProfileCompiler(registry);
        var compiled = compiler.CompileAll(config);
        var configSvc = CreateConfig(config);
        var svc = new FilteringService(configSvc);
        svc.UpdateCompiledProfiles(compiled);
        return (svc, compiled);
    }

    private static DnsDatagram MakeRequest(string domain)
    {
        var question = new DnsQuestionRecord(domain, DnsResourceRecordType.A, DnsClass.IN);
        return new DnsDatagram(
            0, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, new[] { question });
    }

    private static IPEndPoint EP(string ip) => new(IPAddress.Parse(ip), 53);

    // --- Step 1: Global blocking disabled ---

    [Fact]
    public void Step1_BlockingDisabled_AllowsBlockedDomain()
    {
        var config = new AppConfig
        {
            EnableBlocking = false,
            DefaultProfile = "kids",
            Profiles =
            {
                ["kids"] = new ProfileConfig { CustomRules = ["blocked.com"] }
            }
        };
        var (svc, _) = CreateFullPipeline(config);

        var allowed = svc.IsAllowed(MakeRequest("blocked.com"), EP("10.0.0.1"), "blocked.com", out var debug, out _);

        Assert.True(allowed);
        Assert.Contains("blocking disabled", debug);
    }

    // --- Step 2: Client resolution (DoT client ID, exact IP, CIDR) ---

    [Fact]
    public void Step2_ExactIpResolution_CorrectProfile()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            Clients =
            [
                new ClientConfig { Ids = ["10.0.0.1"], Profile = "strict" },
                new ClientConfig { Ids = ["10.0.0.2"], Profile = "relaxed" }
            ],
            Profiles =
            {
                ["strict"] = new ProfileConfig { CustomRules = ["games.com"] },
                ["relaxed"] = new ProfileConfig()
            }
        };
        var (svc, _) = CreateFullPipeline(config);

        // Strict profile blocks games.com
        Assert.False(svc.IsAllowed(MakeRequest("games.com"), EP("10.0.0.1"), "games.com", out _, out _));
        // Relaxed profile allows games.com
        Assert.True(svc.IsAllowed(MakeRequest("games.com"), EP("10.0.0.2"), "games.com", out _, out _));
    }

    [Fact]
    public void Step2_CidrResolution_LongestPrefixWins()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            Clients =
            [
                new ClientConfig { Ids = ["192.168.0.0/16"], Profile = "broad" },
                new ClientConfig { Ids = ["192.168.1.0/24"], Profile = "narrow" }
            ],
            Profiles =
            {
                ["broad"] = new ProfileConfig(),
                ["narrow"] = new ProfileConfig { CustomRules = ["blocked.com"] }
            }
        };
        var (svc, _) = CreateFullPipeline(config);

        // 192.168.1.50 matches both CIDRs, but /24 is more specific
        var allowed = svc.IsAllowed(MakeRequest("blocked.com"), EP("192.168.1.50"), "blocked.com", out var debug, out _);
        Assert.False(allowed);
        Assert.Contains("profile=narrow", debug);
    }

    // --- Step 3: Profile lookup (fallback to base profile) ---

    [Fact]
    public void Step3_NoProfileMatch_FallsBackToBase()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            BaseProfile = "base",
            Profiles =
            {
                ["base"] = new ProfileConfig { CustomRules = ["ads.com"] }
            }
        };
        var (svc, _) = CreateFullPipeline(config);

        // No client matches, no default profile, falls back to base
        var allowed = svc.IsAllowed(MakeRequest("ads.com"), EP("10.0.0.1"), "ads.com", out _, out _);
        Assert.False(allowed);
    }

    [Fact]
    public void Step3_NoProfileNoBase_Allows()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            // No clients, no default, no base
        };
        var (svc, _) = CreateFullPipeline(config);

        var allowed = svc.IsAllowed(MakeRequest("anything.com"), EP("10.0.0.1"), "anything.com", out _, out _);
        Assert.True(allowed);
    }

    // --- Step 4: DNS rewrite matching ---

    [Fact]
    public void Step4_RewriteMatch_ReturnsRewriteConfig()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles =
            {
                ["kids"] = new ProfileConfig
                {
                    DnsRewrites = [new DnsRewriteConfig { Domain = "youtube.com", Answer = "restrict.youtube.com" }]
                }
            }
        };
        var (svc, _) = CreateFullPipeline(config);

        var allowed = svc.IsAllowed(MakeRequest("youtube.com"), EP("10.0.0.1"), "youtube.com", out var debug, out var rewrite);

        Assert.False(allowed);
        Assert.NotNull(rewrite);
        Assert.Equal("restrict.youtube.com", rewrite.Answer);
        Assert.Contains("REWRITE", debug);
    }

    [Fact]
    public void Step4_SubdomainRewrite_Matches()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles =
            {
                ["kids"] = new ProfileConfig
                {
                    DnsRewrites = [new DnsRewriteConfig { Domain = "youtube.com", Answer = "restrict.youtube.com" }]
                }
            }
        };
        var (svc, _) = CreateFullPipeline(config);

        var allowed = svc.IsAllowed(MakeRequest("www.youtube.com"), EP("10.0.0.1"), "www.youtube.com", out _, out var rewrite);
        Assert.False(allowed);
        Assert.NotNull(rewrite);
    }

    // --- Step 5: Allowlist check ---

    [Fact]
    public void Step5_AllowlistOverridesBlock()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles =
            {
                ["kids"] = new ProfileConfig
                {
                    CustomRules = ["example.com", "@@safe.example.com"],
                    AllowList = ["trusted.com"]
                }
            }
        };
        var (svc, _) = CreateFullPipeline(config);

        // Blocked domain
        Assert.False(svc.IsAllowed(MakeRequest("example.com"), EP("10.0.0.1"), "example.com", out _, out _));
        // Allowlisted via @@-rule
        Assert.True(svc.IsAllowed(MakeRequest("safe.example.com"), EP("10.0.0.1"), "safe.example.com", out var debug1, out _));
        Assert.Contains("allowlisted", debug1);
        // Allowlisted via allowList
        Assert.True(svc.IsAllowed(MakeRequest("trusted.com"), EP("10.0.0.1"), "trusted.com", out _, out _));
    }

    // --- Step 6: Schedule evaluation ---
    // (Schedule tests are limited here since we can't inject time; detailed tests in ScheduleTests.cs)

    [Fact]
    public void Step6_ScheduleAllDay_AlwaysBlocks()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            ScheduleAllDay = true,
            DefaultProfile = "kids",
            Profiles =
            {
                ["kids"] = new ProfileConfig
                {
                    CustomRules = ["blocked.com"],
                    Schedule = new()
                    {
                        ["mon"] = [new ScheduleConfig { AllDay = false, Start = "00:00", End = "00:01" }],
                        ["tue"] = [new ScheduleConfig { AllDay = false, Start = "00:00", End = "00:01" }],
                        ["wed"] = [new ScheduleConfig { AllDay = false, Start = "00:00", End = "00:01" }],
                        ["thu"] = [new ScheduleConfig { AllDay = false, Start = "00:00", End = "00:01" }],
                        ["fri"] = [new ScheduleConfig { AllDay = false, Start = "00:00", End = "00:01" }],
                        ["sat"] = [new ScheduleConfig { AllDay = false, Start = "00:00", End = "00:01" }],
                        ["sun"] = [new ScheduleConfig { AllDay = false, Start = "00:00", End = "00:01" }]
                    }
                }
            }
        };
        var (svc, _) = CreateFullPipeline(config);

        // ScheduleAllDay=true overrides the narrow window for all days
        var allowed = svc.IsAllowed(MakeRequest("blocked.com"), EP("10.0.0.1"), "blocked.com", out _, out _);
        Assert.False(allowed);
    }

    // --- Step 7: Blocklist check ---

    [Fact]
    public void Step7_BlockedDomain_Blocked()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles =
            {
                ["kids"] = new ProfileConfig { CustomRules = ["malware.com", "ads.example.com"] }
            }
        };
        var (svc, _) = CreateFullPipeline(config);

        Assert.False(svc.IsAllowed(MakeRequest("malware.com"), EP("10.0.0.1"), "malware.com", out _, out _));
        Assert.False(svc.IsAllowed(MakeRequest("ads.example.com"), EP("10.0.0.1"), "ads.example.com", out _, out _));
        // Subdomain of blocked domain is also blocked
        Assert.False(svc.IsAllowed(MakeRequest("sub.malware.com"), EP("10.0.0.1"), "sub.malware.com", out _, out _));
    }

    [Fact]
    public void Step7_BlockedService_DomainsExpanded()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            CustomServices = new Dictionary<string, BlockedServiceDefinition>
            {
                ["testservice"] = new() { Name = "TestService", Domains = ["service.example.com", "api.service.example.com"] }
            },
            Profiles =
            {
                ["kids"] = new ProfileConfig { BlockedServices = ["testservice"] }
            }
        };
        var (svc, _) = CreateFullPipeline(config);

        Assert.False(svc.IsAllowed(MakeRequest("service.example.com"), EP("10.0.0.1"), "service.example.com", out _, out _));
        Assert.False(svc.IsAllowed(MakeRequest("api.service.example.com"), EP("10.0.0.1"), "api.service.example.com", out _, out _));
    }

    // --- Step 8: Default allow ---

    [Fact]
    public void Step8_NoMatch_Allows()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles =
            {
                ["kids"] = new ProfileConfig { CustomRules = ["blocked.com"] }
            }
        };
        var (svc, _) = CreateFullPipeline(config);

        Assert.True(svc.IsAllowed(MakeRequest("allowed.com"), EP("10.0.0.1"), "allowed.com", out _, out _));
    }

    // --- Cross-step priority tests ---

    [Fact]
    public void RewriteBeatsAllowlistBeatsBlock()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Profiles =
            {
                ["kids"] = new ProfileConfig
                {
                    CustomRules = ["example.com", "@@example.com"],
                    DnsRewrites = [new DnsRewriteConfig { Domain = "example.com", Answer = "1.2.3.4" }]
                }
            }
        };
        var (svc, _) = CreateFullPipeline(config);

        // Rewrite takes priority over allowlist and block
        var allowed = svc.IsAllowed(MakeRequest("example.com"), EP("10.0.0.1"), "example.com", out var debug, out var rewrite);
        Assert.False(allowed);
        Assert.NotNull(rewrite);
        Assert.Contains("REWRITE", debug);
    }

    [Fact]
    public void BaseProfileMerge_InheritedBlocksAndAllows()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            BaseProfile = "base",
            DefaultProfile = "kids",
            Profiles =
            {
                ["base"] = new ProfileConfig
                {
                    CustomRules = ["ads.com"],
                    AllowList = ["safe.com"]
                },
                ["kids"] = new ProfileConfig
                {
                    CustomRules = ["games.com"]
                }
            }
        };
        var (svc, _) = CreateFullPipeline(config);

        // Kids inherits base blocks
        Assert.False(svc.IsAllowed(MakeRequest("ads.com"), EP("10.0.0.1"), "ads.com", out _, out _));
        // Kids has its own blocks
        Assert.False(svc.IsAllowed(MakeRequest("games.com"), EP("10.0.0.1"), "games.com", out _, out _));
        // Kids inherits base allows
        Assert.True(svc.IsAllowed(MakeRequest("safe.com"), EP("10.0.0.1"), "safe.com", out _, out _));
        // Unblocked domains pass through
        Assert.True(svc.IsAllowed(MakeRequest("wikipedia.org"), EP("10.0.0.1"), "wikipedia.org", out _, out _));
    }

    [Fact]
    public void CompleteEndToEnd_AllSteps()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            TimeZone = "UTC",
            ScheduleAllDay = true,
            BaseProfile = "base",
            DefaultProfile = "default-fallback",
            CustomServices = new Dictionary<string, BlockedServiceDefinition>
            {
                ["social"] = new() { Name = "Social Media", Domains = ["facebook.com", "instagram.com"] }
            },
            Clients =
            [
                new ClientConfig { Ids = ["192.168.1.10"], Profile = "kids" },
                new ClientConfig { Ids = ["192.168.1.20"], Profile = "adults" },
                new ClientConfig { Ids = ["192.168.2.0/24"], Profile = "guests" }
            ],
            Profiles =
            {
                ["base"] = new ProfileConfig
                {
                    CustomRules = ["malware.com", "phishing.net"]
                },
                ["kids"] = new ProfileConfig
                {
                    BlockedServices = ["social"],
                    CustomRules = ["games.com", "@@homework.games.com"],
                    DnsRewrites = [new DnsRewriteConfig { Domain = "youtube.com", Answer = "restrict.youtube.com" }],
                    Schedule = new()
                    {
                        ["mon"] = [new ScheduleConfig { AllDay = true }],
                        ["tue"] = [new ScheduleConfig { AllDay = true }],
                        ["wed"] = [new ScheduleConfig { AllDay = true }],
                        ["thu"] = [new ScheduleConfig { AllDay = true }],
                        ["fri"] = [new ScheduleConfig { AllDay = true }],
                        ["sat"] = [new ScheduleConfig { AllDay = true }],
                        ["sun"] = [new ScheduleConfig { AllDay = true }]
                    }
                },
                ["adults"] = new ProfileConfig(),
                ["guests"] = new ProfileConfig
                {
                    CustomRules = ["streaming.com"]
                },
                ["default-fallback"] = new ProfileConfig()
            }
        };

        var (svc, _) = CreateFullPipeline(config);

        // Kids: base blocks inherited
        Assert.False(svc.IsAllowed(MakeRequest("malware.com"), EP("192.168.1.10"), "malware.com", out _, out _));
        // Kids: service block
        Assert.False(svc.IsAllowed(MakeRequest("facebook.com"), EP("192.168.1.10"), "facebook.com", out _, out _));
        // Kids: custom rule block
        Assert.False(svc.IsAllowed(MakeRequest("games.com"), EP("192.168.1.10"), "games.com", out _, out _));
        // Kids: allowlist exception
        Assert.True(svc.IsAllowed(MakeRequest("homework.games.com"), EP("192.168.1.10"), "homework.games.com", out _, out _));
        // Kids: rewrite
        svc.IsAllowed(MakeRequest("youtube.com"), EP("192.168.1.10"), "youtube.com", out _, out var rewrite);
        Assert.NotNull(rewrite);
        Assert.Equal("restrict.youtube.com", rewrite.Answer);

        // Adults: base blocks inherited, but no own blocks
        Assert.False(svc.IsAllowed(MakeRequest("malware.com"), EP("192.168.1.20"), "malware.com", out _, out _));
        Assert.True(svc.IsAllowed(MakeRequest("facebook.com"), EP("192.168.1.20"), "facebook.com", out _, out _));

        // Guests (CIDR match): base blocks + own blocks
        Assert.False(svc.IsAllowed(MakeRequest("malware.com"), EP("192.168.2.50"), "malware.com", out _, out _));
        Assert.False(svc.IsAllowed(MakeRequest("streaming.com"), EP("192.168.2.50"), "streaming.com", out _, out _));
        Assert.True(svc.IsAllowed(MakeRequest("facebook.com"), EP("192.168.2.50"), "facebook.com", out _, out _));

        // Unknown IP falls back to default-fallback: only base blocks
        Assert.False(svc.IsAllowed(MakeRequest("malware.com"), EP("10.0.0.99"), "malware.com", out _, out _));
        Assert.True(svc.IsAllowed(MakeRequest("facebook.com"), EP("10.0.0.99"), "facebook.com", out _, out _));
    }
}
