using ContentFilter.Models;

namespace ContentFilter.Services;

/// <summary>
/// Evaluates domain-level filtering decisions: rewrites, allowlists, and blocklists.
/// Uses DomainMatcher for subdomain-walking lookups.
/// </summary>
internal static class DomainEvaluator
{
    /// <summary>
    /// Looks up a rewrite for the given domain by walking up the domain hierarchy.
    /// Returns the matching DnsRewriteConfig or null.
    /// #14: Uses string slicing instead of span.ToString() to reduce hot-path allocations.
    /// </summary>
    internal static DnsRewriteConfig? GetRewrite(Dictionary<string, DnsRewriteConfig> rewrites, string domain)
    {
        if (rewrites.Count == 0)
            return null;

        // Trim trailing FQDN dot
        var trimmed = domain.EndsWith('.') ? domain[..^1] : domain;

        // Walk up the domain hierarchy using string slicing (#14)
        var current = trimmed;
        while (true)
        {
            if (rewrites.TryGetValue(current, out var rw))
                return rw;

            var dotIndex = current.IndexOf('.');
            if (dotIndex < 0 || dotIndex == current.Length - 1)
                break;

            current = current[(dotIndex + 1)..];
        }

        return null;
    }

    /// <summary>
    /// Returns true if the domain matches the allowlist.
    /// </summary>
    internal static bool IsAllowlisted(CompiledProfile profile, string domain)
    {
        return profile.AllowedDomains.Count > 0 && DomainMatcher.Matches(profile.AllowedDomains, domain);
    }

    /// <summary>
    /// Returns true if the domain matches the blocklist.
    /// </summary>
    internal static bool IsBlocked(CompiledProfile profile, string domain)
    {
        return profile.BlockedDomains.Count > 0 && DomainMatcher.Matches(profile.BlockedDomains, domain);
    }
}
