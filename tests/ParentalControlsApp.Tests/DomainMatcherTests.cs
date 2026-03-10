using ParentalControlsApp.Services;

namespace ParentalControlsApp.Tests;

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
}
