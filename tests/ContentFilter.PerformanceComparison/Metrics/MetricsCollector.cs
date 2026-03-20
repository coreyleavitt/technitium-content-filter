using ContentFilter.PerformanceComparison.LoadGenerator;

namespace ContentFilter.PerformanceComparison.Metrics;

/// <summary>
/// Computes performance metrics from raw query results and aggregates across runs.
/// </summary>
public static class MetricsCollector
{
    public static ScenarioMetrics Compute(
        string pluginName, string scenarioName, List<QueryResult> results, TimeSpan wallClock)
    {
        if (results.Count == 0)
        {
            return new ScenarioMetrics
            {
                PluginName = pluginName,
                ScenarioName = scenarioName,
                TotalQueries = 0,
                CorrectResults = 0,
                Qps = 0,
                MeanLatencyMs = 0,
                P50LatencyMs = 0,
                P95LatencyMs = 0,
                P99LatencyMs = 0,
                MinLatencyMs = 0,
                MaxLatencyMs = 0,
                WallClock = wallClock,
            };
        }

        var latencies = results.Select(r => r.Latency.TotalMilliseconds).OrderBy(x => x).ToArray();
        int correct = results.Count(r => r.IsCorrect);

        return new ScenarioMetrics
        {
            PluginName = pluginName,
            ScenarioName = scenarioName,
            TotalQueries = results.Count,
            CorrectResults = correct,
            Qps = results.Count / wallClock.TotalSeconds,
            MeanLatencyMs = latencies.Average(),
            P50LatencyMs = Percentile(latencies, 50),
            P95LatencyMs = Percentile(latencies, 95),
            P99LatencyMs = Percentile(latencies, 99),
            MinLatencyMs = latencies[0],
            MaxLatencyMs = latencies[^1],
            WallClock = wallClock,
        };
    }

    public static AggregatedMetrics Aggregate(string pluginName, string scenarioName, List<ScenarioMetrics> runs)
    {
        var qpsValues = runs.Select(r => r.Qps).ToArray();
        var p50Values = runs.Select(r => r.P50LatencyMs).ToArray();
        var p95Values = runs.Select(r => r.P95LatencyMs).ToArray();
        var p99Values = runs.Select(r => r.P99LatencyMs).ToArray();
        var correctValues = runs.Select(r => r.CorrectnessPct).ToArray();

        return new AggregatedMetrics
        {
            PluginName = pluginName,
            ScenarioName = scenarioName,
            Runs = runs.Count,
            MeanQps = qpsValues.Average(),
            StdDevQps = StdDev(qpsValues),
            MeanP50Ms = p50Values.Average(),
            StdDevP50Ms = StdDev(p50Values),
            MeanP95Ms = p95Values.Average(),
            StdDevP95Ms = StdDev(p95Values),
            MeanP99Ms = p99Values.Average(),
            StdDevP99Ms = StdDev(p99Values),
            MeanCorrectnessPct = correctValues.Average(),
            TotalQueries = runs.Sum(r => r.TotalQueries),
        };
    }

    private static double Percentile(double[] sorted, int percentile)
    {
        double index = (percentile / 100.0) * (sorted.Length - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        if (lower == upper) return sorted[lower];
        double frac = index - lower;
        return sorted[lower] * (1 - frac) + sorted[upper] * frac;
    }

    private static double StdDev(double[] values)
    {
        if (values.Length <= 1) return 0;
        double mean = values.Average();
        double sumSquares = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSquares / (values.Length - 1));
    }
}
