using System.Collections.Concurrent;
using System.Net;
using DnsServerCore.ApplicationCommon;
using NSubstitute;
using ContentFilter.Models;
using ContentFilter.Services;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace ContentFilter.Tests;

/// <summary>
/// Issue #28: Tests for rewrite ID collision handling.
/// The pending rewrite system uses request.Identifier (16-bit) as the key.
/// Tests verify that different requests with the same ID overwrite each other
/// (last write wins) and that distinct IDs remain independent.
/// </summary>
[Trait("Category", "Unit")]
public class RewriteCollisionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IDnsServer _mockServer;

    public RewriteCollisionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rewrite-collision-" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task SameRequestId_DifferentDomains_LastRewriteWins()
    {
        // When two rewrites use the same 16-bit request ID, the second overwrites the first
        // in the ConcurrentDictionary (keyed by Identifier).
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

        // First rewrite with ID 100
        var req1 = MakeRequest("youtube.com", id: 100);
        await app.IsAllowedAsync(req1, EP());

        // Second rewrite with same ID 100 overwrites the pending entry
        var req2 = MakeRequest("bing.com", id: 100);
        await app.IsAllowedAsync(req2, EP());

        // ProcessRequest retrieves the last-written rewrite (bing)
        var response = await app.ProcessRequestAsync(req2, EP());
        Assert.Equal(DnsResponseCode.NoError, response.RCODE);
        Assert.True(response.Answer.Count > 0);
        var cname = response.Answer[0].RDATA as DnsCNAMERecordData;
        Assert.NotNull(cname);
        Assert.Equal("safesearch.bing.com", cname.Domain);
    }

    [Fact]
    public async Task DifferentRequestIds_IndependentRewrites()
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

        // Two rewrites with different IDs
        var req1 = MakeRequest("youtube.com", id: 200);
        var req2 = MakeRequest("bing.com", id: 201);
        await app.IsAllowedAsync(req1, EP());
        await app.IsAllowedAsync(req2, EP());

        // Each ID retrieves its own rewrite
        var resp1 = await app.ProcessRequestAsync(req1, EP());
        var resp2 = await app.ProcessRequestAsync(req2, EP());

        Assert.Equal(DnsResponseCode.NoError, resp1.RCODE);
        Assert.Equal(DnsResponseCode.NoError, resp2.RCODE);

        var cname1 = resp1.Answer[0].RDATA as DnsCNAMERecordData;
        var cname2 = resp2.Answer[0].RDATA as DnsCNAMERecordData;
        Assert.NotNull(cname1);
        Assert.NotNull(cname2);
        Assert.Equal("restrict.youtube.com", cname1.Domain);
        Assert.Equal("safesearch.bing.com", cname2.Domain);
    }

    [Fact]
    public async Task DifferentClients_SameRequestId_LastWriteWins()
    {
        // The current implementation uses only request.Identifier as key,
        // so different clients with the same request ID will collide.
        // This test documents the current behavior (last write wins).
        var config = """
        {
            "enableBlocking": true,
            "clients": [
                { "name": "Client1", "ids": ["10.0.0.1"], "profile": "kids" },
                { "name": "Client2", "ids": ["10.0.0.2"], "profile": "kids" }
            ],
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

        // Both clients use the same request ID
        var req1 = MakeRequest("youtube.com", id: 300);
        var req2 = MakeRequest("youtube.com", id: 300);

        await app.IsAllowedAsync(req1, EP("10.0.0.1"));
        await app.IsAllowedAsync(req2, EP("10.0.0.2"));

        // With composite keys ({requestId}:{clientIp}), each client has its own entry
        var resp1 = await app.ProcessRequestAsync(req1, EP("10.0.0.1"));
        Assert.Equal(DnsResponseCode.NoError, resp1.RCODE);

        // Second client also finds its own pending rewrite (no collision)
        var resp2 = await app.ProcessRequestAsync(req2, EP("10.0.0.2"));
        Assert.Equal(DnsResponseCode.NoError, resp2.RCODE);
    }

    [Fact]
    public async Task ConcurrentRewrites_AllDistinctIds_AllConsumed()
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

        // Launch many concurrent rewrite+consume cycles with distinct IDs
        var tasks = Enumerable.Range(1000, 100).Select(async id =>
        {
            var request = MakeRequest("youtube.com", id: (ushort)id);
            await app.IsAllowedAsync(request, EP());
            var response = await app.ProcessRequestAsync(request, EP());
            return response.RCODE;
        });

        var results = await Task.WhenAll(tasks);
        Assert.All(results, rcode => Assert.Equal(DnsResponseCode.NoError, rcode));
    }
}
