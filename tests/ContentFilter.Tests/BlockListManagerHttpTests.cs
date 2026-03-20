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
        public HttpRequestMessage? LastRequest { get; private set; }
        public Dictionary<string, string> ResponseHeaders { get; set; } = new();

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
            LastRequest = request;
            var content = _responseFactory(request.RequestUri!.ToString());
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(content)
            };
            foreach (var (key, value) in ResponseHeaders)
            {
                if (!response.Headers.TryAddWithoutValidation(key, value))
                    response.Content.Headers.TryAddWithoutValidation(key, value);
            }
            return Task.FromResult(response);
        }
    }

    internal record MockResponse(string Content, HttpStatusCode Status, Dictionary<string, string>? Headers = null);

    /// <summary>Returns different responses for sequential requests.</summary>
    internal class SequentialHandler : HttpMessageHandler
    {
        private readonly MockResponse[] _responses;
        private int _index;
        public int RequestCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        public SequentialHandler(params MockResponse[] responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            LastRequest = request;
            var resp = _index < _responses.Length ? _responses[_index++] : _responses[^1];
            var response = new HttpResponseMessage(resp.Status)
            {
                Content = new StringContent(resp.Content)
            };
            if (resp.Headers is not null)
            {
                foreach (var (key, value) in resp.Headers)
                {
                    if (!response.Headers.TryAddWithoutValidation(key, value))
                        response.Content.Headers.TryAddWithoutValidation(key, value);
                }
            }
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Returns 200 with ETag/Last-Modified on first call.
    /// Returns 304 (empty body) when matching conditional headers are present.
    /// Falls back to 200 for unconditional requests after the first.
    /// </summary>
    internal class ConditionalHandler : HttpMessageHandler
    {
        private readonly string _content;
        private readonly string? _etag;
        private readonly string? _lastModified;
        public int RequestCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        public ConditionalHandler(string content, string? etag = null, string? lastModified = null)
        {
            _content = content;
            _etag = etag;
            _lastModified = lastModified;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            LastRequest = request;

            var hasIfNoneMatch = request.Headers.TryGetValues("If-None-Match", out var inm)
                && inm.Any(v => v == _etag);
            var hasIfModifiedSince = request.Headers.TryGetValues("If-Modified-Since", out var ims)
                && ims.Any(v => v == _lastModified);

            if (hasIfNoneMatch || hasIfModifiedSince)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)
                {
                    Content = new StringContent("")
                });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_content)
            };
            if (_etag is not null)
                response.Headers.TryAddWithoutValidation("ETag", _etag);
            if (_lastModified is not null)
                response.Content.Headers.TryAddWithoutValidation("Last-Modified", _lastModified);
            return Task.FromResult(response);
        }
    }

    #region Conditional Fetch Tests

    [Fact]
    public async Task ConditionalFetch_304_SkipsRedownload_KeepsData()
    {
        var handler = new ConditionalHandler("ads.example.com\n", etag: "\"abc123\"");
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 0 }
        };

        // First call: 200 with ETag
        await manager.RefreshAsync(lists);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("ads.example.com", manager.GetDomains("https://example.com/list.txt")!);

        // Second call: 304 (refreshHours=0 forces re-check)
        await manager.RefreshAsync(lists);
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains("ads.example.com", manager.GetDomains("https://example.com/list.txt")!);
    }

    [Fact]
    public async Task ConditionalFetch_304_UpdatesLastFetch()
    {
        var handler = new ConditionalHandler("ads.example.com\n", etag: "\"v1\"");
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 0 }
        };

        await manager.RefreshAsync(lists);
        var firstFetch = manager.GetAllStatus()["https://example.com/list.txt"].LastFetch;
        Assert.NotNull(firstFetch);

        await Task.Delay(50);

        // 304 should still update LastFetch
        await manager.RefreshAsync(lists);
        var secondFetch = manager.GetAllStatus()["https://example.com/list.txt"].LastFetch;
        Assert.NotNull(secondFetch);
        Assert.True(secondFetch > firstFetch);
    }

    [Fact]
    public async Task ConditionalFetch_304_LogsNotModified()
    {
        var handler = new ConditionalHandler("ads.example.com\n", etag: "\"v1\"");
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 0 }
        };

        await manager.RefreshAsync(lists);
        _logs.Clear();

        await manager.RefreshAsync(lists);
        Assert.Contains(_logs, l => l.Contains("not modified (304)"));
    }

    [Fact]
    public async Task ConditionalFetch_200_StoresAndSendsHeaders()
    {
        var handler = new ConditionalHandler(
            "ads.example.com\n",
            etag: "\"etag-value\"",
            lastModified: "Sat, 01 Jan 2025 00:00:00 GMT");
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 0 }
        };

        // First request: no conditional headers
        await manager.RefreshAsync(lists);
        // The ConditionalHandler returns 200 when no conditional headers are present

        // Second request: should send conditional headers
        await manager.RefreshAsync(lists);
        var lastReq = handler.LastRequest!;
        Assert.True(lastReq.Headers.TryGetValues("If-None-Match", out var inm));
        Assert.Contains("\"etag-value\"", inm);
        Assert.True(lastReq.Headers.TryGetValues("If-Modified-Since", out var ims));
        Assert.Contains("Sat, 01 Jan 2025 00:00:00 GMT", ims);
    }

    [Fact]
    public async Task ConditionalFetch_NoServerHeaders_FallsBackToUnconditional()
    {
        // Server returns no ETag or Last-Modified
        var handler = new ConditionalHandler("ads.example.com\n");
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 0 }
        };

        await manager.RefreshAsync(lists);
        await manager.RefreshAsync(lists);

        // No conditional headers should be sent
        var lastReq = handler.LastRequest!;
        Assert.False(lastReq.Headers.Contains("If-None-Match"));
        Assert.False(lastReq.Headers.Contains("If-Modified-Since"));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ConditionalFetch_OldMetaFormat_NoConditionalHeaders()
    {
        var handler = new ConditionalHandler("ads.example.com\n", etag: "\"v1\"");
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 0 }
        };

        // Simulate old meta by writing a meta file with only LastFetch
        await manager.RefreshAsync(lists);

        // Overwrite meta with old format (no ETag/LastModified fields)
        var metaFiles = Directory.GetFiles(_tempDir, "*.meta.json", SearchOption.AllDirectories);
        Assert.Single(metaFiles);
        File.WriteAllText(metaFiles[0], """{"LastFetch":"2020-01-01T00:00:00Z"}""");

        // Next refresh: stale, downloads without conditional headers
        await manager.RefreshAsync(lists);
        var lastReq = handler.LastRequest!;
        Assert.False(lastReq.Headers.Contains("If-None-Match"));
        Assert.False(lastReq.Headers.Contains("If-Modified-Since"));
    }

    [Fact]
    public async Task ConditionalFetch_304_RegexType_KeepsPatterns()
    {
        var handler = new ConditionalHandler("^ads\\.\ntracking\\.\n", etag: "\"regex-v1\"");
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/regex.txt", Enabled = true, RefreshHours = 0, Type = "regex" }
        };

        await manager.RefreshAsync(lists);
        Assert.Equal(2, manager.GetPatterns("https://example.com/regex.txt")!.Count);

        // 304 path
        await manager.RefreshAsync(lists);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(2, manager.GetPatterns("https://example.com/regex.txt")!.Count);
    }

    [Fact]
    public async Task ConditionalFetch_304_DataNotInMemory_RedownloadsUnconditionally()
    {
        // Use SequentialHandler to control exact responses:
        // 1st: 200 with ETag (initial download)
        // 2nd: 304 (conditional check - but we'll clear in-memory data)
        // 3rd: 200 (unconditional re-download after 304 with no data in memory)
        var handler = new SequentialHandler(
            new MockResponse("ads.example.com\n", HttpStatusCode.OK,
                new Dictionary<string, string> { ["ETag"] = "\"v1\"" }),
            new MockResponse("", HttpStatusCode.NotModified),
            new MockResponse("ads.example.com\ntracker.example.com\n", HttpStatusCode.OK));
        using var manager = CreateManager(handler);

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 0 }
        };

        // First download
        await manager.RefreshAsync(lists);
        Assert.Single(manager.GetDomains("https://example.com/list.txt")!);

        // Simulate process restart: clear in-memory data but meta still has ETag on disk
        // We can't directly clear _domainsByUrl, but we can create a new manager sharing the same temp dir
        using var manager2 = new BlockListManager(_tempDir, handler, msg => _logs.Add(msg));

        // This should get 304, realize data isn't in memory, then re-download
        await manager2.RefreshAsync(lists);
        Assert.Equal(3, handler.RequestCount);
        Assert.Contains(_logs, l => l.Contains("304 but data not in memory"));
        Assert.NotNull(manager2.GetDomains("https://example.com/list.txt"));
        Assert.Equal(2, manager2.GetDomains("https://example.com/list.txt")!.Count);
    }

    [Fact]
    public async Task ConditionalFetch_StatusReflectsSupport()
    {
        var handler = new SequentialHandler(
            new MockResponse("ads.example.com\n", HttpStatusCode.OK,
                new Dictionary<string, string> { ["ETag"] = "\"v1\"" }),
            new MockResponse("plain.example.com\n", HttpStatusCode.OK));
        using var manager = CreateManager(handler);

        await manager.RefreshAsync(new[]
        {
            new BlockListConfig { Url = "https://example.com/with-etag.txt", Enabled = true }
        });
        await manager.RefreshAsync(new[]
        {
            new BlockListConfig { Url = "https://example.com/no-etag.txt", Enabled = true }
        });

        var status = manager.GetAllStatus();
        Assert.True(status["https://example.com/with-etag.txt"].ConditionalFetchSupported);
        Assert.False(status["https://example.com/no-etag.txt"].ConditionalFetchSupported);
    }

    #endregion
}
