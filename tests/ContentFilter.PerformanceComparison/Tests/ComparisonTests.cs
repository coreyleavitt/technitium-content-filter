using ContentFilter.PerformanceComparison.Config;
using ContentFilter.PerformanceComparison.LoadGenerator;
using ContentFilter.PerformanceComparison.Metrics;
using Xunit.Abstractions;

namespace ContentFilter.PerformanceComparison.Tests;

[Collection("Performance")]
[Trait("Category", "Performance")]
public class ComparisonTests
{
    private readonly DualFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ComparisonTests(DualFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public static TheoryData<TestScenario> Scenarios => new()
    {
        TestScenario.SmallBlocklist(),
        TestScenario.MediumBlocklist(),
        TestScenario.LargeBlocklist(),
        TestScenario.MixedTraffic(),
        TestScenario.SubdomainMatch(),
    };

    [Theory(Timeout = 600_000)] // 10 minutes per scenario
    [MemberData(nameof(Scenarios))]
    public async Task ComparePerformance(TestScenario scenario)
    {
        _output.WriteLine($"=== {scenario.Name} ===");
        _output.WriteLine($"Concurrency: {scenario.Concurrency}, Duration: {scenario.Duration}, Runs: {scenario.Runs}");
        _output.WriteLine($"Blocked domains: {scenario.BlockedDomains.Count:N0}, Queries: {scenario.Queries.Count:N0}");

        // Configure both plugins
        var cfConfig = ConfigTranslator.ToContentFilterConfig(scenario);
        var abConfig = ConfigTranslator.ToAdvancedBlockingConfig(scenario);

        await _fixture.ContentFilter.SetConfigAsync(cfConfig);
        await _fixture.AdvancedBlocking.SetConfigAsync(abConfig);

        var cfRuns = new List<ScenarioMetrics>();
        var abRuns = new List<ScenarioMetrics>();

        for (int run = 0; run < scenario.Runs; run++)
        {
            _output.WriteLine($"\n--- Run {run + 1}/{scenario.Runs} ---");

            // Run ContentFilter
            var (cfResults, cfWall) = await DnsLoadGenerator.RunAsync(_fixture.ContentFilter, scenario);
            var cfMetrics = MetricsCollector.Compute(
                _fixture.ContentFilter.PluginDisplayName, scenario.Name, cfResults, cfWall);
            cfRuns.Add(cfMetrics);
            _output.WriteLine($"ContentFilter: {cfMetrics.TotalQueries} queries, {cfMetrics.Qps:F0} QPS, p50={cfMetrics.P50LatencyMs:F2}ms, p99={cfMetrics.P99LatencyMs:F2}ms, correct={cfMetrics.CorrectnessPct:F1}%");

            // Run Advanced Blocking
            var (abResults, abWall) = await DnsLoadGenerator.RunAsync(_fixture.AdvancedBlocking, scenario);
            var abMetrics = MetricsCollector.Compute(
                _fixture.AdvancedBlocking.PluginDisplayName, scenario.Name, abResults, abWall);
            abRuns.Add(abMetrics);
            _output.WriteLine($"Advanced Blocking: {abMetrics.TotalQueries} queries, {abMetrics.Qps:F0} QPS, p50={abMetrics.P50LatencyMs:F2}ms, p99={abMetrics.P99LatencyMs:F2}ms, correct={abMetrics.CorrectnessPct:F1}%");
        }

        // Aggregate across runs
        var cfAgg = MetricsCollector.Aggregate(
            _fixture.ContentFilter.PluginDisplayName, scenario.Name, cfRuns);
        var abAgg = MetricsCollector.Aggregate(
            _fixture.AdvancedBlocking.PluginDisplayName, scenario.Name, abRuns);

        // Build and write report for this scenario
        var report = new PerformanceReport();
        report.Scenarios.Add(new ScenarioComparison(scenario.Name, cfAgg, abAgg));

        _output.WriteLine("\n" + report.ToMarkdown());

        // Write report files
        var resultsDir = "/results";
        if (Directory.Exists(resultsDir))
        {
            var safeName = scenario.Name.Replace(" ", "_").Replace("(", "").Replace(")", "").Replace(",", "");
            await File.WriteAllTextAsync(Path.Combine(resultsDir, $"{safeName}.json"), report.ToJson());
            await File.WriteAllTextAsync(Path.Combine(resultsDir, $"{safeName}.md"), report.ToMarkdown());
        }

        // Assert correctness -- both plugins should produce correct results
        Assert.True(cfAgg.MeanCorrectnessPct >= 99.0,
            $"ContentFilter correctness too low: {cfAgg.MeanCorrectnessPct:F1}%");
        Assert.True(abAgg.MeanCorrectnessPct >= 99.0,
            $"Advanced Blocking correctness too low: {abAgg.MeanCorrectnessPct:F1}%");
    }
}
