using System.Net;
using DnsServerCore.ApplicationCommon;
using NSubstitute;
using ContentFilter.Models;
using ContentFilter.Services;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace ContentFilter.Tests;

/// <summary>
/// Tests the App plugin class (main entry point) including initialization,
/// IsAllowedAsync, ProcessRequestAsync, and the rewrite flow.
/// </summary>
[Trait("Category", "Unit")]
public class AppTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IDnsServer _mockServer;

    public AppTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "app-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _mockServer = Substitute.For<IDnsServer>();
        _mockServer.ApplicationFolder.Returns(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static DnsDatagram MakeRequest(string domain, ushort id = 1)
    {
        var question = new DnsQuestionRecord(domain, DnsResourceRecordType.A, DnsClass.IN);
        return new DnsDatagram(
            id, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, new[] { question });
    }

    private static IPEndPoint EP(string ip = "10.0.0.1") => new(IPAddress.Parse(ip), 53);

    private async Task<ContentFilter.App> CreateInitializedApp(string configJson)
    {
        var app = new ContentFilter.App();
        await app.InitializeAsync(_mockServer, configJson);
        return app;
    }

    [Fact]
    public async Task InitializeAsync_LogsInitialization()
    {
        var config = """{"enableBlocking": true}""";

        using var app = await CreateInitializedApp(config);

        _mockServer.Received().WriteLog(Arg.Is<string>(s => s.Contains("initialized")));
    }

    [Fact]
    public async Task InitializeAsync_ReInit_DoesNotThrow()
    {
        var config = """{"enableBlocking": true}""";

        using var app = await CreateInitializedApp(config);
        // Re-initialize with different config (simulates Technitium hot reload)
        await app.InitializeAsync(_mockServer, """{"enableBlocking": false}""");

        _mockServer.Received(2).WriteLog(Arg.Is<string>(s => s.Contains("initialized")));
    }

    [Fact]
    public async Task IsAllowedAsync_EmptyQuestionList_ReturnsTrue()
    {
        using var app = await CreateInitializedApp("""{"enableBlocking": true}""");

        var request = new DnsDatagram(
            0, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, Array.Empty<DnsQuestionRecord>());

        var result = await app.IsAllowedAsync(request, EP());

        Assert.True(result);
    }

    [Fact]
    public async Task IsAllowedAsync_BlockedDomain_ReturnsFalse()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": { "kids": { "customRules": ["blocked.com"] } }
        }
        """;

        using var app = await CreateInitializedApp(config);
        var result = await app.IsAllowedAsync(MakeRequest("blocked.com"), EP());

        Assert.False(result);
        _mockServer.Received().WriteLog(Arg.Is<string>(s => s.Contains("BLOCKED")));
    }

    [Fact]
    public async Task IsAllowedAsync_AllowedDomain_ReturnsTrue()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": { "kids": { "customRules": ["blocked.com"] } }
        }
        """;

        using var app = await CreateInitializedApp(config);
        var result = await app.IsAllowedAsync(MakeRequest("safe.com"), EP());

        Assert.True(result);
    }

    [Fact]
    public async Task IsAllowedAsync_Rewrite_ReturnsFalseAndLogs()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [{ "domain": "youtube.com", "answer": "restrict.youtube.com" }]
                }
            }
        }
        """;

        using var app = await CreateInitializedApp(config);
        var result = await app.IsAllowedAsync(MakeRequest("youtube.com"), EP());

        Assert.False(result);
        _mockServer.Received().WriteLog(Arg.Is<string>(s => s.Contains("REWRITE")));
    }

    [Fact]
    public async Task ProcessRequestAsync_NoPendingRewrite_ReturnsNxDomain()
    {
        using var app = await CreateInitializedApp("""{"enableBlocking": true}""");

        var request = MakeRequest("anything.com", id: 999);
        var response = await app.ProcessRequestAsync(request, EP());

        Assert.Equal(DnsResponseCode.NxDomain, response.RCODE);
    }

    [Fact]
    public async Task FullFlow_Rewrite_ProducesCnameResponse()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [{ "domain": "youtube.com", "answer": "restrict.youtube.com" }]
                }
            }
        }
        """;

        using var app = await CreateInitializedApp(config);
        var request = MakeRequest("youtube.com", id: 42);

        // IsAllowed stores the rewrite in _pendingRewrites
        var allowed = await app.IsAllowedAsync(request, EP());
        Assert.False(allowed);

        // ProcessRequest retrieves the rewrite and builds a CNAME response
        var response = await app.ProcessRequestAsync(request, EP());

        Assert.Equal(DnsResponseCode.NoError, response.RCODE);
        Assert.True(response.Answer.Count > 0);
        var cnameRecord = response.Answer[0].RDATA as DnsCNAMERecordData;
        Assert.NotNull(cnameRecord);
        Assert.Equal("restrict.youtube.com", cnameRecord.Domain);
    }

    [Fact]
    public async Task FullFlow_RewriteIp_ProducesARecord()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [{ "domain": "custom.local", "answer": "192.168.1.100" }]
                }
            }
        }
        """;

        using var app = await CreateInitializedApp(config);
        var request = MakeRequest("custom.local", id: 43);

        await app.IsAllowedAsync(request, EP());
        var response = await app.ProcessRequestAsync(request, EP());

        Assert.Equal(DnsResponseCode.NoError, response.RCODE);
        Assert.True(response.Answer.Count > 0);
        var aRecord = response.Answer[0].RDATA as DnsARecordData;
        Assert.NotNull(aRecord);
        Assert.Equal(IPAddress.Parse("192.168.1.100"), aRecord.Address);
    }

    [Fact]
    public async Task FullFlow_RewriteIpv6_ProducesAaaaRecord()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [{ "domain": "v6.local", "answer": "fd00::1" }]
                }
            }
        }
        """;

        using var app = await CreateInitializedApp(config);
        // Query with AAAA type
        var question = new DnsQuestionRecord("v6.local", DnsResourceRecordType.AAAA, DnsClass.IN);
        var request = new DnsDatagram(
            44, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, new[] { question });

        await app.IsAllowedAsync(request, EP());
        var response = await app.ProcessRequestAsync(request, EP());

        Assert.Equal(DnsResponseCode.NoError, response.RCODE);
        Assert.True(response.Answer.Count > 0);
        var aaaaRecord = response.Answer[0].RDATA as DnsAAAARecordData;
        Assert.NotNull(aaaaRecord);
        Assert.Equal(IPAddress.Parse("fd00::1"), aaaaRecord.Address);
    }

    [Fact]
    public async Task FullFlow_Block_ProducesNxDomain()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": { "kids": { "customRules": ["blocked.com"] } }
        }
        """;

        using var app = await CreateInitializedApp(config);
        var request = MakeRequest("blocked.com", id: 45);

        var allowed = await app.IsAllowedAsync(request, EP());
        Assert.False(allowed);

        // No pending rewrite, so ProcessRequest returns NxDomain
        var response = await app.ProcessRequestAsync(request, EP());
        Assert.Equal(DnsResponseCode.NxDomain, response.RCODE);
    }

    [Fact]
    public async Task ProcessRequestAsync_RewriteConsumedOnce()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [{ "domain": "youtube.com", "answer": "restrict.youtube.com" }]
                }
            }
        }
        """;

        using var app = await CreateInitializedApp(config);
        var request = MakeRequest("youtube.com", id: 46);

        await app.IsAllowedAsync(request, EP());

        // First ProcessRequest consumes the rewrite
        var response1 = await app.ProcessRequestAsync(request, EP());
        Assert.Equal(DnsResponseCode.NoError, response1.RCODE);

        // Second ProcessRequest with same ID has no pending rewrite -> NxDomain
        var response2 = await app.ProcessRequestAsync(request, EP());
        Assert.Equal(DnsResponseCode.NxDomain, response2.RCODE);
    }

    [Fact]
    public async Task Dispose_DoesNotThrow()
    {
        var app = await CreateInitializedApp("""{"enableBlocking": true}""");
        app.Dispose();
        // Calling dispose again should also be safe
        app.Dispose();
    }

    [Fact]
    public async Task RewriteResponse_HasTtl300()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [{ "domain": "test.com", "answer": "10.0.0.1" }]
                }
            }
        }
        """;

        using var app = await CreateInitializedApp(config);
        var request = MakeRequest("test.com", id: 47);

        await app.IsAllowedAsync(request, EP());
        var response = await app.ProcessRequestAsync(request, EP());

        Assert.Equal(300u, response.Answer[0].OriginalTtlValue);
    }

    [Fact]
    public async Task RewriteIpv4_WrongQueryType_EmptyAnswer()
    {
        // Rewrite has an IPv4 answer but query is AAAA
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [{ "domain": "v4only.local", "answer": "10.0.0.1" }]
                }
            }
        }
        """;

        using var app = await CreateInitializedApp(config);
        var question = new DnsQuestionRecord("v4only.local", DnsResourceRecordType.AAAA, DnsClass.IN);
        var request = new DnsDatagram(
            48, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, new[] { question });

        await app.IsAllowedAsync(request, EP());
        var response = await app.ProcessRequestAsync(request, EP());

        // IPv4 rewrite for AAAA query produces NoError but empty answer
        Assert.Equal(DnsResponseCode.NoError, response.RCODE);
        Assert.Empty(response.Answer);
    }

    [Fact]
    public async Task RewriteIpv6_AQuery_EmptyAnswer()
    {
        // Rewrite has an IPv6 answer but query is A (IPv4)
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [{ "domain": "v6only.local", "answer": "fd00::1" }]
                }
            }
        }
        """;

        using var app = await CreateInitializedApp(config);
        var request = MakeRequest("v6only.local", id: 49); // A query

        await app.IsAllowedAsync(request, EP());
        var response = await app.ProcessRequestAsync(request, EP());

        // IPv6 rewrite for A query produces NoError but empty answer
        Assert.Equal(DnsResponseCode.NoError, response.RCODE);
        Assert.Empty(response.Answer);
    }

    [Fact]
    public async Task RewriteCname_AaaaQuery_ProducesCname()
    {
        // CNAME answers should be returned regardless of query type
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [{ "domain": "alias.local", "answer": "target.example.com" }]
                }
            }
        }
        """;

        using var app = await CreateInitializedApp(config);
        var question = new DnsQuestionRecord("alias.local", DnsResourceRecordType.AAAA, DnsClass.IN);
        var request = new DnsDatagram(
            50, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, new[] { question });

        await app.IsAllowedAsync(request, EP());
        var response = await app.ProcessRequestAsync(request, EP());

        Assert.Equal(DnsResponseCode.NoError, response.RCODE);
        Assert.Single(response.Answer);
        var cnameRecord = response.Answer[0].RDATA as DnsCNAMERecordData;
        Assert.NotNull(cnameRecord);
        Assert.Equal("target.example.com", cnameRecord.Domain);
    }

    [Fact]
    public async Task RewriteIp_AnyQuery_ProducesARecord()
    {
        // A query type of ANY should get an A record for IPv4 rewrites
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [{ "domain": "any.local", "answer": "10.0.0.1" }]
                }
            }
        }
        """;

        using var app = await CreateInitializedApp(config);
        var question = new DnsQuestionRecord("any.local", DnsResourceRecordType.ANY, DnsClass.IN);
        var request = new DnsDatagram(
            51, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, new[] { question });

        await app.IsAllowedAsync(request, EP());
        var response = await app.ProcessRequestAsync(request, EP());

        Assert.Equal(DnsResponseCode.NoError, response.RCODE);
        Assert.Single(response.Answer);
        var aRecord = response.Answer[0].RDATA as DnsARecordData;
        Assert.NotNull(aRecord);
    }

    [Fact]
    public async Task ConcurrentRewriteConsumption_ThreadSafe()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [{ "domain": "concurrent.local", "answer": "10.0.0.1" }]
                }
            }
        }
        """;

        using var app = await CreateInitializedApp(config);

        // Fire 50 concurrent rewrite+process cycles with unique request IDs
        var tasks = Enumerable.Range(100, 50).Select(async id =>
        {
            var request = MakeRequest("concurrent.local", id: (ushort)id);
            var allowed = await app.IsAllowedAsync(request, EP());
            Assert.False(allowed);

            var response = await app.ProcessRequestAsync(request, EP());
            // Each rewrite should be consumed exactly once
            return response.RCODE;
        });

        var results = await Task.WhenAll(tasks);

        // All 50 should have gotten NoError (the rewrite response)
        Assert.All(results, rcode => Assert.Equal(DnsResponseCode.NoError, rcode));
    }

    [Fact]
    public async Task RefreshBlockLists_FailedRefresh_DoesNotBreakFiltering()
    {
        // Config with a blocklist pointing to unreachable URL
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "customRules": ["blocked.com"],
                    "blockLists": ["https://unreachable.invalid/list.txt"]
                }
            },
            "blockLists": [
                { "url": "https://unreachable.invalid/list.txt", "name": "Bad", "enabled": true, "refreshHours": 0 }
            ]
        }
        """;

        using var app = await CreateInitializedApp(config);

        // Custom rules should still work even if blocklist refresh fails
        var result = await app.IsAllowedAsync(MakeRequest("blocked.com"), EP());
        Assert.False(result);

        // Non-blocked domain should still be allowed
        var result2 = await app.IsAllowedAsync(MakeRequest("safe.com"), EP());
        Assert.True(result2);

        // Verify error was logged (the timer fires after 5s, but we can verify the app
        // survives initialization even with bad blocklist URLs)
        _mockServer.Received().WriteLog(Arg.Is<string>(s => s.Contains("initialized")));
    }
}
