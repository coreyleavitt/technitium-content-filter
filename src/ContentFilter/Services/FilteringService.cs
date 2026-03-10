using System.Net;
using ContentFilter.Models;
using TechnitiumLibrary.Net.Dns;

namespace ContentFilter.Services;

/// <summary>
/// Core filtering logic: determines whether a DNS query from a given client
/// should be blocked based on compiled profile domain sets and schedule.
///
/// Evaluation order:
/// 1. Blocking disabled globally? -> ALLOW
/// 2. Resolve client -> profile
/// 3. No profile? -> use base profile only (if set)
/// 4. Domain matches rewrite? (profile + base rewrites) -> REWRITE
/// 5. Domain in profile's AllowedDomains? -> ALLOW (overrides base blocks)
/// 6. Schedule inactive? -> ALLOW
/// 7. Domain in merged BlockedDomains? (profile + base) -> BLOCK
/// 8. -> ALLOW (no match)
/// </summary>
public sealed class FilteringService
{
    private readonly ConfigService _configService;
    private volatile Dictionary<string, CompiledProfile> _compiledProfiles = new();

    public FilteringService(ConfigService configService)
    {
        _configService = configService;
    }

    /// <summary>
    /// Atomically swaps in newly compiled profiles. Called from App.cs after
    /// config reload or blocklist refresh.
    /// </summary>
    public void UpdateCompiledProfiles(Dictionary<string, CompiledProfile> profiles)
    {
        _compiledProfiles = profiles;
    }

    /// <summary>
    /// Returns true if the query should be allowed, false if it should be blocked.
    /// Also returns a rewrite config if the domain matches a rewrite rule.
    /// </summary>
    public bool IsAllowed(DnsDatagram request, IPEndPoint remoteEP, string questionDomain,
        out string debugInfo, out DnsRewriteConfig? rewrite)
    {
        rewrite = null;
        try
        {
            return IsAllowedCore(request, remoteEP, questionDomain, out debugInfo, out rewrite);
        }
        catch (Exception ex)
        {
            // A DNS filtering plugin must never crash the host server. Fail open.
            debugInfo = $"ERROR: {ex.Message}";
            return true;
        }
    }

    /// <summary>
    /// Looks up a rewrite for the given domain by walking up the domain hierarchy.
    /// Returns the matching DnsRewriteConfig or null.
    /// </summary>
    public static DnsRewriteConfig? GetRewrite(Dictionary<string, DnsRewriteConfig> rewrites, string domain)
    {
        if (rewrites.Count == 0)
            return null;

        var span = domain.AsSpan().TrimEnd('.');
        while (true)
        {
            var key = span.ToString();
            if (rewrites.TryGetValue(key, out var rw))
                return rw;

            var dotIndex = span.IndexOf('.');
            if (dotIndex < 0 || dotIndex == span.Length - 1)
                break;

            span = span[(dotIndex + 1)..];
        }

        return null;
    }

    private bool IsAllowedCore(DnsDatagram request, IPEndPoint remoteEP, string questionDomain,
        out string debugInfo, out DnsRewriteConfig? rewrite)
    {
        rewrite = null;
        var config = _configService.Config;

        // 1. Global kill switch
        if (!config.EnableBlocking)
        {
            debugInfo = "blocking disabled";
            return true;
        }

        // 2. Resolve client -> profile
        var clientId = ExtractClientId(request);
        var profileName = ResolveProfile(config, clientId, remoteEP.Address);

        debugInfo = $"domain={questionDomain} clientId={clientId ?? "null"} ip={remoteEP.Address} profile={profileName ?? "null"}";

        // 3. No profile -> use base profile if set
        if (profileName is null)
        {
            if (!string.IsNullOrWhiteSpace(config.BaseProfile))
                profileName = config.BaseProfile;
            else
                return true;
        }

        if (!config.Profiles.TryGetValue(profileName, out var profileConfig))
        {
            debugInfo += " (profile not found)";
            return true;
        }

        // Fail-open: if profile hasn't been compiled yet (e.g., during startup before
        // background compilation completes), allow queries rather than block them.
        // A DNS filtering plugin must never silently deny legitimate traffic.
        var compiled = _compiledProfiles;
        if (!compiled.TryGetValue(profileName, out var profile))
        {
            debugInfo += " (not compiled)";
            return true;
        }

        // 4. DNS Rewrite check
        var rw = GetRewrite(profile.Rewrites, questionDomain);
        if (rw is not null)
        {
            rewrite = rw;
            debugInfo += $" REWRITE -> {rw.Answer}";
            return false; // Signal that we handle this query (not a normal allow)
        }

        // 5. Allow list overrides everything
        if (profile.AllowedDomains.Count > 0 && DomainMatcher.Matches(profile.AllowedDomains, questionDomain))
        {
            debugInfo += " (allowlisted)";
            return true;
        }

        // 6. Schedule check
        if (!IsBlockingActiveNow(profileConfig, config.TimeZone, config.ScheduleAllDay))
        {
            debugInfo += " (outside schedule)";
            return true;
        }

        // 7. Blocked domains check (single HashSet lookup with subdomain walking)
        if (profile.BlockedDomains.Count > 0 && DomainMatcher.Matches(profile.BlockedDomains, questionDomain))
        {
            debugInfo += " BLOCKED";
            return false;
        }

        // 8. No match -> allow
        return true;
    }

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

    /// <summary>
    /// Determines whether blocking is active right now for the given profile.
    /// Schedule semantics:
    ///   - No schedule at all -> blocking always active
    ///   - Day has no entry -> blocking active (always-on for unscheduled days)
    ///   - Day has windows -> blocking active only DURING those windows, inactive outside
    /// </summary>
    internal static bool IsBlockingActiveNow(ProfileConfig profile, string timeZoneId, bool scheduleAllDay, DateTime? utcNow = null)
    {
        if (profile.Schedule is null || profile.Schedule.Count == 0)
            return true;

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.Utc;
        }

        var now = TimeZoneInfo.ConvertTimeFromUtc(utcNow ?? DateTime.UtcNow, tz);
        var dayKey = now.DayOfWeek switch
        {
            DayOfWeek.Sunday => "sun",
            DayOfWeek.Monday => "mon",
            DayOfWeek.Tuesday => "tue",
            DayOfWeek.Wednesday => "wed",
            DayOfWeek.Thursday => "thu",
            DayOfWeek.Friday => "fri",
            DayOfWeek.Saturday => "sat",
            _ => ""
        };

        if (!profile.Schedule.TryGetValue(dayKey, out var windows) || windows.Count == 0)
            return true;

        var currentTime = TimeOnly.FromDateTime(now);

        foreach (var window in windows)
        {
            if (scheduleAllDay || window.AllDay)
                return true;

            var start = window.StartTime;
            var end = window.EndTime;
            if (start is null || end is null)
                continue;

            var inWindow = start.Value <= end.Value
                ? currentTime >= start.Value && currentTime <= end.Value
                : currentTime >= start.Value || currentTime <= end.Value;

            if (inWindow)
                return true;
        }

        // Outside all defined windows for this day -> blocking inactive
        return false;
    }
}
