using System.Text.RegularExpressions;
using ContentFilter.Services;

namespace ContentFilter.Tests;

[Trait("Category", "Unit")]
public class RegexCompilerTests
{
    [Fact]
    public void ValidPatterns_CompileSuccessfully()
    {
        var patterns = new List<string> { @"^ads?\d*\.", @"tracking\.example\.com$" };

        var result = RegexCompiler.Compile(patterns);

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public void InvalidPattern_SkippedWithLog()
    {
        var patterns = new List<string> { @"valid\.com", @"[invalid", @"also-valid\.net" };
        var logged = new List<string>();

        var result = RegexCompiler.Compile(patterns, msg => logged.Add(msg));

        Assert.Equal(2, result.Length);
        Assert.Single(logged);
        Assert.Contains("[invalid", logged[0]);
    }

    [Fact]
    public void EmptyAndCommentLines_Skipped()
    {
        var patterns = new List<string> { "", "  ", "# comment", "valid\\.com" };

        var result = RegexCompiler.Compile(patterns);

        Assert.Single(result);
    }

    [Fact]
    public void CaseInsensitive_ByDefault()
    {
        var patterns = new List<string> { @"example\.com" };

        var result = RegexCompiler.Compile(patterns);

        Assert.True(result[0].IsMatch("EXAMPLE.COM"));
        Assert.True(result[0].IsMatch("example.com"));
    }

    [Fact]
    public void Timeout_SetTo250Ms()
    {
        var patterns = new List<string> { @"test" };

        var result = RegexCompiler.Compile(patterns);

        Assert.Equal(TimeSpan.FromMilliseconds(250), result[0].MatchTimeout);
    }

    [Fact]
    public void CatastrophicBacktracking_TimesOut()
    {
        // Pattern known to cause catastrophic backtracking
        var patterns = new List<string> { @"^(a+)+$" };
        var result = RegexCompiler.Compile(patterns);

        // Input designed to trigger exponential backtracking
        var evilInput = new string('a', 30) + "!";

        Assert.Throws<RegexMatchTimeoutException>(() => result[0].IsMatch(evilInput));
    }

    [Fact]
    public void EmptyList_ReturnsEmptyArray()
    {
        var result = RegexCompiler.Compile(new List<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void NullLog_DoesNotThrow()
    {
        var patterns = new List<string> { @"[invalid" };

        var result = RegexCompiler.Compile(patterns, null);

        Assert.Empty(result);
    }
}
