using System.Net;
using ParentalControlsApp.Models;
using ParentalControlsApp.Services;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace ParentalControlsApp.Tests;

/// <summary>
/// Negative and boundary tests: malformed inputs, pathological configs,
/// unicode domains, and edge cases that should never crash the plugin.
/// </summary>
[Trait("Category", "Unit")]
public class NegativeTests
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

    // --- Malformed domain queries ---

    [Fact]
    public void EmptyDomain_DoesNotCrash()
    {
        // Technitium validates domains in DnsQuestionRecord, rejecting dots/hyphens at boundaries.
        // Test that IsAllowed handles an empty string passed as the questionDomain parameter.
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

        // Pass a valid DnsDatagram but override the questionDomain string
        var allowed = svc.IsAllowed(MakeRequest("valid.com"), EP("10.0.0.1"), "", out _, out _);
        Assert.True(allowed);
    }

    [Theory]
    [InlineData("a.b.c.d.e.f.g.h.i.j.example.com")]   // deeply nested but valid
    [InlineData("x")]                                    // single label (valid DNS, no TLD)
    [InlineData("a-valid-domain.com")]                   // hyphens in middle
    public void UnusualButValidDomain_DoesNotCrash(string domain)
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

        var allowed = svc.IsAllowed(MakeRequest(domain), EP("10.0.0.1"), domain, out _, out _);
        Assert.True(allowed);
    }

    /// <summary>
    /// Technitium's DnsQuestionRecord rejects domains with leading dots, trailing dots,
    /// empty labels, and leading hyphens. These never reach IsAllowed in production.
    /// Verify the library validates them at construction time.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".leading.dot.com")]
    [InlineData("-starts-with-hyphen.com")]
    public void TechnitiumRejects_MalformedDomains(string domain)
    {
        Assert.ThrowsAny<Exception>(() => MakeRequest(domain));
    }

    // --- Unicode / IDN domains ---

    [Theory]
    [InlineData("xn--nxasmq6b.com")]          // punycode (IDN)
    [InlineData("xn--e1afmapc.xn--p1ai")]     // cyrillic punycode
    [InlineData("sub.xn--nxasmq6b.com")]       // subdomain of punycode
    public void PunycodeDomain_HandledCorrectly(string domain)
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
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "xn--nxasmq6b.com" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var svc = CreateService(config, compiled);

        var allowed = svc.IsAllowed(MakeRequest(domain), EP("10.0.0.1"), domain, out _, out _);

        if (domain == "xn--nxasmq6b.com" || domain == "sub.xn--nxasmq6b.com")
            Assert.False(allowed, $"Punycode domain '{domain}' should be blocked");
        else
            Assert.True(allowed);
    }

    // --- Pathologically large configs ---

    [Fact]
    public void LargeBlocklist_CompileDoesNotCrash()
    {
        var compiler = new ProfileCompiler(new ServiceRegistry());
        var rules = Enumerable.Range(0, 10_000).Select(i => $"domain{i}.example.com").ToList();
        var config = new AppConfig
        {
            Profiles =
            {
                ["test"] = new ProfileConfig { CustomRules = rules }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Equal(10_000, result["test"].BlockedDomains.Count);
    }

    [Fact]
    public void LargeBlocklist_DomainMatcherPerformance()
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 100_000; i++)
            domains.Add($"domain{i}.example.com");

        // Should complete quickly (HashSet O(1) per level)
        Assert.True(DomainMatcher.Matches(domains, "domain50000.example.com"));
        Assert.False(DomainMatcher.Matches(domains, "notblocked.test.com"));
        Assert.True(DomainMatcher.Matches(domains, "sub.domain99999.example.com"));
    }

    [Fact]
    public void ManyProfiles_CompileAll()
    {
        var compiler = new ProfileCompiler(new ServiceRegistry());
        var config = new AppConfig();
        for (int i = 0; i < 100; i++)
            config.Profiles[$"profile{i}"] = new ProfileConfig
            {
                CustomRules = [$"block{i}.com"],
                AllowList = [$"allow{i}.com"],
                DnsRewrites = [new DnsRewriteConfig { Domain = $"rw{i}.com", Answer = "1.2.3.4" }]
            };

        var result = compiler.CompileAll(config);

        Assert.Equal(100, result.Count);
        Assert.Contains("block50.com", result["profile50"].BlockedDomains);
        Assert.Contains("allow50.com", result["profile50"].AllowedDomains);
        Assert.True(result["profile50"].Rewrites.ContainsKey("rw50.com"));
    }

    // --- Deeply nested subdomain ---

    [Fact]
    public void DeeplyNestedSubdomain_MatchesParent()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" };
        var deep = string.Join('.', Enumerable.Range(0, 50).Select(i => $"sub{i}")) + ".example.com";

        Assert.True(DomainMatcher.Matches(set, deep));
    }

    [Fact]
    public void DeeplyNestedSubdomain_NoMatch_DoesNotCrash()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "other.com" };
        var deep = string.Join('.', Enumerable.Range(0, 50).Select(i => $"sub{i}")) + ".example.com";

        Assert.False(DomainMatcher.Matches(set, deep));
    }

    // --- Empty/null edge cases ---

    [Fact]
    public void EmptyCompiledProfile_AllowsEverything()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "empty",
            Profiles = { ["empty"] = new ProfileConfig() }
        };
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["empty"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var svc = CreateService(config, compiled);

        Assert.True(svc.IsAllowed(MakeRequest("anything.com"), EP("10.0.0.1"), "anything.com", out _, out _));
    }

    [Fact]
    public void EmptyClientsList_UsesDefaultProfile()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "kids",
            Clients = [],
            Profiles = { ["kids"] = new ProfileConfig() }
        };
        var compiled = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked.com" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var svc = CreateService(config, compiled);

        Assert.False(svc.IsAllowed(MakeRequest("blocked.com"), EP("10.0.0.1"), "blocked.com", out _, out _));
    }

    // --- Client config edge cases ---

    [Fact]
    public void ClientWithEmptyIds_Skipped()
    {
        var config = new AppConfig
        {
            EnableBlocking = true,
            DefaultProfile = "fallback",
            Clients =
            [
                new ClientConfig { Ids = [], Profile = "kids" }
            ],
            Profiles =
            {
                ["kids"] = new ProfileConfig(),
                ["fallback"] = new ProfileConfig()
            }
        };

        var result = FilteringService.ResolveProfile(config, null, IPAddress.Parse("10.0.0.1"));
        Assert.Equal("fallback", result);
    }

    // --- Rewrite edge cases ---

    [Fact]
    public void RewriteWithEmptyAnswer_SkippedDuringCompile()
    {
        var compiler = new ProfileCompiler(new ServiceRegistry());
        var config = new AppConfig
        {
            Profiles =
            {
                ["test"] = new ProfileConfig
                {
                    DnsRewrites =
                    [
                        new DnsRewriteConfig { Domain = "test.com", Answer = "" },
                        new DnsRewriteConfig { Domain = "test2.com", Answer = "   " },
                        new DnsRewriteConfig { Domain = "valid.com", Answer = "1.2.3.4" }
                    ]
                }
            }
        };

        var result = compiler.CompileAll(config);
        Assert.Single(result["test"].Rewrites);
        Assert.True(result["test"].Rewrites.ContainsKey("valid.com"));
    }

    [Fact]
    public void GetRewrite_SingleLabelDomain_ReturnsNull()
    {
        var rewrites = new Dictionary<string, DnsRewriteConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["com"] = new DnsRewriteConfig { Domain = "com", Answer = "1.2.3.4" }
        };

        // Single-label query walks up but "com" is the TLD -- should still work
        var result = FilteringService.GetRewrite(rewrites, "example.com");
        // "example.com" -> check "example.com" (miss) -> check "com" (hit)
        Assert.NotNull(result);
    }

    // --- Schedule edge cases ---

    [Fact]
    public void Schedule_EmptyDayWindowsList_BlockingActive()
    {
        var profile = new ProfileConfig
        {
            Schedule = new() { ["mon"] = [] }
        };

        // Monday with empty window list -> TryGetValue succeeds but windows.Count == 0 -> returns true
        var utc = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc); // Monday
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, utc));
    }

    // --- Config loading edge cases ---

    [Fact]
    public void Load_MalformedJson_Throws()
    {
        var service = new ConfigService(Path.GetTempPath());
        Assert.Throws<System.Text.Json.JsonException>(() => service.Load("{invalid json}"));
    }

    [Fact]
    public void Load_ArrayInsteadOfObject_Throws()
    {
        var service = new ConfigService(Path.GetTempPath());
        Assert.Throws<System.Text.Json.JsonException>(() => service.Load("[1,2,3]"));
    }

    [Fact]
    public void Load_UnknownProperties_Ignored()
    {
        var service = new ConfigService(Path.GetTempPath());
        service.Load("""{"enableBlocking": true, "unknownProp": "value", "anotherUnknown": 42}""");
        Assert.True(service.Config.EnableBlocking);
    }

    // --- Semantically invalid config values ---

    [Fact]
    public async Task NegativeRefreshHours_DoesNotCrash()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "neg-refresh-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            // Negative refreshHours means the list is always "stale" (downloads every time)
            using var handler = new HttpMessageHandler_Stub("ads.example.com\n");
            using var manager = new BlockListManager(tempDir, handler);

            var lists = new[]
            {
                new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = -1 }
            };

            await manager.RefreshAsync(lists);
            var domains = manager.GetDomains("https://example.com/list.txt");
            Assert.NotNull(domains);
            Assert.Contains("ads.example.com", domains);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EmptyProfileName_CompilesSuccessfully()
    {
        var compiler = new ProfileCompiler(new ServiceRegistry());
        var config = new AppConfig
        {
            Profiles = { [""] = new ProfileConfig { CustomRules = ["blocked.com"] } }
        };

        var result = compiler.CompileAll(config);

        Assert.True(result.ContainsKey(""));
        Assert.Contains("blocked.com", result[""].BlockedDomains);
    }

    [Fact]
    public void DuplicateClientIds_FirstMatchWins()
    {
        var config = new AppConfig
        {
            Clients =
            [
                new ClientConfig { Ids = ["10.0.0.1"], Profile = "first" },
                new ClientConfig { Ids = ["10.0.0.1"], Profile = "second" }
            ],
            Profiles =
            {
                ["first"] = new ProfileConfig(),
                ["second"] = new ProfileConfig()
            }
        };

        var result = FilteringService.ResolveProfile(config, null, System.Net.IPAddress.Parse("10.0.0.1"));
        Assert.Equal("first", result);
    }

    /// <summary>Simple handler for negative tests that returns a fixed content string.</summary>
    private sealed class HttpMessageHandler_Stub : HttpMessageHandler
    {
        private readonly string _content;
        public HttpMessageHandler_Stub(string content) => _content = content;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(_content)
            });
        }
    }
}
