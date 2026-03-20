namespace ContentFilter.Services;

/// <summary>
/// Shared subdomain-walking lookup against a HashSet. Walks up the domain
/// hierarchy checking each level: "a.b.example.com" -> "b.example.com" -> "example.com".
/// </summary>
public static class DomainMatcher
{
    /// <summary>
    /// Returns the matched domain string if the domain (or any parent) exists in the set,
    /// or null if no match. The returned string is the actual entry that matched (may be
    /// a parent domain due to subdomain walking).
    /// </summary>
    public static string? FindMatch(HashSet<string> domains, string query)
    {
        // Trim trailing FQDN dot
        var trimmed = query.EndsWith('.') ? query[..^1] : query;

        // Walk up the domain hierarchy using string slicing (#14)
        var current = trimmed;
        while (true)
        {
            if (domains.Contains(current))
                return current;

            var dotIndex = current.IndexOf('.');
            if (dotIndex < 0 || dotIndex == current.Length - 1)
                break;

            current = current[(dotIndex + 1)..];
        }

        return null;
    }

    /// <summary>
    /// Returns true if the domain (or any parent) exists in the set.
    /// #14: Uses string slicing instead of span.ToString() to reduce allocations.
    /// #21: HashSet uses OrdinalIgnoreCase comparer (set at construction site).
    /// </summary>
    public static bool Matches(HashSet<string> domains, string query)
    {
        return FindMatch(domains, query) is not null;
    }
}
