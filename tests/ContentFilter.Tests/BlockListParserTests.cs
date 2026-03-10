using ContentFilter.Services;

namespace ContentFilter.Tests;

[Trait("Category", "Unit")]
public class BlockListParserTests : IDisposable
{
    private readonly string _tempDir;

    public BlockListParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "blocklist-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private HashSet<string> Parse(string content)
    {
        var path = Path.Combine(_tempDir, $"test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return BlockListManager.ParseFile(path);
    }

    [Fact]
    public void PlainDomains()
    {
        var domains = Parse("example.com\ntest.org\n");
        Assert.Contains("example.com", domains);
        Assert.Contains("test.org", domains);
    }

    [Fact]
    public void AdblockFormat()
    {
        var domains = Parse("||ads.example.com^\n||tracker.test.org^\n");
        Assert.Contains("ads.example.com", domains);
        Assert.Contains("tracker.test.org", domains);
    }

    [Fact]
    public void HostsFile_ZeroAddress()
    {
        var domains = Parse("0.0.0.0 ads.example.com\n");
        Assert.Contains("ads.example.com", domains);
    }

    [Fact]
    public void HostsFile_Localhost()
    {
        var domains = Parse("127.0.0.1 tracker.test.org\n");
        Assert.Contains("tracker.test.org", domains);
    }

    [Fact]
    public void CommentsSkipped()
    {
        var domains = Parse("# Hash comment\n! Bang comment\nexample.com\n");
        Assert.Single(domains);
        Assert.Contains("example.com", domains);
    }

    [Fact]
    public void EmptyLinesSkipped()
    {
        var domains = Parse("\n\nexample.com\n\n\ntest.org\n\n");
        Assert.Equal(2, domains.Count);
    }

    [Fact]
    public void TrailingDotsRemoved()
    {
        var domains = Parse("example.com.\n");
        Assert.Contains("example.com", domains);
        Assert.DoesNotContain("example.com.", domains);
    }

    [Fact]
    public void SingleLabelDomainsSkipped()
    {
        var domains = Parse("localhost\ncom\nexample.com\n");
        Assert.Single(domains);
        Assert.Contains("example.com", domains);
    }

    [Fact]
    public void DomainsStartingWithDotSkipped()
    {
        var domains = Parse(".example.com\nexample.com\n");
        Assert.Single(domains);
        Assert.Contains("example.com", domains);
    }

    [Fact]
    public void AtAtPrefixedSkipped()
    {
        var domains = Parse("@@example.com\nblocked.com\n");
        Assert.Single(domains);
        Assert.Contains("blocked.com", domains);
    }

    [Fact]
    public void MixedFormats()
    {
        var content = """
            # Comment
            example.com
            ||ads.tracker.com^
            0.0.0.0 malware.bad.com
            127.0.0.1 spyware.evil.org
            ! Another comment

            plain.domain.net
            """;
        var domains = Parse(content);
        Assert.Equal(5, domains.Count);
        Assert.Contains("example.com", domains);
        Assert.Contains("ads.tracker.com", domains);
        Assert.Contains("malware.bad.com", domains);
        Assert.Contains("spyware.evil.org", domains);
        Assert.Contains("plain.domain.net", domains);
    }

    [Fact]
    public void WhitespaceTrimmed()
    {
        var domains = Parse("  example.com  \n  test.org  \n");
        Assert.Contains("example.com", domains);
        Assert.Contains("test.org", domains);
    }

    [Fact]
    public void HostsFile_TabSeparated_ParsedCorrectly()
    {
        var domains = Parse("0.0.0.0\texample.com\n");
        Assert.Contains("example.com", domains);
    }

    [Fact]
    public void HostsFile_TabSeparated_Localhost()
    {
        var domains = Parse("127.0.0.1\ttracker.example.com\n");
        Assert.Contains("tracker.example.com", domains);
    }

    [Fact]
    public void CaseInsensitiveDedup()
    {
        var domains = Parse("Example.COM\nexample.com\n");
        Assert.Single(domains);
    }

    [Fact]
    public void EmptyFile_ReturnsEmptySet()
    {
        var domains = Parse("");
        Assert.Empty(domains);
    }
}
