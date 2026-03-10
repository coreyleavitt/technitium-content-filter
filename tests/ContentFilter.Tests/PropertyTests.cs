using System.Net;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using ContentFilter.Models;
using ContentFilter.Services;

namespace ContentFilter.Tests;

/// <summary>
/// FsCheck configuration for reproducible property tests.
///
/// When a property test fails, FsCheck prints the seed value in the output.
/// To reproduce a failure, add Replay = "seed,size" to the [Property] attribute:
///   [Property(Replay = "12345,42")]
///
/// All property tests use QuietOnSuccess = false so seeds are always logged,
/// making any failure trivially reproducible.
/// </summary>
public class ReproducibleProperties : PropertiesAttribute
{
    public ReproducibleProperties()
    {
        QuietOnSuccess = false;
    }
}

/// <summary>
/// Custom generators for DNS-related types.
/// </summary>
public static class DnsGenerators
{
    /// <summary>
    /// Generates valid domain labels (1-20 lowercase alphanumeric chars).
    /// </summary>
    public static Gen<string> DomainLabel()
    {
        return Gen.Choose(1, 20).SelectMany(len =>
            Gen.Elements("abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray())
            .ArrayOf(len)
            .Select(chars => new string(chars)));
    }

    /// <summary>
    /// Generates valid domain names with 2-4 labels (e.g., "abc.example.com").
    /// </summary>
    public static Gen<string> DomainName()
    {
        return Gen.Choose(2, 4).SelectMany(labelCount =>
            DomainLabel().ArrayOf(labelCount)
            .Select(labels => string.Join('.', labels)));
    }

    /// <summary>
    /// Generates a subdomain prefix (1-3 labels) to prepend to a domain.
    /// </summary>
    public static Gen<string> SubdomainPrefix()
    {
        return Gen.Choose(1, 3).SelectMany(labelCount =>
            DomainLabel().ArrayOf(labelCount)
            .Select(labels => string.Join('.', labels)));
    }

    /// <summary>
    /// Generates valid domain labels of exactly 63 characters (max allowed by DNS spec).
    /// </summary>
    public static Gen<string> MaxLengthLabel()
    {
        return Gen.Elements("abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray())
            .ArrayOf(63)
            .Select(chars => new string(chars));
    }

    /// <summary>
    /// Generates single-label domains (no dots).
    /// </summary>
    public static Gen<string> SingleLabel()
    {
        return Gen.Choose(1, 20).SelectMany(len =>
            Gen.Elements("abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray())
            .ArrayOf(len)
            .Select(chars => new string(chars)));
    }

    /// <summary>
    /// Generates valid IPv4 addresses.
    /// </summary>
    public static Gen<IPAddress> IPv4Address()
    {
        return Gen.Choose(0, 255).ArrayOf(4)
            .Select(bytes => new IPAddress(bytes.Select(b => (byte)b).ToArray()));
    }

    /// <summary>
    /// Generates a CIDR prefix length for IPv4 (8-32).
    /// </summary>
    public static Gen<int> CidrPrefix()
    {
        return Gen.Choose(8, 32);
    }

    /// <summary>
    /// Generates valid IPv6 addresses.
    /// </summary>
    public static Gen<IPAddress> IPv6Address()
    {
        return Gen.Choose(0, 255).ArrayOf(16)
            .Select(bytes => new IPAddress(bytes.Select(b => (byte)b).ToArray()));
    }

    /// <summary>
    /// Generates a CIDR prefix length for IPv6 (8-128).
    /// </summary>
    public static Gen<int> CidrPrefixV6()
    {
        return Gen.Choose(8, 128);
    }
}

[Trait("Category", "Property")]
[ReproducibleProperties]
public class DomainMatcherPropertyTests
{
    [Property]
    public Property SubdomainAlwaysMatchesParent()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            DnsGenerators.SubdomainPrefix().ToArbitrary(),
            (domain, prefix) =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { domain };
                var subdomain = $"{prefix}.{domain}";
                return DomainMatcher.Matches(set, subdomain)
                    .Label($"'{subdomain}' should match parent '{domain}'");
            });
    }

    [Property]
    public Property ExactDomainAlwaysMatches()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            domain =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { domain };
                return DomainMatcher.Matches(set, domain)
                    .Label($"'{domain}' should match itself");
            });
    }

    [Property]
    public Property TrailingDotEquivalent()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            domain =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { domain };
                var withDot = domain + ".";
                return DomainMatcher.Matches(set, withDot)
                    .Label($"'{withDot}' should match '{domain}'");
            });
    }

    [Property]
    public Property CaseInsensitiveMatching()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            domain =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { domain };
                return (DomainMatcher.Matches(set, domain.ToUpperInvariant())
                    && DomainMatcher.Matches(set, domain.ToLowerInvariant()))
                    .Label($"Case variants of '{domain}' should match");
            });
    }

    [Property]
    public Property MaxLengthLabel_DomainMatches()
    {
        return Prop.ForAll(
            DnsGenerators.MaxLengthLabel().ToArbitrary(),
            label =>
            {
                var domain = $"{label}.com";
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { domain };
                return DomainMatcher.Matches(set, domain)
                    .Label($"63-char label domain '{domain[..10]}...' should match itself");
            });
    }

    [Property]
    public Property SingleLabel_ExactMatchWorks()
    {
        return Prop.ForAll(
            DnsGenerators.SingleLabel().ToArbitrary(),
            label =>
            {
                // DomainMatcher.Matches checks exact match first via HashSet, so single-label
                // domains in the set should still match when queried exactly.
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { label };
                return DomainMatcher.Matches(set, label)
                    .Label($"Single label '{label}' should match when in set");
            });
    }

    [Property]
    public Property DisjointDomainsNeverMatch()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            DnsGenerators.DomainName().ToArbitrary(),
            (domain1, domain2) =>
            {
                // Only test when domains share no suffix relationship
                if (domain1.EndsWith(domain2, StringComparison.OrdinalIgnoreCase) ||
                    domain2.EndsWith(domain1, StringComparison.OrdinalIgnoreCase))
                    return true.Label("skip -- domains are related");

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { domain1 };
                return (!DomainMatcher.Matches(set, domain2))
                    .Label($"'{domain2}' should NOT match unrelated '{domain1}'");
            });
    }
}

[Trait("Category", "Property")]
[ReproducibleProperties]
public class CidrMatchPropertyTests
{
    [Property]
    public Property NetworkAddressAlwaysMatchesItsCidr()
    {
        return Prop.ForAll(
            DnsGenerators.IPv4Address().ToArbitrary(),
            DnsGenerators.CidrPrefix().ToArbitrary(),
            (ip, prefix) =>
            {
                // The network address itself (masked) should always match
                var ipBytes = ip.GetAddressBytes();
                var masked = new byte[4];
                for (int i = 0; i < 4; i++)
                {
                    var bitsInByte = Math.Min(8, Math.Max(0, prefix - i * 8));
                    var mask = bitsInByte >= 8 ? 0xFF : (byte)(0xFF << (8 - bitsInByte));
                    masked[i] = (byte)(ipBytes[i] & mask);
                }
                var networkAddr = new IPAddress(masked);
                return FilteringService.MatchesCidr(networkAddr, $"{networkAddr}/{prefix}", out _)
                    .Label($"Network address {networkAddr} should match {networkAddr}/{prefix}");
            });
    }

    [Property]
    public Property Cidr32_OnlyMatchesExactIp()
    {
        return Prop.ForAll(
            DnsGenerators.IPv4Address().ToArbitrary(),
            DnsGenerators.IPv4Address().ToArbitrary(),
            (target, other) =>
            {
                var cidr = $"{target}/32";
                var shouldMatch = target.Equals(other);
                var doesMatch = FilteringService.MatchesCidr(other, cidr, out _);
                return (shouldMatch == doesMatch)
                    .Label($"/32 for {target}: {other} match={doesMatch}, expected={shouldMatch}");
            });
    }

    [Property]
    public Property Cidr0_MatchesAllIPv4()
    {
        return Prop.ForAll(
            DnsGenerators.IPv4Address().ToArbitrary(),
            ip =>
            {
                return FilteringService.MatchesCidr(ip, "0.0.0.0/0", out _)
                    .Label($"{ip} should match 0.0.0.0/0");
            });
    }

    [Property]
    public Property IPv6_NetworkAddressAlwaysMatchesItsCidr()
    {
        return Prop.ForAll(
            DnsGenerators.IPv6Address().ToArbitrary(),
            DnsGenerators.CidrPrefixV6().ToArbitrary(),
            (ip, prefix) =>
            {
                var ipBytes = ip.GetAddressBytes();
                var masked = new byte[16];
                for (int i = 0; i < 16; i++)
                {
                    var bitsInByte = Math.Min(8, Math.Max(0, prefix - i * 8));
                    var mask = bitsInByte >= 8 ? 0xFF : (byte)(0xFF << (8 - bitsInByte));
                    masked[i] = (byte)(ipBytes[i] & mask);
                }
                var networkAddr = new IPAddress(masked);
                return FilteringService.MatchesCidr(networkAddr, $"{networkAddr}/{prefix}", out _)
                    .Label($"IPv6 network address {networkAddr} should match {networkAddr}/{prefix}");
            });
    }

    [Property]
    public Property NarrowerPrefix_IsSubsetOfBroader()
    {
        return Prop.ForAll(
            DnsGenerators.IPv4Address().ToArbitrary(),
            DnsGenerators.IPv4Address().ToArbitrary(),
            Gen.Choose(8, 30).ToArbitrary(),
            (network, testIp, narrowPrefix) =>
            {
                // Pick a broader prefix that's 1-8 bits shorter
                var rng = new Random(narrowPrefix ^ testIp.GetHashCode());
                var broadPrefix = narrowPrefix - rng.Next(1, Math.Min(narrowPrefix, 9));
                if (broadPrefix < 0) broadPrefix = 0;

                var narrowCidr = $"{network}/{narrowPrefix}";
                var broadCidr = $"{network}/{broadPrefix}";

                var matchesNarrow = FilteringService.MatchesCidr(testIp, narrowCidr, out _);
                var matchesBroad = FilteringService.MatchesCidr(testIp, broadCidr, out _);

                // If it matches the narrower CIDR, it must also match the broader one
                return (!matchesNarrow || matchesBroad)
                    .Label($"{testIp} matches /{narrowPrefix} but not /{broadPrefix} of {network}");
            });
    }
}

[Trait("Category", "Property")]
[ReproducibleProperties]
public class ProfileCompilerPropertyTests
{
    [Property(MaxTest = 50)]
    public Property BaseProfileBlocks_AreSubsetOfMergedChild()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ListOf().ToArbitrary(),
            DnsGenerators.DomainName().ListOf().ToArbitrary(),
            (baseDomains, childDomains) =>
            {
                var compiler = new ProfileCompiler(new ServiceRegistry());
                var config = new AppConfig
                {
                    BaseProfile = "base",
                    Profiles =
                    {
                        ["base"] = new ProfileConfig
                        {
                            CustomRules = baseDomains.ToList()
                        },
                        ["child"] = new ProfileConfig
                        {
                            CustomRules = childDomains.ToList()
                        }
                    }
                };

                var compiled = compiler.CompileAll(config);
                var baseBlocked = compiled["base"].BlockedDomains;
                var childBlocked = compiled["child"].BlockedDomains;

                // Every domain blocked by base must also be blocked by child
                return baseBlocked.All(d => childBlocked.Contains(d))
                    .Label("Base blocked domains should be subset of child blocked domains");
            });
    }

    [Property(MaxTest = 50)]
    public Property ChildProfileAllows_NotOverriddenByBaseMerge()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            domain =>
            {
                var compiler = new ProfileCompiler(new ServiceRegistry());
                var config = new AppConfig
                {
                    BaseProfile = "base",
                    Profiles =
                    {
                        ["base"] = new ProfileConfig(),
                        ["child"] = new ProfileConfig
                        {
                            AllowList = [domain]
                        }
                    }
                };

                var compiled = compiler.CompileAll(config);
                return compiled["child"].AllowedDomains.Contains(domain)
                    .Label($"Child allow '{domain}' must survive base merge");
            });
    }

    [Property(MaxTest = 50)]
    public Property ChildRewrites_OverrideBaseOnConflict()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            DnsGenerators.DomainName().ToArbitrary(),
            DnsGenerators.DomainName().ToArbitrary(),
            (domain, baseAnswer, childAnswer) =>
            {
                var compiler = new ProfileCompiler(new ServiceRegistry());
                var config = new AppConfig
                {
                    BaseProfile = "base",
                    Profiles =
                    {
                        ["base"] = new ProfileConfig
                        {
                            DnsRewrites = [new DnsRewriteConfig { Domain = domain, Answer = baseAnswer }]
                        },
                        ["child"] = new ProfileConfig
                        {
                            DnsRewrites = [new DnsRewriteConfig { Domain = domain, Answer = childAnswer }]
                        }
                    }
                };

                var compiled = compiler.CompileAll(config);
                var trimmedDomain = domain.TrimEnd('.');
                return (compiled["child"].Rewrites.ContainsKey(trimmedDomain)
                    && compiled["child"].Rewrites[trimmedDomain].Answer == childAnswer)
                    .Label($"Child rewrite for '{domain}' should win over base");
            });
    }
}

[Trait("Category", "Property")]
[ReproducibleProperties]
public class BlockListParserPropertyTests : IDisposable
{
    private readonly string _tempDir;

    public BlockListParserPropertyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fscheck-blocklist-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private HashSet<string> Parse(string content)
    {
        var path = Path.Combine(_tempDir, $"test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return BlockListManager.ParseFile(path);
    }

    [Property]
    public Property PlainDomain_RoundTrips()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            domain =>
            {
                var parsed = Parse(domain + "\n");
                return parsed.Contains(domain)
                    .Label($"Plain domain '{domain}' should be parsed");
            });
    }

    [Property]
    public Property AdblockFormat_RoundTrips()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            domain =>
            {
                var line = $"||{domain}^";
                var parsed = Parse(line + "\n");
                return parsed.Contains(domain)
                    .Label($"Adblock '{line}' should parse to '{domain}'");
            });
    }

    [Property]
    public Property HostsFormat_RoundTrips()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            Gen.Elements("0.0.0.0", "127.0.0.1").ToArbitrary(),
            (domain, prefix) =>
            {
                var line = $"{prefix} {domain}";
                var parsed = Parse(line + "\n");
                return parsed.Contains(domain)
                    .Label($"Hosts line '{line}' should parse to '{domain}'");
            });
    }

    [Property]
    public Property CommentLines_NeverProduceDomains()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            Gen.Elements("#", "!").ToArbitrary(),
            (domain, commentChar) =>
            {
                var line = $"{commentChar} {domain}";
                var parsed = Parse(line + "\n");
                return (parsed.Count == 0)
                    .Label($"Comment '{line}' should produce no domains");
            });
    }

    [Property]
    public Property AllFormats_CaseInsensitiveDedup()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            domain =>
            {
                var content = $"{domain}\n{domain.ToUpperInvariant()}\n{domain.ToLowerInvariant()}\n";
                var parsed = Parse(content);
                return (parsed.Count == 1)
                    .Label($"'{domain}' in 3 cases should dedup to 1");
            });
    }
}

[Trait("Category", "Property")]
[ReproducibleProperties]
public class GetRewritePropertyTests
{
    [Property]
    public Property SubdomainRewrite_AlwaysMatchesParent()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            DnsGenerators.SubdomainPrefix().ToArbitrary(),
            DnsGenerators.DomainName().ToArbitrary(),
            (domain, prefix, answer) =>
            {
                var rewrites = new Dictionary<string, DnsRewriteConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    [domain] = new DnsRewriteConfig { Domain = domain, Answer = answer }
                };
                var subdomain = $"{prefix}.{domain}";
                var result = FilteringService.GetRewrite(rewrites, subdomain);
                return (result is not null && result.Answer == answer)
                    .Label($"Rewrite for '{domain}' should match '{subdomain}'");
            });
    }

    [Property]
    public Property MoreSpecificRewrite_WinsOverParent()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            DnsGenerators.SubdomainPrefix().ToArbitrary(),
            (domain, prefix) =>
            {
                var subdomain = $"{prefix}.{domain}";
                var rewrites = new Dictionary<string, DnsRewriteConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    [domain] = new DnsRewriteConfig { Domain = domain, Answer = "parent" },
                    [subdomain] = new DnsRewriteConfig { Domain = subdomain, Answer = "child" }
                };
                var result = FilteringService.GetRewrite(rewrites, subdomain);
                return (result is not null && result.Answer == "child")
                    .Label($"'{subdomain}' should match child rewrite, not parent '{domain}'");
            });
    }
}
