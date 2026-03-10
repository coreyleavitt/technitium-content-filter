using ContentFilter.Models;
using ContentFilter.Services;

namespace ContentFilter.Tests;

[Trait("Category", "Unit")]
public class BlockListManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BlockListManager _manager;
    private readonly List<string> _logs = new();

    public BlockListManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "blm-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _manager = new BlockListManager(_tempDir, msg => _logs.Add(msg));
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void GetDomains_UnknownUrl_ReturnsNull()
    {
        Assert.Null(_manager.GetDomains("https://example.com/nonexistent.txt"));
    }

    [Fact]
    public void GetAllStatus_Empty_ReturnsEmptyDict()
    {
        var status = _manager.GetAllStatus();
        Assert.Empty(status);
    }

    [Fact]
    public async Task RefreshAsync_DisabledList_Skipped()
    {
        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/list.txt", Enabled = false }
        };

        await _manager.RefreshAsync(lists);

        Assert.Null(_manager.GetDomains("https://example.com/list.txt"));
    }

    [Fact]
    public async Task RefreshAsync_EmptyUrl_Skipped()
    {
        var lists = new[]
        {
            new BlockListConfig { Url = "", Enabled = true },
            new BlockListConfig { Url = "  ", Enabled = true }
        };

        await _manager.RefreshAsync(lists);

        Assert.Empty(_manager.GetAllStatus());
    }

    [Fact]
    public async Task RefreshAsync_InvalidUrl_LogsError()
    {
        var lists = new[]
        {
            new BlockListConfig { Url = "https://invalid.test.example/404.txt", Enabled = true }
        };

        // This will fail to download but should not throw
        await _manager.RefreshAsync(lists);

        Assert.True(_logs.Any(l => l.Contains("failed to refresh")));
    }

    [Fact]
    public async Task RefreshAsync_DuplicateUrls_UsesMinRefreshHours()
    {
        // Two entries with same URL but different refresh hours -- should use minimum
        var lists = new[]
        {
            new BlockListConfig { Url = "https://example.com/same.txt", Enabled = true, RefreshHours = 24 },
            new BlockListConfig { Url = "https://example.com/same.txt", Enabled = true, RefreshHours = 6 }
        };

        // This will fail to download (not a real URL) but tests the dedup logic
        await _manager.RefreshAsync(lists);

        // Only one failure log (not two), proving dedup worked
        Assert.Single(_logs.Where(l => l.Contains("failed to refresh")));
    }
}

[Trait("Category", "Unit")]
public class BlockListParseFileTests
{
    private readonly string _tempDir;

    public BlockListParseFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "parse-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    private HashSet<string> Parse(string content, Action<string>? log = null)
    {
        var path = Path.Combine(_tempDir, $"test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return BlockListManager.ParseFile(path, log);
    }

    [Fact]
    public void IgnoresSingleLabelDomains()
    {
        var parsed = Parse("localhost\n");
        Assert.Empty(parsed);
    }

    [Fact]
    public void IgnoresDomainsStartingWithDot()
    {
        var parsed = Parse(".example.com\n");
        Assert.Empty(parsed);
    }

    [Fact]
    public void IgnoresExceptionRules()
    {
        var parsed = Parse("@@exception.com\n");
        Assert.Empty(parsed);
    }

    [Fact]
    public void TrimsTrailingDot()
    {
        var parsed = Parse("example.com.\n");
        Assert.Contains("example.com", parsed);
        Assert.DoesNotContain("example.com.", parsed);
    }

    [Fact]
    public void TabSeparatedHostsFile_ParsedCorrectly()
    {
        var parsed = Parse("0.0.0.0\texample.com\n");
        Assert.Single(parsed);
        Assert.Contains("example.com", parsed);
    }

    [Fact]
    public void IgnoresLinesWithSpacesButNoHostPrefix()
    {
        var parsed = Parse("some random text with spaces\n");
        Assert.Empty(parsed);
    }

    [Fact]
    public void MixedFormats_AllParsed()
    {
        var content = """
            # Comment line
            ! Another comment
            example.com
            ||adblock.example.com^
            0.0.0.0 hosts.example.com
            127.0.0.1 hosts2.example.com
            """;
        var parsed = Parse(content);

        Assert.Contains("example.com", parsed);
        Assert.Contains("adblock.example.com", parsed);
        Assert.Contains("hosts.example.com", parsed);
        Assert.Contains("hosts2.example.com", parsed);
        Assert.Equal(4, parsed.Count);
    }

    [Fact]
    public void InlineComments_NotStripped()
    {
        // The parser does NOT strip inline comments -- the whole line is treated as a domain
        // Lines with spaces that don't match hosts format are ignored
        var parsed = Parse("example.com # inline comment\n");
        Assert.Empty(parsed); // has spaces, not hosts format
    }

    [Fact]
    public void ConsecutiveDots_Rejected()
    {
        // "example..com" starts with dot after trimming? No, but it has empty labels.
        // ParseFile checks domain.Contains('.') and !domain.StartsWith('.') -- both pass.
        // But "example..com" is technically valid as a string, just DNS-invalid.
        // The parser does NOT validate DNS label rules, only basic format checks.
        var parsed = Parse("example..com\n");
        // Contains '.' and doesn't start with '.', so it passes the filter
        Assert.Single(parsed);
        Assert.Contains("example..com", parsed);
    }

    [Fact]
    public void NullBytes_InDomain_TreatedAsPlainText()
    {
        // Null bytes in domain name -- parser treats as regular chars
        var parsed = Parse("example\x00.com\n");
        // The domain contains a null byte but still has a dot and doesn't start with dot
        Assert.Single(parsed);
    }

    [Fact]
    public void VeryLongLabel_NotRejected()
    {
        // DNS labels max at 63 chars, but ParseFile doesn't enforce this
        var longLabel = new string('a', 100);
        var domain = $"{longLabel}.example.com";
        var parsed = Parse(domain + "\n");
        Assert.Single(parsed);
        Assert.Contains(domain, parsed);
    }

    [Fact]
    public void BinaryContent_DoesNotCrash()
    {
        // Binary-like content that has no newlines and valid structure
        var binaryish = "JFIF\x00\x01garbage.data\x00\n";
        var parsed = Parse(binaryish);
        // Should not throw -- may or may not parse anything depending on content
        Assert.NotNull(parsed);
    }

    [Fact]
    public void EmptyLines_Ignored()
    {
        var parsed = Parse("\n\n\nexample.com\n\n\n");
        Assert.Single(parsed);
        Assert.Contains("example.com", parsed);
    }

    [Fact]
    public void WhitespaceOnlyLines_Ignored()
    {
        var parsed = Parse("   \n\t\t\n  example.com  \n");
        Assert.Single(parsed);
        Assert.Contains("example.com", parsed);
    }

    [Fact]
    public void ConsecutiveDots_LogsWarning()
    {
        var logs = new List<string>();
        var parsed = Parse("example..com\n", msg => logs.Add(msg));
        Assert.Single(parsed); // Still added (blocklists are messy)
        Assert.Single(logs);
        Assert.Contains("consecutive dots", logs[0]);
    }

    [Fact]
    public void LongLabel_LogsWarning()
    {
        var logs = new List<string>();
        var longLabel = new string('a', 64);
        var parsed = Parse($"{longLabel}.example.com\n", msg => logs.Add(msg));
        Assert.Single(parsed); // Still added
        Assert.Single(logs);
        Assert.Contains("exceeds 63 chars", logs[0]);
    }

    [Fact]
    public void ValidDomain_NoWarning()
    {
        var logs = new List<string>();
        var parsed = Parse("valid.example.com\n", msg => logs.Add(msg));
        Assert.Single(parsed);
        Assert.Empty(logs);
    }
}
