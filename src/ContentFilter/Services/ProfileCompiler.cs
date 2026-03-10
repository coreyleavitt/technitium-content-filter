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

    public ProfileCompiler(ServiceRegistry serviceRegistry, BlockListManager? blockListManager = null)
    {
        _serviceRegistry = serviceRegistry;
        _blockListManager = blockListManager;
    }

    /// <summary>
    /// Compiles all profiles in the config into a dictionary of CompiledProfile keyed by profile name.
    /// If a base profile is set, its domains/rewrites are merged into all other profiles.
    /// </summary>
    public Dictionary<string, CompiledProfile> CompileAll(AppConfig config)
    {
        // First pass: compile each profile standalone
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

            // Merge: base blocked + profile blocked
            var mergedBlocked = new HashSet<string>(baseCompiled.BlockedDomains, StringComparer.OrdinalIgnoreCase);
            foreach (var d in compiled.BlockedDomains)
                mergedBlocked.Add(d);

            // Merge: base allowed + profile allowed
            var mergedAllowed = new HashSet<string>(baseCompiled.AllowedDomains, StringComparer.OrdinalIgnoreCase);
            foreach (var d in compiled.AllowedDomains)
                mergedAllowed.Add(d);

            // Merge rewrites: base first, profile overwrites on conflict
            var mergedRewrites = new Dictionary<string, DnsRewriteConfig>(
                baseCompiled.Rewrites, StringComparer.OrdinalIgnoreCase);
            foreach (var (domain, rewrite) in compiled.Rewrites)
                mergedRewrites[domain] = rewrite;

            result[name] = new CompiledProfile(mergedBlocked, mergedAllowed, mergedRewrites);
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

        // 4. Blocklist domains -- profile.BlockLists now contains URL strings
        //    that reference global config.BlockLists entries
        if (_blockListManager is not null)
        {
            var globalByUrl = config.BlockLists
                .Where(bl => bl.Enabled && !string.IsNullOrWhiteSpace(bl.Url))
                .ToDictionary(bl => bl.Url, StringComparer.OrdinalIgnoreCase);

            foreach (var url in profile.BlockLists)
            {
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                // Only include if the global entry exists and is enabled
                if (!globalByUrl.ContainsKey(url))
                    continue;

                var domains = _blockListManager.GetDomains(url);
                if (domains is not null)
                {
                    foreach (var domain in domains)
                        blocked.Add(domain);
                }
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

        return new CompiledProfile(blocked, allowed, rewrites);
    }
}
