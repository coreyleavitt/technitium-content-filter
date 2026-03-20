using System.Text.RegularExpressions;

namespace ContentFilter.Services;

/// <summary>
/// Compiles regex pattern strings into Regex objects with ReDoS protection (250ms timeout).
/// Invalid patterns are logged and skipped. Empty/blank/comment lines are ignored.
/// </summary>
public static class RegexCompiler
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Compiles a list of pattern strings into an array of Regex objects.
    /// Skips empty, blank, and comment lines (starting with #).
    /// Invalid patterns are logged via the optional callback and skipped.
    /// </summary>
    public static Regex[] Compile(List<string> patterns, Action<string>? log = null)
    {
        var result = new List<Regex>();

        foreach (var pattern in patterns)
        {
            var trimmed = pattern.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            try
            {
                var regex = new Regex(
                    trimmed,
                    RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
                    MatchTimeout);
                result.Add(regex);
            }
            catch (ArgumentException ex)
            {
                log?.Invoke($"Invalid regex pattern '{trimmed}': {ex.Message}");
            }
        }

        return result.ToArray();
    }
}
