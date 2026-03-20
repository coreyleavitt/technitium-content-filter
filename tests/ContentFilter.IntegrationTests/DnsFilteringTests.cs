using ContentFilter.TestInfrastructure;

namespace ContentFilter.IntegrationTests;

/// <summary>
/// Integration tests that verify end-to-end DNS filtering behavior
/// against a real Technitium DNS server with the plugin installed.
///
/// These tests start a Technitium container via Testcontainers,
/// deploy the plugin, configure it, and send real DNS queries.
/// </summary>
[Collection("Technitium")]
[Trait("Category", "Integration")]
public class DnsFilteringTests
{
    private readonly ContentFilterFixture _fixture;

    public DnsFilteringTests(ContentFilterFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task BlockedDomain_ReturnsNxDomain()
    {
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new { customRules = new[] { "blocked.example.com" } }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        var response = await _fixture.QueryAsync("blocked.example.com");

        Assert.True(response.IsNxDomain, $"Expected NXDOMAIN, got rcode={response.ResponseCode}");
        Assert.Empty(response.Answers);
    }

    [Fact]
    public async Task SubdomainOfBlockedDomain_ReturnsNxDomain()
    {
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new { customRules = new[] { "blocked.example.com" } }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        var response = await _fixture.QueryAsync("sub.blocked.example.com");

        Assert.True(response.IsNxDomain, $"Expected NXDOMAIN for subdomain, got rcode={response.ResponseCode}");
    }

    [Fact]
    public async Task AllowedDomain_Resolves()
    {
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new { customRules = new[] { "blocked.example.com" } }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        var response = await _fixture.QueryAsync("example.com");

        Assert.False(response.IsNxDomain, "Allowed domain should not be NXDOMAIN");
    }

    [Fact]
    public async Task DnsRewrite_ReturnsCname()
    {
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new
                {
                    dnsRewrites = new[]
                    {
                        new { domain = "youtube.com", answer = "restrict.youtube.com" }
                    }
                }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        var response = await _fixture.QueryAsync("youtube.com");

        Assert.NotNull(response.CnameTarget);
        Assert.Equal("restrict.youtube.com", response.CnameTarget);
    }

    [Fact]
    public async Task DnsRewrite_SubdomainMatchesParent()
    {
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new
                {
                    dnsRewrites = new[]
                    {
                        new { domain = "youtube.com", answer = "restrict.youtube.com" }
                    }
                }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        var response = await _fixture.QueryAsync("www.youtube.com");

        Assert.NotNull(response.CnameTarget);
        Assert.Equal("restrict.youtube.com", response.CnameTarget);
    }

    [Fact]
    public async Task DnsRewrite_IpAnswer_ReturnsARecord()
    {
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new
                {
                    dnsRewrites = new[]
                    {
                        new { domain = "custom.local", answer = "192.168.1.100" }
                    }
                }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        var response = await _fixture.QueryAsync("custom.local");

        Assert.True(response.IsNoError);
        var aRecord = response.Answers.FirstOrDefault(a => a.IsA);
        Assert.NotNull(aRecord);
        Assert.Equal("192.168.1.100", aRecord.Data);
    }

    [Fact]
    public async Task AllowlistOverridesBlock()
    {
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new
                {
                    customRules = new[] { "example.com" },
                    allowList = new[] { "example.com" }
                }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        var response = await _fixture.QueryAsync("example.com");

        Assert.False(response.IsNxDomain, "Allowlisted domain should not be blocked");
    }

    [Fact]
    public async Task BlockingDisabled_AllowsEverything()
    {
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = false,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new { customRules = new[] { "blocked.example.com" } }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        var response = await _fixture.QueryAsync("blocked.example.com");

        Assert.False(response.IsNxDomain, "Blocking disabled -- nothing should be blocked");
    }

    [Fact]
    public async Task NoMatchingProfile_AllowsQuery()
    {
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new { customRules = new[] { "blocked.example.com" } }
            }
            // No clients configured -- no profile match
        });

        var response = await _fixture.QueryAsync("blocked.example.com");

        Assert.False(response.IsNxDomain, "No profile match -- should allow");
    }

    [Fact]
    public async Task BaseProfile_MergesIntoChildProfile()
    {
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            baseProfile = "base",
            profiles = new Dictionary<string, object>
            {
                ["base"] = new { customRules = new[] { "base-blocked.example.com" } },
                ["kids"] = new { customRules = new[] { "kids-blocked.example.com" } }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        // Base block inherited by kids
        var baseResponse = await _fixture.QueryAsync("base-blocked.example.com");
        Assert.True(baseResponse.IsNxDomain, "Base blocked domain should be blocked for kids profile");

        // Kids own block works too
        var kidsResponse = await _fixture.QueryAsync("kids-blocked.example.com");
        Assert.True(kidsResponse.IsNxDomain, "Kids blocked domain should be blocked");
    }

    [Fact]
    public async Task RewriteTtl_Is300Seconds()
    {
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new
                {
                    dnsRewrites = new[]
                    {
                        new { domain = "ttl-test.local", answer = "10.0.0.1" }
                    }
                }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        var response = await _fixture.QueryAsync("ttl-test.local");

        var aRecord = response.Answers.FirstOrDefault(a => a.IsA);
        Assert.NotNull(aRecord);
        Assert.Equal(300u, aRecord.Ttl);
    }

    [Fact]
    public async Task BlockedService_ExpandsToServiceDomains()
    {
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new { blockedServices = new[] { "youtube" } }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        var response = await _fixture.QueryAsync("youtube.com");

        Assert.True(response.IsNxDomain, "YouTube service should be blocked when 'youtube' service is blocked");
    }

    [Fact]
    public async Task CidrClientResolution_MatchesProfile()
    {
        // Use 0.0.0.0/0 as CIDR to match all clients
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["cidr-profile"] = new { customRules = new[] { "cidr-blocked.example.com" } }
            },
            clients = new[]
            {
                new { name = "all-cidr", ids = new[] { "0.0.0.0/0" }, profile = "cidr-profile" }
            }
        });

        var response = await _fixture.QueryAsync("cidr-blocked.example.com");

        Assert.True(response.IsNxDomain, "CIDR client resolution should match and apply profile");
    }

    [Fact]
    public async Task MultipleProfiles_CorrectProfileApplied()
    {
        // Non-matching /32 profile vs catch-all profile -- catch-all should apply
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["restrictive"] = new { customRules = new[] { "multi-blocked.example.com" } },
                ["permissive"] = new { customRules = Array.Empty<string>() }
            },
            clients = new[]
            {
                new { name = "specific", ids = new[] { "198.51.100.1" }, profile = "permissive" },
                new { name = "everyone", ids = new[] { "0.0.0.0/0" }, profile = "restrictive" }
            }
        });

        // Our test queries come from an IP other than 198.51.100.1,
        // so the catch-all restrictive profile applies
        var response = await _fixture.QueryAsync("multi-blocked.example.com");

        Assert.True(response.IsNxDomain, "Catch-all restrictive profile should block the domain");
    }

    [Fact]
    public async Task RegexBlockRule_BlocksMatchingDomain()
    {
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new { regexBlockRules = new[] { @"^ads?\d*\." } }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        var response = await _fixture.QueryAsync("ad.example.com");

        Assert.True(response.IsNxDomain, $"Regex block should return NXDOMAIN, got rcode={response.ResponseCode}");
    }

    [Fact]
    public async Task RegexAllowRule_OverridesBlock()
    {
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new
                {
                    customRules = new[] { "example.com" },
                    regexAllowRules = new[] { @"^safe\." }
                }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        var response = await _fixture.QueryAsync("safe.example.com");

        Assert.False(response.IsNxDomain, "Regex allow should override domain block");
    }

    [Fact]
    public async Task ConfigReload_TakesEffectImmediately()
    {
        // First config: block the domain
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new { customRules = new[] { "reload-test.example.com" } }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        var blocked = await _fixture.QueryAsync("reload-test.example.com");
        Assert.True(blocked.IsNxDomain, "Should be blocked initially");

        // Second config: remove the block
        await _fixture.SetConfigAsync(new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["kids"] = new { customRules = Array.Empty<string>() }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "kids" }
            }
        });

        var allowed = await _fixture.QueryAsync("reload-test.example.com");
        Assert.False(allowed.IsNxDomain, "Should be allowed after config reload");
    }
}
