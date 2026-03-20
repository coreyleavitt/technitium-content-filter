namespace ContentFilter.PerformanceComparison.Metrics;

/// <summary>
/// Computed performance metrics for a single run of a scenario against one plugin.
/// </summary>
public sealed class ScenarioMetrics
{
    public required string PluginName { get; init; }
    public required string ScenarioName { get; init; }
    public required int TotalQueries { get; init; }
    public required int CorrectResults { get; init; }
    public required double Qps { get; init; }
    public required double MeanLatencyMs { get; init; }
    public required double P50LatencyMs { get; init; }
    public required double P95LatencyMs { get; init; }
    public required double P99LatencyMs { get; init; }
    public required double MinLatencyMs { get; init; }
    public required double MaxLatencyMs { get; init; }
    public required TimeSpan WallClock { get; init; }

    public double CorrectnessPct => TotalQueries > 0 ? 100.0 * CorrectResults / TotalQueries : 0;
}

/// <summary>
/// Aggregated metrics across multiple runs (mean + stddev).
/// </summary>
public sealed class AggregatedMetrics
{
    public required string PluginName { get; init; }
    public required string ScenarioName { get; init; }
    public required int Runs { get; init; }
    public required double MeanQps { get; init; }
    public required double StdDevQps { get; init; }
    public required double MeanP50Ms { get; init; }
    public required double StdDevP50Ms { get; init; }
    public required double MeanP95Ms { get; init; }
    public required double StdDevP95Ms { get; init; }
    public required double MeanP99Ms { get; init; }
    public required double StdDevP99Ms { get; init; }
    public required double MeanCorrectnessPct { get; init; }
    public required int TotalQueries { get; init; }
}
