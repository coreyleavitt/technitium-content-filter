using System.Net;
using ContentFilter.Models;

namespace ContentFilter.Tests;

[Trait("Category", "Unit")]
public class BlockingAddressSetTests
{
    [Fact]
    public void Parse_Null_ReturnsEmpty()
    {
        var result = BlockingAddressSet.Parse(null);

        Assert.Same(BlockingAddressSet.Empty, result);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Parse_EmptyList_ReturnsEmpty()
    {
        var result = BlockingAddressSet.Parse(new List<string>());

        Assert.Same(BlockingAddressSet.Empty, result);
    }

    [Fact]
    public void Parse_IPv4Only_ClassifiedCorrectly()
    {
        var result = BlockingAddressSet.Parse(new[] { "10.0.0.1", "192.168.1.1" });

        Assert.Equal(2, result.IPv4Addresses.Length);
        Assert.Empty(result.IPv6Addresses);
        Assert.Empty(result.DomainNames);
        Assert.False(result.IsEmpty);
    }

    [Fact]
    public void Parse_IPv6Only_ClassifiedCorrectly()
    {
        var result = BlockingAddressSet.Parse(new[] { "::1", "fd00::1" });

        Assert.Empty(result.IPv4Addresses);
        Assert.Equal(2, result.IPv6Addresses.Length);
        Assert.Empty(result.DomainNames);
    }

    [Fact]
    public void Parse_MixedIpv4AndIpv6_BothClassified()
    {
        var result = BlockingAddressSet.Parse(new[] { "10.0.0.1", "fd00::1" });

        Assert.Single(result.IPv4Addresses);
        Assert.Single(result.IPv6Addresses);
        Assert.Empty(result.DomainNames);
    }

    [Fact]
    public void Parse_DomainOnly_ClassifiedCorrectly()
    {
        var result = BlockingAddressSet.Parse(new[] { "blockpage.example.com" });

        Assert.Empty(result.IPv4Addresses);
        Assert.Empty(result.IPv6Addresses);
        Assert.Single(result.DomainNames);
        Assert.Equal("blockpage.example.com", result.DomainNames[0]);
    }

    [Fact]
    public void Parse_DomainWithTrailingDot_Trimmed()
    {
        var result = BlockingAddressSet.Parse(new[] { "blockpage.example.com." });

        Assert.Equal("blockpage.example.com", result.DomainNames[0]);
    }

    [Fact]
    public void Parse_MixedDomainsAndIps_DomainsWinIpsIgnored()
    {
        var logged = new List<string>();
        var result = BlockingAddressSet.Parse(
            new[] { "10.0.0.1", "blockpage.example.com", "fd00::1" },
            msg => logged.Add(msg));

        Assert.Empty(result.IPv4Addresses);
        Assert.Empty(result.IPv6Addresses);
        Assert.Single(result.DomainNames);
        Assert.Single(logged);
        Assert.Contains("CNAME", logged[0]);
    }

    [Fact]
    public void Parse_WhitespaceEntries_Skipped()
    {
        var result = BlockingAddressSet.Parse(new[] { "", "  ", "10.0.0.1" });

        Assert.Single(result.IPv4Addresses);
    }

    [Fact]
    public void Parse_EntriesAreTrimmed()
    {
        var result = BlockingAddressSet.Parse(new[] { "  10.0.0.1  " });

        Assert.Single(result.IPv4Addresses);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), result.IPv4Addresses[0]);
    }

    [Fact]
    public void Empty_IsEmpty()
    {
        Assert.True(BlockingAddressSet.Empty.IsEmpty);
        Assert.Empty(BlockingAddressSet.Empty.IPv4Addresses);
        Assert.Empty(BlockingAddressSet.Empty.IPv6Addresses);
        Assert.Empty(BlockingAddressSet.Empty.DomainNames);
    }
}
