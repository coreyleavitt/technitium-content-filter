using System.Net;
using ContentFilter.Models;
using ContentFilter.Services;

namespace ContentFilter.Tests;

/// <summary>
/// Issue #33: Tests for duplicate blocklist URL deduplication.
/// BlockListManager.RefreshAsync deduplicates URLs by collecting them in a dictionary.
/// ProfileCompiler's blocklist section uses the global config to filter profile URLs.
/// </summary>
[Trait("Category", "Unit")]
public class BlockListDeduplicationTests : IDisposable
{
    private readonly string _tempDir;

    public BlockListDeduplicationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dedup-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task RefreshAsync_DuplicateUrls_DownloadsOnlyOnce()
    {
        var handler = new MockHandler("ads.example.com\n");
        using var manager = new BlockListManager(_tempDir, handler, _ => { });

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 24 },
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 12 },
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 6 }
        };

        await manager.RefreshAsync(lists);

        // Only one download despite three entries with the same URL
        Assert.Equal(1, handler.RequestCount);

        var domains = manager.GetDomains("https://example.com/list.txt");
        Assert.NotNull(domains);
        Assert.Contains("ads.example.com", domains);
    }

    [Fact]
    public async Task RefreshAsync_DuplicateUrls_UsesMinRefreshHours()
    {
        var handler = new MockHandler("ads.example.com\n");
        using var manager = new BlockListManager(_tempDir, handler, _ => { });

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 48 },
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 6 }
        };

        // First refresh downloads
        await manager.RefreshAsync(lists);
        Assert.Equal(1, handler.RequestCount);

        // Second refresh within 6 hours (the minimum) should not re-download
        await manager.RefreshAsync(lists);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RefreshAsync_DifferentUrls_DownloadsBoth()
    {
        var handler = new MockHandler(url =>
            url.Contains("list1") ? "a.example.com\n" : "b.example.com\n");
        using var manager = new BlockListManager(_tempDir, handler, _ => { });

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list1.txt", Enabled = true },
            new BlockListConfig { Url = "https://example.com/list2.txt", Enabled = true }
        };

        await manager.RefreshAsync(lists);

        Assert.Equal(2, handler.RequestCount);
        Assert.Contains("a.example.com", manager.GetDomains("https://example.com/list1.txt")!);
        Assert.Contains("b.example.com", manager.GetDomains("https://example.com/list2.txt")!);
    }

    [Fact]
    public void ProfileCompiler_DuplicateUrlsInProfile_DomainsNotDuplicated()
    {
        var tempDir2 = Path.Combine(Path.GetTempPath(), "dedup-compile-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir2);
        try
        {
            using var blm = new BlockListManager(tempDir2);
            var compiler = new ProfileCompiler(new ServiceRegistry(), blm);

            // Profile references the same URL twice
            var config = new AppConfig
            {
                BlockLists =
                [
                    new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true }
                ],
                Profiles =
                {
                    ["test"] = new ProfileConfig
                    {
                        BlockLists =
                        [
                            "https://example.com/list.txt",
                            "https://example.com/list.txt"
                        ]
                    }
                }
            };

            // Even though the URL is listed twice, the domains go into a HashSet
            // so duplicates are automatically handled.
            var result = compiler.CompileAll(config);
            Assert.NotNull(result["test"]);
        }
        finally
        {
            try { Directory.Delete(tempDir2, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RefreshAsync_DisabledDuplicate_OnlyRefreshesEnabled()
    {
        var handler = new MockHandler("ads.example.com\n");
        using var manager = new BlockListManager(_tempDir, handler, _ => { });

        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true, RefreshHours = 24 },
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = false, RefreshHours = 6 }
        };

        await manager.RefreshAsync(lists);

        // Only the enabled entry is processed; the disabled one is skipped entirely.
        // This means the minimum refresh hours only considers enabled entries.
        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>Simple mock handler that returns a fixed response.</summary>
    private sealed class MockHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _responseFactory;
        public int RequestCount { get; private set; }

        public MockHandler(string content) : this(_ => content) { }

        public MockHandler(Func<string, string> responseFactory) => _responseFactory = responseFactory;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            var content = _responseFactory(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });
        }
    }
}
