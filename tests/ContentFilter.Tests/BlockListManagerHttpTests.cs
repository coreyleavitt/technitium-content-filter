using System.Net;
using NSubstitute;
using ContentFilter.Models;
using ContentFilter.Services;

namespace ContentFilter.Tests;

/// <summary>
/// Tests BlockListManager HTTP download and caching behavior using a mock HttpMessageHandler.
/// </summary>
[Trait("Category", "Unit")]
public class BlockListManagerHttpTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _logs = new();

    public BlockListManagerHttpTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "blm-http-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private BlockListManager CreateManager(HttpMessageHandler handler)
    {
        return new BlockListManager(_tempDir, handler, msg => _logs.Add(msg));
    }

    private static MockHandler MockHttp(string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new MockHandler(content, status);
    }

    [Fact]
    public async Task RefreshAsync_DownloadsAndParsesDomains()
    {
        var handler = MockHttp("ads.example.com\ntracker.example.com\n");
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 24 }
        };

        await manager.RefreshAsync(lists);

        var domains = manager.GetDomains("https://example.com/list.txt");
        Assert.NotNull(domains);
        Assert.Contains("ads.example.com", domains);
        Assert.Contains("tracker.example.com", domains);
        Assert.Equal(2, domains.Count);
    }

    [Fact]
    public async Task RefreshAsync_CachesAndReusesOnSecondCall()
    {
        var handler = MockHttp("ads.example.com\n");
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 24 }
        };

        // First call downloads
        await manager.RefreshAsync(lists);
        Assert.Equal(1, handler.RequestCount);

        // Second call should use cache (within refreshHours)
        await manager.RefreshAsync(lists);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RefreshAsync_MultipleUrls_DownloadsAll()
    {
        var handler = new MockHandler(url => url.Contains("list1") ? "a.example.com\n" : "b.example.com\n");
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list1.txt", Enabled = true },
            new BlockListConfig { Url = "https://example.com/list2.txt", Enabled = true }
        };

        await manager.RefreshAsync(lists);

        Assert.Contains("a.example.com", manager.GetDomains("https://example.com/list1.txt")!);
        Assert.Contains("b.example.com", manager.GetDomains("https://example.com/list2.txt")!);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task RefreshAsync_HttpError_LogsAndFallsToCache()
    {
        var handler = MockHttp("", HttpStatusCode.InternalServerError);
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/fail.txt", Enabled = true }
        };

        await manager.RefreshAsync(lists);

        Assert.True(_logs.Any(l => l.Contains("failed to refresh")));
        Assert.Null(manager.GetDomains("https://example.com/fail.txt"));
    }

    [Fact]
    public async Task RefreshAsync_HttpError_UsesCacheIfAvailable()
    {
        // First call succeeds
        var handler = new SequentialHandler(
            new MockResponse("cached.example.com\n", HttpStatusCode.OK),
            new MockResponse("", HttpStatusCode.InternalServerError));
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 0 }
        };

        // First download succeeds
        await manager.RefreshAsync(lists);
        var domains = manager.GetDomains("https://example.com/list.txt");
        Assert.NotNull(domains);
        Assert.Contains("cached.example.com", domains);

        // Second call fails (refreshHours=0 forces re-download), but cache remains
        await manager.RefreshAsync(lists);
        domains = manager.GetDomains("https://example.com/list.txt");
        Assert.NotNull(domains);
        Assert.Contains("cached.example.com", domains);
    }

    [Fact]
    public async Task RefreshAsync_ParsesAdblockFormat()
    {
        var handler = MockHttp("||ads.tracker.com^\n||malware.evil.org^\n# comment\n");
        using var manager = CreateManager(handler);

        await manager.RefreshAsync(new[]
        {
            new BlockListConfig { Url = "https://example.com/adblock.txt", Enabled = true }
        });

        var domains = manager.GetDomains("https://example.com/adblock.txt");
        Assert.NotNull(domains);
        Assert.Contains("ads.tracker.com", domains);
        Assert.Contains("malware.evil.org", domains);
        Assert.Equal(2, domains.Count);
    }

    [Fact]
    public async Task RefreshAsync_ParsesHostsFormat()
    {
        var handler = MockHttp("0.0.0.0 ads.example.com\n127.0.0.1 tracker.example.com\n0.0.0.0\ttab.example.com\n");
        using var manager = CreateManager(handler);

        await manager.RefreshAsync(new[]
        {
            new BlockListConfig { Url = "https://example.com/hosts.txt", Enabled = true }
        });

        var domains = manager.GetDomains("https://example.com/hosts.txt");
        Assert.NotNull(domains);
        Assert.Contains("ads.example.com", domains);
        Assert.Contains("tracker.example.com", domains);
        Assert.Contains("tab.example.com", domains);
        Assert.Equal(3, domains.Count);
    }

    [Fact]
    public async Task RefreshAsync_RegexType_DownloadsAndParsesPatterns()
    {
        var handler = MockHttp("^ads?\\d*\\.\ntracking\\.\n# comment\n");
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/regex.txt", Enabled = true, Type = "regex" }
        };

        await manager.RefreshAsync(lists);

        Assert.Null(manager.GetDomains("https://example.com/regex.txt"));
        var patterns = manager.GetPatterns("https://example.com/regex.txt");
        Assert.NotNull(patterns);
        Assert.Equal(2, patterns.Count);
        Assert.Contains(@"^ads?\d*\.", patterns);
        Assert.Contains(@"tracking\.", patterns);
    }

    [Fact]
    public async Task RefreshAsync_RegexType_CacheFallback()
    {
        var handler = new SequentialHandler(
            new MockResponse("^ads\\.\n", HttpStatusCode.OK),
            new MockResponse("", HttpStatusCode.InternalServerError));
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/regex.txt", Enabled = true, RefreshHours = 0, Type = "regex" }
        };

        await manager.RefreshAsync(lists);
        var patterns = manager.GetPatterns("https://example.com/regex.txt");
        Assert.NotNull(patterns);
        Assert.Single(patterns);

        // Second call fails, cache preserved
        await manager.RefreshAsync(lists);
        patterns = manager.GetPatterns("https://example.com/regex.txt");
        Assert.NotNull(patterns);
        Assert.Single(patterns);
    }

    [Fact]
    public async Task GetAllStatus_RegexList_ShowsCorrectType()
    {
        var handler = MockHttp("^ads\\.\ntracking\\.\n");
        using var manager = CreateManager(handler);

        await manager.RefreshAsync(new[]
        {
            new BlockListConfig { Url = "https://example.com/regex.txt", Enabled = true, Type = "regex" }
        });

        var status = manager.GetAllStatus();
        Assert.Single(status);
        Assert.Equal("regex", status["https://example.com/regex.txt"].Type);
        Assert.Equal(2, status["https://example.com/regex.txt"].EntryCount);
    }

    [Fact]
    public async Task GetAllStatus_ReflectsDownloadedLists()
    {
        var handler = MockHttp("a.example.com\nb.example.com\n");
        using var manager = CreateManager(handler);

        await manager.RefreshAsync(new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true }
        });

        var status = manager.GetAllStatus();
        Assert.Single(status);
        Assert.True(status.ContainsKey("https://example.com/list.txt"));
        Assert.Equal(2, status["https://example.com/list.txt"].EntryCount);
        Assert.NotNull(status["https://example.com/list.txt"].LastFetch);
    }

    [Fact]
    public async Task RefreshAsync_CorruptedMetaFile_RedownloadsGracefully()
    {
        var handler = MockHttp("ads.example.com\n");
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 24 }
        };

        // First download succeeds
        await manager.RefreshAsync(lists);
        Assert.Equal(1, handler.RequestCount);

        // Corrupt the meta file by writing invalid JSON
        var metaFiles = Directory.GetFiles(_tempDir, "*.meta.json", SearchOption.AllDirectories);
        Assert.Single(metaFiles);
        File.WriteAllText(metaFiles[0], "NOT VALID JSON {{{");

        // Next refresh should treat meta as null (corrupted) and re-download
        await manager.RefreshAsync(lists);
        Assert.Equal(2, handler.RequestCount);

        // Domains should still be available
        var domains = manager.GetDomains("https://example.com/list.txt");
        Assert.NotNull(domains);
        Assert.Contains("ads.example.com", domains);
    }

    [Fact]
    public async Task RefreshAsync_Timeout_LogsError()
    {
        var handler = new TimeoutHandler();
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/slow.txt", Enabled = true }
        };

        await manager.RefreshAsync(lists);

        Assert.True(_logs.Any(l => l.Contains("failed to refresh")));
        Assert.Null(manager.GetDomains("https://example.com/slow.txt"));
    }

    [Fact]
    public async Task RefreshAsync_Timeout_UsesCacheIfAvailable()
    {
        // First call succeeds, second times out
        var handler = new SequentialThrowHandler(
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("cached.example.com\n")
            }),
            () => throw new TaskCanceledException("Timeout"));
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 0 }
        };

        // First download succeeds
        await manager.RefreshAsync(lists);
        Assert.Contains("cached.example.com", manager.GetDomains("https://example.com/list.txt")!);

        // Second call times out, cache preserved
        await manager.RefreshAsync(lists);
        Assert.Contains("cached.example.com", manager.GetDomains("https://example.com/list.txt")!);
    }

    /// <summary>Handler that always throws TaskCanceledException (simulates HTTP timeout).</summary>
    internal class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
        }
    }

    /// <summary>Handler that calls different factories for sequential requests.</summary>
    internal class SequentialThrowHandler : HttpMessageHandler
    {
        private readonly Func<Task<HttpResponseMessage>>[] _factories;
        private int _index;

        public SequentialThrowHandler(params Func<Task<HttpResponseMessage>>[] factories) => _factories = factories;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var factory = _index < _factories.Length ? _factories[_index++] : _factories[^1];
            return factory();
        }
    }

    /// <summary>Simple mock handler that returns a fixed response.</summary>
    internal class MockHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _responseFactory;
        private readonly HttpStatusCode _status;
        public int RequestCount { get; private set; }

        public MockHandler(string content, HttpStatusCode status = HttpStatusCode.OK)
            : this(_ => content, status) { }

        public MockHandler(Func<string, string> responseFactory, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responseFactory = responseFactory;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            var content = _responseFactory(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(content)
            });
        }
    }

    internal record MockResponse(string Content, HttpStatusCode Status);

    /// <summary>Returns different responses for sequential requests.</summary>
    internal class SequentialHandler : HttpMessageHandler
    {
        private readonly MockResponse[] _responses;
        private int _index;

        public SequentialHandler(params MockResponse[] responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = _index < _responses.Length ? _responses[_index++] : _responses[^1];
            return Task.FromResult(new HttpResponseMessage(resp.Status)
            {
                Content = new StringContent(resp.Content)
            });
        }
    }
}
