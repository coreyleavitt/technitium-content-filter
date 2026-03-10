using BenchmarkDotNet.Attributes;
using ContentFilter.Models;
using ContentFilter.Services;

namespace ContentFilter.Benchmarks;

/// <summary>
/// Benchmarks profile compilation, which runs on every config reload.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class ProfileCompilerBenchmarks
{
    private ProfileCompiler _compiler = null!;
    private AppConfig _smallConfig = null!;   // 2 profiles, 10 rules each
    private AppConfig _mediumConfig = null!;   // 10 profiles, 100 rules each
    private AppConfig _largeConfig = null!;    // 50 profiles, 1000 rules each

    [GlobalSetup]
    public void Setup()
    {
        _compiler = new ProfileCompiler(new ServiceRegistry());

        _smallConfig = BuildConfig(2, 10);
        _mediumConfig = BuildConfig(10, 100);
        _largeConfig = BuildConfig(50, 1000);
    }

    private static AppConfig BuildConfig(int profileCount, int rulesPerProfile)
    {
        var config = new AppConfig { BaseProfile = "base" };
        config.Profiles["base"] = new ProfileConfig
        {
            CustomRules = Enumerable.Range(0, rulesPerProfile / 2).Select(i => $"base{i}.com").ToList()
        };

        for (int p = 0; p < profileCount; p++)
        {
            config.Profiles[$"profile{p}"] = new ProfileConfig
            {
                CustomRules = Enumerable.Range(0, rulesPerProfile).Select(i => $"p{p}-block{i}.com").ToList(),
                AllowList = Enumerable.Range(0, rulesPerProfile / 10).Select(i => $"p{p}-allow{i}.com").ToList(),
                DnsRewrites = Enumerable.Range(0, 5).Select(i => new DnsRewriteConfig
                {
                    Domain = $"p{p}-rw{i}.com",
                    Answer = $"10.0.{p}.{i}"
                }).ToList()
            };
        }

        return config;
    }

    [Benchmark(Baseline = true)]
    public Dictionary<string, CompiledProfile> SmallConfig() => _compiler.CompileAll(_smallConfig);

    [Benchmark]
    public Dictionary<string, CompiledProfile> MediumConfig() => _compiler.CompileAll(_mediumConfig);

    [Benchmark]
    public Dictionary<string, CompiledProfile> LargeConfig() => _compiler.CompileAll(_largeConfig);
}
