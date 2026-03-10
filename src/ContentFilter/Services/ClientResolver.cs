using System.Net;
using ContentFilter.Models;
using TechnitiumLibrary.Net.Dns;

namespace ContentFilter.Services;

/// <summary>
/// Resolves a DNS client (identified by DoT client ID and/or IP address)
/// to a profile name using the configured client mappings.
///
/// Priority order:
/// 1. ClientID via DoH/DoT/DoQ domain name (case-insensitive)
/// 2. Exact IP match
/// 3. CIDR range (longest prefix match)
/// 4. Default profile
/// </summary>
internal sealed class ClientResolver
{
    /// <summary>
    /// Extracts the client identifier from DoH/DoT/DoQ metadata in a DNS request.
    /// Returns null for plain DNS (UDP/TCP) queries.
    /// </summary>
    internal static string? ExtractClientId(DnsDatagram request)
    {
        if (request.Metadata?.NameServer is null)
            return null;

        var nameServer = request.Metadata.NameServer;

        if (nameServer.DoHEndPoint is not null)
            return nameServer.DoHEndPoint.Host;

        if (nameServer.DomainEndPoint is not null)
            return nameServer.DomainEndPoint.Address;

        return null;
    }

    /// <summary>
    /// Resolves a client to a profile name using the configured client mappings.
    /// </summary>
    internal static string? ResolveProfile(AppConfig config, string? clientId, IPAddress clientIp)
    {
        // Priority 1: ClientID via DoH/DoT/DoQ domain name
        if (clientId is not null)
        {
            foreach (var client in config.Clients)
            {
                foreach (var id in client.Ids)
                {
                    if (!id.Contains('/') && !IPAddress.TryParse(id, out _))
                    {
                        if (id.Equals(clientId, StringComparison.OrdinalIgnoreCase))
                            return client.Profile;
                    }
                }
            }
        }

        // Priority 2: Exact IP match
        foreach (var client in config.Clients)
        {
            foreach (var id in client.Ids)
            {
                if (!id.Contains('/') && IPAddress.TryParse(id, out var configIp) && clientIp.Equals(configIp))
                    return client.Profile;
            }
        }

        // Priority 3: CIDR range (longest prefix match)
        ClientConfig? cidrMatch = null;
        int longestPrefix = -1;

        foreach (var client in config.Clients)
        {
            foreach (var id in client.Ids)
            {
                if (id.Contains('/'))
                {
                    if (MatchesCidr(clientIp, id, out var prefixLen) && prefixLen > longestPrefix)
                    {
                        cidrMatch = client;
                        longestPrefix = prefixLen;
                    }
                }
            }
        }

        if (cidrMatch is not null)
            return cidrMatch.Profile;

        return config.DefaultProfile;
    }

    /// <summary>
    /// Tests whether an IP address falls within a CIDR range.
    /// </summary>
    internal static bool MatchesCidr(IPAddress ip, string cidr, out int prefixLength)
    {
        prefixLength = 0;
        var parts = cidr.Split('/');
        if (parts.Length != 2)
            return false;

        if (!IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out prefixLength))
            return false;

        if (ip.AddressFamily != network.AddressFamily)
            return false;

        var maxPrefix = network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefix)
            return false;

        var ipBytes = ip.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (int i = 0; i < fullBytes && i < ipBytes.Length; i++)
        {
            if (ipBytes[i] != networkBytes[i])
                return false;
        }

        if (remainingBits > 0 && fullBytes < ipBytes.Length)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((ipBytes[fullBytes] & mask) != (networkBytes[fullBytes] & mask))
                return false;
        }

        return true;
    }
}
