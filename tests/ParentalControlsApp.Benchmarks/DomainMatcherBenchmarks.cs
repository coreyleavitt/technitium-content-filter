using BenchmarkDotNet.Attributes;
using ParentalControlsApp.Services;

namespace ParentalControlsApp.Benchmarks;

/// <summary>
/// Benchmarks the DNS hot path: DomainMatcher.Matches with varying set sizes.
/// This is the most performance-critical code -- called on every DNS query.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class DomainMatcherBenchmarks
{
    private HashSet<string> _smallSet = null!;    // 100 domains
    private HashSet<string> _mediumSet = null!;   // 10,000 domains
    private HashSet<string> _largeSet = null!;    // 100,000 domains

    [GlobalSetup]
    public void Setup()
    {
        _smallSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _mediumSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _largeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < 100_000; i++)
        {
            var domain = $"domain{i}.example.com";
            if (i < 100) _smallSet.Add(domain);
            if (i < 10_000) _mediumSet.Add(domain);
            _largeSet.Add(domain);
        }
    }

    [Benchmark(Baseline = true)]
    public bool SmallSet_ExactMatch() => DomainMatcher.Matches(_smallSet, "domain50.example.com");

    [Benchmark]
    public bool MediumSet_ExactMatch() => DomainMatcher.Matches(_mediumSet, "domain5000.example.com");

    [Benchmark]
    public bool LargeSet_ExactMatch() => DomainMatcher.Matches(_largeSet, "domain50000.example.com");

    [Benchmark]
    public bool LargeSet_SubdomainMatch() => DomainMatcher.Matches(_largeSet, "www.domain50000.example.com");

    [Benchmark]
    public bool LargeSet_DeepSubdomain() => DomainMatcher.Matches(_largeSet, "a.b.c.d.e.domain50000.example.com");

    [Benchmark]
    public bool LargeSet_NoMatch() => DomainMatcher.Matches(_largeSet, "notblocked.test.com");

    [Benchmark]
    public bool EmptySet() => DomainMatcher.Matches(new HashSet<string>(), "example.com");

    [Benchmark]
    public bool SingleLabel() => DomainMatcher.Matches(_largeSet, "notadomain");
}
