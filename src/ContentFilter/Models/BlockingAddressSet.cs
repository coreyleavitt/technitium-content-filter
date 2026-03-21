using System.Net;
using System.Net.Sockets;

namespace ContentFilter.Models;

/// <summary>
/// Pre-parsed blocking addresses classified by type: IPv4, IPv6, and domain names.
/// Parsed once at compile time, never on the DNS hot path.
/// Domain names and IPs are mutually exclusive: if any domains are present, IPs are ignored
/// (CNAME must be the only record at a name per RFC 1034).
/// </summary>
public sealed class BlockingAddressSet
{
    public static readonly BlockingAddressSet Empty = new([], [], []);

    public IPAddress[] IPv4Addresses { get; }
    public IPAddress[] IPv6Addresses { get; }
    public string[] DomainNames { get; }
    public bool IsEmpty => IPv4Addresses.Length == 0 && IPv6Addresses.Length == 0 && DomainNames.Length == 0;

    public BlockingAddressSet(IPAddress[] ipv4, IPAddress[] ipv6, string[] domains)
    {
        IPv4Addresses = ipv4;
        IPv6Addresses = ipv6;
        DomainNames = domains;
    }

    /// <summary>
    /// Parses a list of address strings into classified sets.
    /// If any domain names are present, IP addresses are ignored and a warning is logged.
    /// Invalid entries are skipped.
    /// </summary>
    public static BlockingAddressSet Parse(IReadOnlyList<string>? addresses, Action<string>? log = null)
    {
        if (addresses is null || addresses.Count == 0)
            return Empty;

        var ipv4 = new List<IPAddress>();
        var ipv6 = new List<IPAddress>();
        var domains = new List<string>();

        foreach (var entry in addresses)
        {
            var trimmed = entry.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (IPAddress.TryParse(trimmed, out var ip))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    ipv4.Add(ip);
                else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                    ipv6.Add(ip);
            }
            else
            {
                // Treat as domain name (CNAME target)
                domains.Add(trimmed.TrimEnd('.'));
            }
        }

        // Domain names and IPs are mutually exclusive
        if (domains.Count > 0 && (ipv4.Count > 0 || ipv6.Count > 0))
        {
            log?.Invoke("blockingAddresses contains both domain names and IP addresses; IPs will be ignored (CNAME and address records cannot coexist).");
            ipv4.Clear();
            ipv6.Clear();
        }

        return new BlockingAddressSet(ipv4.ToArray(), ipv6.ToArray(), domains.ToArray());
    }
}
