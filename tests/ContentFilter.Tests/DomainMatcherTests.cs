using ContentFilter.Services;

namespace ContentFilter.Tests;

[Trait("Category", "Unit")]
public class DomainMatcherTests
{
    private static HashSet<string> Domains(params string[] entries)
        => new(entries, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.com", "www.example.com", true)]
    [InlineData("example.com", "a.b.c.d.example.com", true)]
    [InlineData("example.com", "example.com.", true)]
    [InlineData("example.com", "www.example.com.", true)]
    [InlineData("Example.COM", "example.com", true)]
    [InlineData("example.com", "WWW.EXAMPLE.COM", true)]
    [InlineData("localhost", "localhost", true)]
    [InlineData("example.com", "other.com", false)]
    [InlineData("example.com", "notexample.com", false)]
    [InlineData("example.com", "example.org", false)]
    [InlineData("example.com", "", false)]
    [InlineData("example.com", ".", false)]
    public void Matches_SingleDomain(string setDomain, string query, bool expected)
    {
        var set = Domains(setDomain);
        Assert.Equal(expected, DomainMatcher.Matches(set, query));
    }

    [Fact]
    public void Matches_EmptySet_AlwaysFalse()
    {
        Assert.False(DomainMatcher.Matches(Domains(), "example.com"));
    }

    [Fact]
    public void Matches_MultipleDomains()
    {
        var set = Domains("youtube.com", "tiktok.com", "facebook.com");
        Assert.True(DomainMatcher.Matches(set, "www.youtube.com"));
        Assert.True(DomainMatcher.Matches(set, "tiktok.com"));
        Assert.False(DomainMatcher.Matches(set, "google.com"));
    }

    // --- FindMatch tests ---

    [Fact]
    public void FindMatch_ExactMatch_ReturnsMatch()
    {
        var set = Domains("example.com");
        var match = DomainMatcher.FindMatch(set, "example.com");
        Assert.Equal("example.com", match);
    }

    [Fact]
    public void FindMatch_ParentMatch_ReturnsParent()
    {
        var set = Domains("example.com");
        var match = DomainMatcher.FindMatch(set, "sub.deep.example.com");
        Assert.Equal("example.com", match);
    }

    [Fact]
    public void FindMatch_NoMatch_ReturnsNull()
    {
        var set = Domains("example.com");
        Assert.Null(DomainMatcher.FindMatch(set, "other.com"));
    }

    [Fact]
    public void FindMatch_TrailingDot_ReturnsMatch()
    {
        var set = Domains("example.com");
        var match = DomainMatcher.FindMatch(set, "example.com.");
        Assert.Equal("example.com", match);
    }

    [Fact]
    public void FindMatch_CaseInsensitive_ReturnsMatch()
    {
        var set = Domains("Example.COM");
        var match = DomainMatcher.FindMatch(set, "www.example.com");
        Assert.NotNull(match);
    }

    [Fact]
    public void FindMatch_EmptySet_ReturnsNull()
    {
        Assert.Null(DomainMatcher.FindMatch(Domains(), "example.com"));
    }
}
