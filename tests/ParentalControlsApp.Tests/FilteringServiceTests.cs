using System.Net;
using ParentalControlsApp.Models;
using ParentalControlsApp.Services;

namespace ParentalControlsApp.Tests;

[Trait("Category", "Unit")]
public class GetRewriteTests
{
    private static Dictionary<string, DnsRewriteConfig> Rewrites(params (string domain, string answer)[] entries)
    {
        var dict = new Dictionary<string, DnsRewriteConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (domain, answer) in entries)
            dict[domain] = new DnsRewriteConfig { Domain = domain, Answer = answer };
        return dict;
    }

    [Theory]
    [InlineData("youtube.com", "youtube.com", true)]
    [InlineData("youtube.com", "www.youtube.com", true)]
    [InlineData("youtube.com", "a.b.c.youtube.com", true)]
    [InlineData("youtube.com", "youtube.com.", true)]
    [InlineData("youtube.com", "google.com", false)]
    public void GetRewrite_MatchBehavior(string rewriteDomain, string query, bool shouldMatch)
    {
        var rewrites = Rewrites((rewriteDomain, "1.2.3.4"));
        var result = FilteringService.GetRewrite(rewrites, query);
        Assert.Equal(shouldMatch, result is not null);
    }

    [Fact]
    public void GetRewrite_EmptyRewrites_ReturnsNull()
    {
        var result = FilteringService.GetRewrite(new Dictionary<string, DnsRewriteConfig>(), "youtube.com");
        Assert.Null(result);
    }

    [Fact]
    public void GetRewrite_CaseInsensitive()
    {
        var rewrites = Rewrites(("YouTube.COM", "restrict.youtube.com"));
        Assert.NotNull(FilteringService.GetRewrite(rewrites, "youtube.com"));
    }

    [Fact]
    public void GetRewrite_ReturnsOriginalAnswer_NoTrimming()
    {
        // GetRewrite returns the answer as-is; trimming happens in App.BuildRewriteResponse
        var rewrites = Rewrites(("youtube.com", "  1.2.3.4  "));
        var result = FilteringService.GetRewrite(rewrites, "youtube.com");
        Assert.NotNull(result);
        Assert.Equal("  1.2.3.4  ", result.Answer);
    }

    [Fact]
    public void GetRewrite_MostSpecificWins()
    {
        var rewrites = Rewrites(
            ("example.com", "parent-answer"),
            ("sub.example.com", "child-answer"));
        var result = FilteringService.GetRewrite(rewrites, "sub.example.com");
        Assert.NotNull(result);
        Assert.Equal("child-answer", result.Answer);
    }
}

[Trait("Category", "Unit")]
public class ResolveProfileTests
{
    [Fact]
    public void NoClients_ReturnsDefault()
    {
        var config = new AppConfig { DefaultProfile = "fallback" };
        var result = FilteringService.ResolveProfile(config, null, IPAddress.Parse("10.0.0.1"));
        Assert.Equal("fallback", result);
    }

    [Fact]
    public void NoClients_NoDefault_ReturnsNull()
    {
        var config = new AppConfig();
        var result = FilteringService.ResolveProfile(config, null, IPAddress.Parse("10.0.0.1"));
        Assert.Null(result);
    }

    [Fact]
    public void ExactIp_Matches()
    {
        var config = new AppConfig
        {
            Clients = [new ClientConfig { Ids = ["192.168.1.50"], Profile = "kids" }]
        };
        var result = FilteringService.ResolveProfile(config, null, IPAddress.Parse("192.168.1.50"));
        Assert.Equal("kids", result);
    }

    [Fact]
    public void ExactIp_NoMatch_FallsToDefault()
    {
        var config = new AppConfig
        {
            DefaultProfile = "default",
            Clients = [new ClientConfig { Ids = ["192.168.1.50"], Profile = "kids" }]
        };
        var result = FilteringService.ResolveProfile(config, null, IPAddress.Parse("10.0.0.99"));
        Assert.Equal("default", result);
    }

    [Fact]
    public void ClientId_MatchesBeforeIp()
    {
        var config = new AppConfig
        {
            Clients =
            [
                new ClientConfig { Ids = ["10.0.0.1"], Profile = "ip-profile" },
                new ClientConfig { Ids = ["alice"], Profile = "name-profile" }
            ]
        };
        var result = FilteringService.ResolveProfile(config, "alice", IPAddress.Parse("10.0.0.1"));
        Assert.Equal("name-profile", result);
    }

    [Fact]
    public void ClientId_CaseInsensitive()
    {
        var config = new AppConfig
        {
            Clients = [new ClientConfig { Ids = ["Alice.DNS.Example"], Profile = "alice" }]
        };
        var result = FilteringService.ResolveProfile(config, "alice.dns.example", IPAddress.Parse("10.0.0.1"));
        Assert.Equal("alice", result);
    }

    [Fact]
    public void ExactIp_MatchesBeforeCidr()
    {
        var config = new AppConfig
        {
            Clients =
            [
                new ClientConfig { Ids = ["192.168.1.0/24"], Profile = "subnet" },
                new ClientConfig { Ids = ["192.168.1.50"], Profile = "exact" }
            ]
        };
        var result = FilteringService.ResolveProfile(config, null, IPAddress.Parse("192.168.1.50"));
        Assert.Equal("exact", result);
    }

    [Fact]
    public void Cidr_Matches()
    {
        var config = new AppConfig
        {
            Clients = [new ClientConfig { Ids = ["192.168.1.0/24"], Profile = "lan" }]
        };
        var result = FilteringService.ResolveProfile(config, null, IPAddress.Parse("192.168.1.200"));
        Assert.Equal("lan", result);
    }

    [Fact]
    public void Cidr_LongestPrefixWins()
    {
        var config = new AppConfig
        {
            Clients =
            [
                new ClientConfig { Ids = ["192.168.0.0/16"], Profile = "broad" },
                new ClientConfig { Ids = ["192.168.1.0/24"], Profile = "narrow" }
            ]
        };
        var result = FilteringService.ResolveProfile(config, null, IPAddress.Parse("192.168.1.50"));
        Assert.Equal("narrow", result);
    }

    [Fact]
    public void MultipleIds_AnyCanMatch()
    {
        var config = new AppConfig
        {
            Clients = [new ClientConfig { Ids = ["10.0.0.1", "10.0.0.2", "alice"], Profile = "multi" }]
        };
        Assert.Equal("multi", FilteringService.ResolveProfile(config, null, IPAddress.Parse("10.0.0.2")));
        Assert.Equal("multi", FilteringService.ResolveProfile(config, "alice", IPAddress.Parse("99.99.99.99")));
    }

    [Fact]
    public void Priority_ClientId_Then_ExactIp_Then_Cidr_Then_Default()
    {
        var config = new AppConfig
        {
            DefaultProfile = "default",
            Clients =
            [
                new ClientConfig { Ids = ["192.168.1.0/24"], Profile = "cidr" },
                new ClientConfig { Ids = ["192.168.1.50"], Profile = "exact-ip" },
                new ClientConfig { Ids = ["alice"], Profile = "client-id" }
            ]
        };

        // ClientID match -> highest priority
        Assert.Equal("client-id", FilteringService.ResolveProfile(config, "alice", IPAddress.Parse("192.168.1.50")));
        // Exact IP -> beats CIDR
        Assert.Equal("exact-ip", FilteringService.ResolveProfile(config, null, IPAddress.Parse("192.168.1.50")));
        // CIDR match -> beats default
        Assert.Equal("cidr", FilteringService.ResolveProfile(config, null, IPAddress.Parse("192.168.1.99")));
        // No match -> default
        Assert.Equal("default", FilteringService.ResolveProfile(config, null, IPAddress.Parse("10.0.0.1")));
    }
}

[Trait("Category", "Unit")]
public class CidrMatchTests
{
    [Theory]
    [InlineData("192.168.1.50", "192.168.1.0/24", true, 24)]
    [InlineData("192.168.2.50", "192.168.1.0/24", false, 0)]
    [InlineData("100.64.5.10", "100.64.0.0/16", true, 16)]
    [InlineData("10.0.0.1", "10.0.0.1/32", true, 32)]
    [InlineData("10.0.0.2", "10.0.0.1/32", false, 0)]
    [InlineData("1.2.3.4", "0.0.0.0/0", true, 0)]
    [InlineData("192.168.1.200", "192.168.1.128/25", true, 25)]
    [InlineData("192.168.1.100", "192.168.1.128/25", false, 0)]
    [InlineData("fd00::1", "fd00::/64", true, 64)]
    [InlineData("fd01::1", "fd00::/16", false, 0)]
    [InlineData("192.168.1.1", "fd00::/64", false, 0)]
    [InlineData("192.168.1.1", "not-a-cidr", false, 0)]
    [InlineData("192.168.1.1", "192.168.1.0/33", false, 0)]
    // IPv6 edge cases
    [InlineData("fd00::1", "fd00::/128", false, 0)]       // /128 exact match check (not exact)
    [InlineData("fd00::1", "fd00::1/128", true, 128)]     // /128 exact match
    [InlineData("::1", "::1/128", true, 128)]             // loopback
    [InlineData("2001:db8::1", "2001:db8::/32", true, 32)] // documentation prefix
    [InlineData("2001:db8::1", "2001:db9::/32", false, 0)] // different /32
    [InlineData("fe80::1", "::/0", true, 0)]              // /0 matches all IPv6
    [InlineData("::ffff:192.168.1.1", "::ffff:192.168.1.0/120", true, 120)] // IPv4-mapped IPv6
    // IPv4 boundary prefix lengths
    [InlineData("10.0.0.1", "10.0.0.0/8", true, 8)]
    [InlineData("10.128.0.1", "10.0.0.0/8", true, 8)]
    [InlineData("11.0.0.1", "10.0.0.0/8", false, 0)]
    [InlineData("10.0.0.0", "10.0.0.0/31", true, 31)]
    [InlineData("10.0.0.1", "10.0.0.0/31", true, 31)]
    [InlineData("10.0.0.2", "10.0.0.0/31", false, 0)]
    // Malformed inputs
    [InlineData("10.0.0.1", "10.0.0.0/-1", false, 0)]
    [InlineData("10.0.0.1", "10.0.0.0/", false, 0)]
    [InlineData("10.0.0.1", "/24", false, 0)]
    [InlineData("10.0.0.1", "10.0.0.0", false, 0)]        // no prefix at all
    // IPv6 case-insensitive hex
    [InlineData("FD00::1", "fd00::/64", true, 64)]
    [InlineData("fd00::1", "FD00::/64", true, 64)]
    [InlineData("FD00::ABCD", "fd00::abcd/128", true, 128)]
    public void MatchesCidr(string ip, string cidr, bool expectedMatch, int expectedPrefix)
    {
        var result = FilteringService.MatchesCidr(IPAddress.Parse(ip), cidr, out var prefix);
        Assert.Equal(expectedMatch, result);
        if (expectedMatch)
            Assert.Equal(expectedPrefix, prefix);
    }
}
