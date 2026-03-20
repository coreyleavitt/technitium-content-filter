using System.Net;
using ContentFilter.Models;
using TechnitiumLibrary.Net.Dns;

namespace ContentFilter.Services;

/// <summary>
/// Core filtering logic: determines whether a DNS query from a given client
/// should be blocked based on compiled profile domain sets and schedule.
///
/// Evaluation order (10 steps):
/// 1. Blocking disabled globally? -> ALLOW
/// 2. Resolve client -> profile
/// 3. No profile? -> use base profile only (if set)
/// 4. Domain matches rewrite? (profile + base rewrites) -> REWRITE
/// 5. Domain in profile's AllowedDomains? -> ALLOW
/// 6. Domain matches regex allow rules? -> ALLOW
/// 7. Schedule inactive? -> ALLOW
/// 8. Domain in merged BlockedDomains? -> BLOCK
/// 9. Domain matches regex block rules? -> BLOCK
/// 10. -> ALLOW (no match)
///
/// Delegates to ClientResolver, ScheduleEvaluator, and DomainEvaluator (#18).
/// </summary>
public sealed class FilteringService
{
    private readonly ConfigService _configService;
    private volatile Dictionary<string, CompiledProfile> _compiledProfiles = new(StringComparer.OrdinalIgnoreCase);

    public FilteringService(ConfigService configService)
    {
        _configService = configService;
    }

    /// <summary>
    /// Returns the number of currently compiled profiles (#27).
    /// </summary>
    internal int CompiledProfileCount => _compiledProfiles.Count;

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
    /// Delegates to DomainEvaluator (#18).
    /// </summary>
    public static DnsRewriteConfig? GetRewrite(Dictionary<string, DnsRewriteConfig> rewrites, string domain)
    {
        return DomainEvaluator.GetRewrite(rewrites, domain);
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

        // 2. Resolve client -> profile (#18: delegate to ClientResolver)
        var clientId = ClientResolver.ExtractClientId(request);
        var profileName = ClientResolver.ResolveProfile(config, clientId, remoteEP.Address);

        // #22: Only build debugInfo string when we actually need it (lazy construction)
        // Build minimal debug info upfront; append details only on interesting paths
        string? lazyDebugPrefix = null;

        // 3. No profile -> use base profile if set
        if (profileName is null)
        {
            if (!string.IsNullOrWhiteSpace(config.BaseProfile))
                profileName = config.BaseProfile;
            else
            {
                debugInfo = BuildDebugPrefix(questionDomain, clientId, remoteEP.Address, profileName);
                return true;
            }
        }

        if (!config.Profiles.TryGetValue(profileName, out var profileConfig))
        {
            debugInfo = BuildDebugPrefix(questionDomain, clientId, remoteEP.Address, profileName) + " (profile not found)";
            return true;
        }

        // Fail-open: if profile hasn't been compiled yet (e.g., during startup before
        // background compilation completes), allow queries rather than block them.
        var compiled = _compiledProfiles;
        if (!compiled.TryGetValue(profileName, out var profile))
        {
            debugInfo = BuildDebugPrefix(questionDomain, clientId, remoteEP.Address, profileName) + " (not compiled)";
            return true;
        }

        lazyDebugPrefix = BuildDebugPrefix(questionDomain, clientId, remoteEP.Address, profileName);

        // 4. DNS Rewrite check (#18: delegate to DomainEvaluator)
        var rw = DomainEvaluator.GetRewrite(profile.Rewrites, questionDomain);
        if (rw is not null)
        {
            rewrite = rw;
            debugInfo = lazyDebugPrefix + $" REWRITE -> {rw.Answer}";
            return false; // Signal that we handle this query (not a normal allow)
        }

        // 5. Allow list overrides everything (#18: delegate to DomainEvaluator)
        if (DomainEvaluator.IsAllowlisted(profile, questionDomain))
        {
            debugInfo = lazyDebugPrefix + " (allowlisted)";
            return true;
        }

        // 6. Regex allow rules (overrides domain blocks and regex blocks)
        if (DomainEvaluator.IsRegexAllowlisted(profile, questionDomain))
        {
            debugInfo = lazyDebugPrefix + " (regex allowlisted)";
            return true;
        }

        // 7. Schedule check (#18: delegate to ScheduleEvaluator)
        if (!ScheduleEvaluator.IsBlockingActiveNow(profileConfig, config.TimeZone, config.ScheduleAllDay))
        {
            debugInfo = lazyDebugPrefix + " (outside schedule)";
            return true;
        }

        // 8. Blocked domains check (#18: delegate to DomainEvaluator)
        if (DomainEvaluator.IsBlocked(profile, questionDomain))
        {
            debugInfo = lazyDebugPrefix + " BLOCKED";
            return false;
        }

        // 9. Regex block rules
        if (DomainEvaluator.IsRegexBlocked(profile, questionDomain))
        {
            debugInfo = lazyDebugPrefix + " BLOCKED (regex)";
            return false;
        }

        // 10. No match -> allow
        debugInfo = lazyDebugPrefix;
        return true;
    }

    /// <summary>
    /// #22: Build debug prefix string only when needed.
    /// </summary>
    private static string BuildDebugPrefix(string domain, string? clientId, IPAddress clientIp, string? profileName)
    {
        return $"domain={domain} clientId={clientId ?? "null"} ip={clientIp} profile={profileName ?? "null"}";
    }

    // --- Static wrappers for backward compatibility with tests ---

    /// <summary>
    /// Static wrapper for ClientResolver.ExtractClientId, for test compatibility.
    /// </summary>
    internal static string? ExtractClientId(DnsDatagram request)
    {
        return ClientResolver.ExtractClientId(request);
    }

    /// <summary>
    /// Static wrapper for ClientResolver.ResolveProfile, for test compatibility.
    /// </summary>
    internal static string? ResolveProfile(AppConfig config, string? clientId, IPAddress clientIp)
    {
        return ClientResolver.ResolveProfile(config, clientId, clientIp);
    }

    /// <summary>
    /// Static wrapper for ClientResolver.MatchesCidr, for test compatibility.
    /// </summary>
    internal static bool MatchesCidr(IPAddress ip, string cidr, out int prefixLength)
    {
        return ClientResolver.MatchesCidr(ip, cidr, out prefixLength);
    }

    /// <summary>
    /// Static wrapper for ScheduleEvaluator.IsBlockingActiveNow, for test compatibility.
    /// </summary>
    internal static bool IsBlockingActiveNow(ProfileConfig profile, string timeZoneId, bool scheduleAllDay, DateTime? utcNow = null)
    {
        return ScheduleEvaluator.IsBlockingActiveNow(profile, timeZoneId, scheduleAllDay, utcNow);
    }
}
