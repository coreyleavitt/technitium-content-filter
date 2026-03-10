namespace ParentalControlsApp.Services;

/// <summary>
/// Shared subdomain-walking lookup against a HashSet. Walks up the domain
/// hierarchy checking each level: "a.b.example.com" → "b.example.com" → "example.com".
/// </summary>
public static class DomainMatcher
{
    /// <summary>
    /// Returns true if the domain (or any parent) exists in the set.
    /// </summary>
    public static bool Matches(HashSet<string> domains, string query)
    {
        var span = query.AsSpan().TrimEnd('.');

        while (true)
        {
            if (domains.Contains(span.ToString()))
                return true;

            var dotIndex = span.IndexOf('.');
            if (dotIndex < 0 || dotIndex == span.Length - 1)
                break;

            span = span[(dotIndex + 1)..];
        }

        return false;
    }
}
