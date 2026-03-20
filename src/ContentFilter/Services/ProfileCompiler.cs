using System.Text.RegularExpressions;
using ContentFilter.Models;

namespace ContentFilter.Services;

/// <summary>
/// Builds CompiledProfile instances from config + service registry + blocklist data.
/// Called on config reload or blocklist refresh. Result is swapped atomically.
/// </summary>
public sealed class ProfileCompiler
{
    private readonly ServiceRegistry _serviceRegistry;
    private readonly BlockListManager? _blockListManager;
    private readonly Action<string>? _log;

    public ProfileCompiler(ServiceRegistry serviceRegistry, BlockListManager? blockListManager = null, Action<string>? log = null)
    {
        _serviceRegistry = serviceRegistry;
        _blockListManager = blockListManager;
        _log = log;
    }

    /// <summary>
    /// Compiles all profiles in the config into a dictionary of CompiledProfile keyed by profile name.
    /// If a base profile is set, its domains/rewrites are merged into all other profiles.
    /// #16: Uses OrdinalIgnoreCase for profile dictionary.
    /// </summary>
    public Dictionary<string, CompiledProfile> CompileAll(AppConfig config)
    {
        // First pass: compile each profile standalone
        // #16: Use OrdinalIgnoreCase for profile dictionary
        var standalone = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, profile) in config.Profiles)
        {
            standalone[name] = Compile(profile, config);
        }

        // If no base profile, return as-is
        if (string.IsNullOrWhiteSpace(config.BaseProfile)
            || !standalone.TryGetValue(config.BaseProfile, out var baseCompiled))
        {
            return standalone;
        }

        // Second pass: merge base profile into all others
        var result = new Dictionary<string, CompiledProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, compiled) in standalone)
        {
            if (name.Equals(config.BaseProfile, StringComparison.OrdinalIgnoreCase))
            {
                result[name] = compiled;
                continue;
            }

            // #20: Create new HashSet from union instead of mutating source sets
            var mergedBlocked = new HashSet<string>(baseCompiled.BlockedDomains, StringComparer.OrdinalIgnoreCase);
            foreach (var d in compiled.BlockedDomains)
                mergedBlocked.Add(d);

            var mergedAllowed = new HashSet<string>(baseCompiled.AllowedDomains, StringComparer.OrdinalIgnoreCase);
            foreach (var d in compiled.AllowedDomains)
                mergedAllowed.Add(d);

            // Merge rewrites: base first, profile overwrites on conflict
            var mergedRewrites = new Dictionary<string, DnsRewriteConfig>(
                baseCompiled.Rewrites, StringComparer.OrdinalIgnoreCase);
            foreach (var (domain, rewrite) in compiled.Rewrites)
                mergedRewrites[domain] = rewrite;

            // Merge regex arrays: base + child concatenated
            var mergedBlockedRegexes = baseCompiled.BlockedRegexes.Concat(compiled.BlockedRegexes).ToArray();
            var mergedAllowedRegexes = baseCompiled.AllowedRegexes.Concat(compiled.AllowedRegexes).ToArray();

            result[name] = new CompiledProfile(mergedBlocked, mergedAllowed, mergedRewrites, mergedBlockedRegexes, mergedAllowedRegexes);
        }

        return result;
    }

    private CompiledProfile Compile(ProfileConfig profile, AppConfig config)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Service domains from blocked services
        foreach (var serviceId in profile.BlockedServices)
        {
            if (_serviceRegistry.Services.TryGetValue(serviceId, out var svc))
            {
                foreach (var domain in svc.Domains)
                    blocked.Add(domain);
            }
        }

        // 2. Custom rules: plain domain -> blocked, @@domain -> allowed
        foreach (var rule in profile.CustomRules)
        {
            var trimmed = rule.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            if (trimmed.StartsWith("@@"))
            {
                var domain = trimmed[2..].Trim();
                if (!string.IsNullOrEmpty(domain))
                    allowed.Add(domain);
            }
            else
            {
                blocked.Add(trimmed);
            }
        }

        // 3. Allow list entries
        foreach (var entry in profile.AllowList)
        {
            var trimmed = entry.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                allowed.Add(trimmed);
        }

        // 4. Build global blocklist lookup (shared by domain and regex steps)
        // #12: Null check/coalesce for profile.BlockLists
        var globalByUrl = new Dictionary<string, BlockListConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var bl in config.BlockLists)
        {
            if (bl.Enabled && !string.IsNullOrWhiteSpace(bl.Url))
                globalByUrl.TryAdd(bl.Url, bl);
        }

        // 4a. Blocklist domains
        if (_blockListManager is not null && profile.BlockLists is not null)
        {
            foreach (var url in profile.BlockLists)
            {
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                if (!globalByUrl.TryGetValue(url, out var blConfig) || blConfig.Type == "regex")
                    continue;

                var domains = _blockListManager.GetDomains(url);
                if (domains is not null)
                {
                    foreach (var domain in domains)
                        blocked.Add(domain);
                }
            }
        }

        // 4b. Remote regex blocklist patterns
        var remoteRegexPatterns = new List<string>();
        if (_blockListManager is not null && profile.BlockLists is not null)
        {
            foreach (var url in profile.BlockLists)
            {
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                if (!globalByUrl.TryGetValue(url, out var blConfig) || blConfig.Type != "regex")
                    continue;

                var patterns = _blockListManager.GetPatterns(url);
                if (patterns is not null)
                    remoteRegexPatterns.AddRange(patterns);
            }
        }

        // 5. DNS Rewrites
        var rewrites = new Dictionary<string, DnsRewriteConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var rw in profile.DnsRewrites)
        {
            var domain = rw.Domain?.Trim().TrimEnd('.');
            if (!string.IsNullOrEmpty(domain) && !string.IsNullOrEmpty(rw.Answer?.Trim()))
                rewrites[domain] = rw;
        }

        // 6. Regex rules (inline + remote patterns combined)
        var combinedBlockPatterns = new List<string>(profile.RegexBlockRules);
        combinedBlockPatterns.AddRange(remoteRegexPatterns);
        var blockedRegexes = RegexCompiler.Compile(combinedBlockPatterns, _log);
        var allowedRegexes = RegexCompiler.Compile(profile.RegexAllowRules, _log);

        return new CompiledProfile(blocked, allowed, rewrites, blockedRegexes, allowedRegexes);
    }
}
