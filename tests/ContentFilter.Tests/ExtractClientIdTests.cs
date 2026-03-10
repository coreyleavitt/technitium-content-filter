using ContentFilter.Services;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace ContentFilter.Tests;

/// <summary>
/// Tests for FilteringService.ExtractClientId which extracts client identifiers
/// from DoH/DoT/DoQ metadata in the DnsDatagram.
///
/// Note: The happy-path tests (DoH/DoT metadata present) cannot be written as unit tests
/// because DnsDatagram.Metadata is set internally by Technitium's query pipeline.
/// The NameServer, DoHEndPoint, and DomainEndPoint types are not publicly constructible.
/// Happy-path behavior is verified via integration tests with real DoT queries.
/// </summary>
[Trait("Category", "Unit")]
public class ExtractClientIdTests
{
    private static DnsDatagram MakeRequest(string domain = "example.com")
    {
        var question = new DnsQuestionRecord(domain, DnsResourceRecordType.A, DnsClass.IN);
        return new DnsDatagram(
            0, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, new[] { question });
    }

    [Fact]
    public void NullMetadata_ReturnsNull()
    {
        var request = MakeRequest();
        // Standard request has no metadata
        var result = FilteringService.ExtractClientId(request);
        Assert.Null(result);
    }

    [Fact]
    public void PlainDnsRequest_ReturnsNull()
    {
        // A normal UDP/TCP DNS request has no DoH/DoT metadata
        var request = MakeRequest("google.com");
        Assert.Null(FilteringService.ExtractClientId(request));
    }

    [Fact(Skip = "DnsDatagram.Metadata is set internally by Technitium's query pipeline; " +
                 "NameServer, DoHEndPoint, and DomainEndPoint are not publicly constructible. " +
                 "Happy-path extraction is tested via integration tests with real DoT queries.")]
    public void DoHMetadata_ReturnsHost()
    {
        // Placeholder documenting the untestable happy path.
        // In production, ExtractClientId extracts:
        //   - nameServer.DoHEndPoint.Host for DoH queries
        //   - nameServer.DomainEndPoint.Address for DoT/DoQ queries
    }
}
