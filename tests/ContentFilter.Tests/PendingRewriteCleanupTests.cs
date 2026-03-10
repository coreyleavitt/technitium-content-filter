using System.Collections.Concurrent;
using System.Net;
using DnsServerCore.ApplicationCommon;
using NSubstitute;
using ContentFilter.Models;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace ContentFilter.Tests;

/// <summary>
/// Issue #29: Tests for pending rewrite cleanup/expiration.
/// The _pendingRewrites dictionary stores entries until consumed by ProcessRequestAsync.
/// These tests verify that entries are consumed atomically via TryRemove and that
/// unconsumed entries don't prevent normal operation.
/// </summary>
[Trait("Category", "Unit")]
public class PendingRewriteCleanupTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IDnsServer _mockServer;

    public PendingRewriteCleanupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rewrite-cleanup-" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<ContentFilter.App> CreateApp(string configJson)
    {
        var app = new ContentFilter.App();
        await app.InitializeAsync(_mockServer, configJson);
        return app;
    }

    [Fact]
    public async Task UnconsumedRewrite_DoesNotBlockNewRequests()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [
                        { "domain": "youtube.com", "answer": "restrict.youtube.com" }
                    ]
                }
            }
        }
        """;

        using var app = await CreateApp(config);

        // Create a rewrite but never consume it
        var req1 = MakeRequest("youtube.com", id: 500);
        await app.IsAllowedAsync(req1, EP());

        // A new request with a different ID should still work
        var req2 = MakeRequest("youtube.com", id: 501);
        var allowed = await app.IsAllowedAsync(req2, EP());
        Assert.False(allowed);

        var resp2 = await app.ProcessRequestAsync(req2, EP());
        Assert.Equal(DnsResponseCode.NoError, resp2.RCODE);
    }

    [Fact]
    public async Task ProcessRequest_TryRemove_AtomicConsumption()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [
                        { "domain": "youtube.com", "answer": "restrict.youtube.com" }
                    ]
                }
            }
        }
        """;

        using var app = await CreateApp(config);

        var request = MakeRequest("youtube.com", id: 600);
        await app.IsAllowedAsync(request, EP());

        // First consume succeeds
        var resp1 = await app.ProcessRequestAsync(request, EP());
        Assert.Equal(DnsResponseCode.NoError, resp1.RCODE);

        // Second consume fails (TryRemove already removed it)
        var resp2 = await app.ProcessRequestAsync(request, EP());
        Assert.Equal(DnsResponseCode.NxDomain, resp2.RCODE);

        // Third consume also fails
        var resp3 = await app.ProcessRequestAsync(request, EP());
        Assert.Equal(DnsResponseCode.NxDomain, resp3.RCODE);
    }

    [Fact]
    public async Task ManyUnconsumedRewrites_AppStillFunctions()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [
                        { "domain": "youtube.com", "answer": "restrict.youtube.com" }
                    ],
                    "customRules": ["blocked.com"]
                }
            }
        }
        """;

        using var app = await CreateApp(config);

        // Create many unconsumed rewrites
        for (ushort i = 0; i < 100; i++)
        {
            var req = MakeRequest("youtube.com", id: i);
            await app.IsAllowedAsync(req, EP());
        }

        // Normal blocking still works
        var blockReq = MakeRequest("blocked.com", id: 200);
        var allowed = await app.IsAllowedAsync(blockReq, EP());
        Assert.False(allowed);

        // Normal allow still works
        var safeReq = MakeRequest("safe.com", id: 201);
        allowed = await app.IsAllowedAsync(safeReq, EP());
        Assert.True(allowed);
    }

    [Fact]
    public async Task RewriteOverwritten_OnlyLatestConsumed()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "dnsRewrites": [
                        { "domain": "youtube.com", "answer": "restrict.youtube.com" },
                        { "domain": "bing.com", "answer": "safesearch.bing.com" }
                    ]
                }
            }
        }
        """;

        using var app = await CreateApp(config);

        // Same ID used twice: second overwrites first
        var req1 = MakeRequest("youtube.com", id: 700);
        var req2 = MakeRequest("bing.com", id: 700);
        await app.IsAllowedAsync(req1, EP());
        await app.IsAllowedAsync(req2, EP());

        // Consume once: gets the bing rewrite (last write)
        var resp = await app.ProcessRequestAsync(req2, EP());
        Assert.Equal(DnsResponseCode.NoError, resp.RCODE);

        // Second consume: entry already removed
        var resp2 = await app.ProcessRequestAsync(req1, EP());
        Assert.Equal(DnsResponseCode.NxDomain, resp2.RCODE);
    }
}
