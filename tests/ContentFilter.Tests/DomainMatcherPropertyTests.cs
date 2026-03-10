using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using ContentFilter.Services;

namespace ContentFilter.Tests;

/// <summary>
/// Issue #35: Property tests for domain matching edge cases.
/// Uses FsCheck to generate random domain names and subdomains, verifying
/// DomainMatcher correctly identifies matches and non-matches.
/// </summary>
[Trait("Category", "Property")]
[ReproducibleProperties]
public class DomainMatcherEdgeCasePropertyTests
{
    [Property]
    public Property EmptySet_NeverMatches()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            domain =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return (!DomainMatcher.Matches(set, domain))
                    .Label($"Empty set should never match '{domain}'");
            });
    }

    [Property]
    public Property ParentDomain_DoesNotMatchChild()
    {
        // If only "sub.example.com" is in the set, "example.com" should NOT match
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            DnsGenerators.SubdomainPrefix().ToArbitrary(),
            (domain, prefix) =>
            {
                var subdomain = $"{prefix}.{domain}";
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { subdomain };
                return (!DomainMatcher.Matches(set, domain))
                    .Label($"Set contains '{subdomain}' but '{domain}' should not match (child does not match parent)");
            });
    }

    [Property]
    public Property MultipleSubdomains_AllMatchParent()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            DnsGenerators.SubdomainPrefix().ToArbitrary(),
            DnsGenerators.SubdomainPrefix().ToArbitrary(),
            (domain, prefix1, prefix2) =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { domain };
                var sub1 = $"{prefix1}.{domain}";
                var sub2 = $"{prefix2}.{domain}";
                var deep = $"{prefix1}.{prefix2}.{domain}";
                return (DomainMatcher.Matches(set, sub1)
                    && DomainMatcher.Matches(set, sub2)
                    && DomainMatcher.Matches(set, deep))
                    .Label($"All subdomains of '{domain}' should match");
            });
    }

    [Property]
    public Property DomainWithTrailingDot_EquivalentToWithout()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            DnsGenerators.SubdomainPrefix().ToArbitrary(),
            (domain, prefix) =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { domain };
                var subWithDot = $"{prefix}.{domain}.";
                var subWithout = $"{prefix}.{domain}";
                var matchWithDot = DomainMatcher.Matches(set, subWithDot);
                var matchWithout = DomainMatcher.Matches(set, subWithout);
                return (matchWithDot == matchWithout)
                    .Label($"Trailing dot should not affect matching: '{subWithDot}' vs '{subWithout}'");
            });
    }

    [Property]
    public Property UnrelatedDomain_NeverMatches()
    {
        // Generate two completely independent domains that share no suffix
        return Prop.ForAll(
            DnsGenerators.DomainLabel().ToArbitrary(),
            DnsGenerators.DomainLabel().ToArbitrary(),
            (label1, label2) =>
            {
                // Use distinct TLDs to guarantee no suffix overlap
                var domain1 = $"{label1}.testdomain1.org";
                var domain2 = $"{label2}.testdomain2.net";
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { domain1 };
                return (!DomainMatcher.Matches(set, domain2))
                    .Label($"'{domain2}' should not match unrelated '{domain1}'");
            });
    }

    [Property]
    public Property LargeSet_StillFindsMatch()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            Gen.Choose(10, 100).ToArbitrary(),
            (targetDomain, setSize) =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < setSize; i++)
                    set.Add($"filler{i}.example.com");
                set.Add(targetDomain);

                return DomainMatcher.Matches(set, targetDomain)
                    .Label($"Should find '{targetDomain}' in set of {set.Count} domains");
            });
    }

    [Property]
    public Property MixedCase_AlwaysMatches()
    {
        return Prop.ForAll(
            DnsGenerators.DomainName().ToArbitrary(),
            domain =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { domain };

                // Random case mutation
                var upper = domain.ToUpperInvariant();
                var lower = domain.ToLowerInvariant();
                var mixed = new string(domain.Select((c, i) => i % 2 == 0 ? char.ToUpper(c) : char.ToLower(c)).ToArray());

                return (DomainMatcher.Matches(set, upper)
                    && DomainMatcher.Matches(set, lower)
                    && DomainMatcher.Matches(set, mixed))
                    .Label($"All case variants of '{domain}' should match");
            });
    }

    [Property]
    public Property MultipleDotsInDomain_StillMatches()
    {
        return Prop.ForAll(
            Gen.Choose(3, 6).ToArbitrary(),
            labelCount =>
            {
                var labels = Enumerable.Range(0, labelCount).Select(i => $"label{i}").ToArray();
                var domain = string.Join('.', labels);
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { domain };

                return DomainMatcher.Matches(set, domain)
                    .Label($"{labelCount}-label domain should match itself");
            });
    }
}
