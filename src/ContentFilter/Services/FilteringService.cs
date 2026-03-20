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
    /// Evaluates a DNS query and returns a structured FilterResult describing the
    /// filtering decision, including block reason provenance for diagnostics.
    /// </summary>
    public FilterResult Evaluate(DnsDatagram request, IPEndPoint remoteEP, string questionDomain)
    {
        try
        {
            return EvaluateCore(request, remoteEP, questionDomain);
        }
        catch (Exception ex)
        {
            // A DNS filtering plugin must never crash the host server. Fail open.
            return new FilterResult
            {
                Action = FilterAction.Allow,
                ClientIp = remoteEP.Address.ToString(),
                QuestionDomain = questionDomain,
                DebugSummary = $"ERROR: {ex.Message}"
            };
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

    private FilterResult EvaluateCore(DnsDatagram request, IPEndPoint remoteEP, string questionDomain)
    {
        var config = _configService.Config;

        // 1. Global kill switch
        if (!config.EnableBlocking)
        {
            return new FilterResult
            {
                Action = FilterAction.Allow,
                ClientIp = remoteEP.Address.ToString(),
                QuestionDomain = questionDomain,
                DebugSummary = "blocking disabled"
            };
        }

        // 2. Resolve client -> profile (#18: delegate to ClientResolver)
        var clientId = ClientResolver.ExtractClientId(request);
        var profileName = ClientResolver.ResolveProfile(config, clientId, remoteEP.Address);

        // 3. No profile -> use base profile if set
        if (profileName is null)
        {
            if (!string.IsNullOrWhiteSpace(config.BaseProfile))
                profileName = config.BaseProfile;
            else
            {
                return new FilterResult
                {
                    Action = FilterAction.Allow,
                    ClientId = clientId,
                    ClientIp = remoteEP.Address.ToString(),
                    QuestionDomain = questionDomain,
                    DebugSummary = BuildDebugPrefix(questionDomain, clientId, remoteEP.Address, profileName)
                };
            }
        }

        if (!config.Profiles.TryGetValue(profileName, out var profileConfig))
        {
            return new FilterResult
            {
                Action = FilterAction.Allow,
                ProfileName = profileName,
                ClientId = clientId,
                ClientIp = remoteEP.Address.ToString(),
                QuestionDomain = questionDomain,
                DebugSummary = BuildDebugPrefix(questionDomain, clientId, remoteEP.Address, profileName) + " (profile not found)"
            };
        }

        // Fail-open: if profile hasn't been compiled yet (e.g., during startup before
        // background compilation completes), allow queries rather than block them.
        var compiled = _compiledProfiles;
        if (!compiled.TryGetValue(profileName, out var profile))
        {
            return new FilterResult
            {
                Action = FilterAction.Allow,
                ProfileName = profileName,
                ClientId = clientId,
                ClientIp = remoteEP.Address.ToString(),
                QuestionDomain = questionDomain,
                DebugSummary = BuildDebugPrefix(questionDomain, clientId, remoteEP.Address, profileName) + " (not compiled)"
            };
        }

        var debugPrefix = BuildDebugPrefix(questionDomain, clientId, remoteEP.Address, profileName);

        // 4. DNS Rewrite check (#18: delegate to DomainEvaluator)
        var rw = DomainEvaluator.GetRewrite(profile.Rewrites, questionDomain);
        if (rw is not null)
        {
            return new FilterResult
            {
                Action = FilterAction.Rewrite,
                ProfileName = profileName,
                ClientId = clientId,
                ClientIp = remoteEP.Address.ToString(),
                QuestionDomain = questionDomain,
                Rewrite = rw,
                DebugSummary = debugPrefix + $" REWRITE -> {rw.Answer}"
            };
        }

        // 5. Allow list overrides everything (#18: delegate to DomainEvaluator)
        if (DomainEvaluator.IsAllowlisted(profile, questionDomain))
        {
            return new FilterResult
            {
                Action = FilterAction.Allow,
                ProfileName = profileName,
                ClientId = clientId,
                ClientIp = remoteEP.Address.ToString(),
                QuestionDomain = questionDomain,
                DebugSummary = debugPrefix + " (allowlisted)"
            };
        }

        // 6. Regex allow rules (overrides domain blocks and regex blocks)
        if (DomainEvaluator.IsRegexAllowlisted(profile, questionDomain))
        {
            return new FilterResult
            {
                Action = FilterAction.Allow,
                ProfileName = profileName,
                ClientId = clientId,
                ClientIp = remoteEP.Address.ToString(),
                QuestionDomain = questionDomain,
                DebugSummary = debugPrefix + " (regex allowlisted)"
            };
        }

        // 7. Schedule check (#18: delegate to ScheduleEvaluator)
        if (!ScheduleEvaluator.IsBlockingActiveNow(profileConfig, config.TimeZone, config.ScheduleAllDay))
        {
            return new FilterResult
            {
                Action = FilterAction.Allow,
                ProfileName = profileName,
                ClientId = clientId,
                ClientIp = remoteEP.Address.ToString(),
                QuestionDomain = questionDomain,
                DebugSummary = debugPrefix + " (outside schedule)"
            };
        }

        // 8. Blocked domains check (#18: delegate to DomainEvaluator)
        var matchedDomain = DomainEvaluator.FindBlockedDomain(profile, questionDomain);
        if (matchedDomain is not null)
        {
            return new FilterResult
            {
                Action = FilterAction.Block,
                ProfileName = profileName,
                ClientId = clientId,
                ClientIp = remoteEP.Address.ToString(),
                QuestionDomain = questionDomain,
                BlockReason = new BlockReason
                {
                    Source = BlockSource.DomainBlocklist,
                    MatchedDomain = matchedDomain
                },
                DebugSummary = debugPrefix + " BLOCKED"
            };
        }

        // 9. Regex block rules
        var matchedRegex = DomainEvaluator.FindBlockingRegex(profile, questionDomain);
        if (matchedRegex is not null)
        {
            return new FilterResult
            {
                Action = FilterAction.Block,
                ProfileName = profileName,
                ClientId = clientId,
                ClientIp = remoteEP.Address.ToString(),
                QuestionDomain = questionDomain,
                BlockReason = new BlockReason
                {
                    Source = BlockSource.RegexBlocklist,
                    MatchedRegex = matchedRegex
                },
                DebugSummary = debugPrefix + " BLOCKED (regex)"
            };
        }

        // 10. No match -> allow
        return new FilterResult
        {
            Action = FilterAction.Allow,
            ProfileName = profileName,
            ClientId = clientId,
            ClientIp = remoteEP.Address.ToString(),
            QuestionDomain = questionDomain,
            DebugSummary = debugPrefix
        };
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
