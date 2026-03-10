namespace ContentFilter.Models;

/// <summary>
/// Pre-computed domain sets for a profile. Built at config load / list refresh time,
/// used for O(1) lookups on the DNS hot path. Never serialized.
/// </summary>
public sealed class CompiledProfile
{
    /// <summary>
    /// Union of: blocklist domains + custom block rules + service domains.
    /// </summary>
    public HashSet<string> BlockedDomains { get; }

    /// <summary>
    /// Union of: allowList entries + @@-prefixed custom rules (without the @@ prefix).
    /// </summary>
    public HashSet<string> AllowedDomains { get; }

    /// <summary>
    /// Domain -> rewrite config. Checked before block/allow in the hot path.
    /// </summary>
    public Dictionary<string, DnsRewriteConfig> Rewrites { get; }

    public CompiledProfile(
        HashSet<string> blockedDomains,
        HashSet<string> allowedDomains,
        Dictionary<string, DnsRewriteConfig>? rewrites = null)
    {
        BlockedDomains = blockedDomains;
        AllowedDomains = allowedDomains;
        Rewrites = rewrites ?? new Dictionary<string, DnsRewriteConfig>(StringComparer.OrdinalIgnoreCase);
    }
}
