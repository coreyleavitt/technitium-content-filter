using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContentFilter.PerformanceComparison.Metrics;

/// <summary>
/// Comparison of aggregated metrics for both plugins across one scenario.
/// </summary>
public record ScenarioComparison(
    string ScenarioName,
    AggregatedMetrics ContentFilter,
    AggregatedMetrics AdvancedBlocking);

/// <summary>
/// Full performance comparison report with JSON and markdown output.
/// </summary>
public sealed class PerformanceReport
{
    public List<ScenarioComparison> Scenarios { get; } = [];

    public string ToJson()
    {
        return JsonSerializer.Serialize(Scenarios, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        });
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Performance Comparison: ContentFilter vs Advanced Blocking");
        sb.AppendLine();

        foreach (var scenario in Scenarios)
        {
            sb.AppendLine($"## {scenario.ScenarioName}");
            sb.AppendLine();
            sb.AppendLine("| Metric | ContentFilter | Advanced Blocking |");
            sb.AppendLine("|--------|--------------|-------------------|");
            sb.AppendLine($"| QPS (mean +/- stddev) | {scenario.ContentFilter.MeanQps:F0} +/- {scenario.ContentFilter.StdDevQps:F0} | {scenario.AdvancedBlocking.MeanQps:F0} +/- {scenario.AdvancedBlocking.StdDevQps:F0} |");
            sb.AppendLine($"| p50 latency (ms) | {scenario.ContentFilter.MeanP50Ms:F2} +/- {scenario.ContentFilter.StdDevP50Ms:F2} | {scenario.AdvancedBlocking.MeanP50Ms:F2} +/- {scenario.AdvancedBlocking.StdDevP50Ms:F2} |");
            sb.AppendLine($"| p95 latency (ms) | {scenario.ContentFilter.MeanP95Ms:F2} +/- {scenario.ContentFilter.StdDevP95Ms:F2} | {scenario.AdvancedBlocking.MeanP95Ms:F2} +/- {scenario.AdvancedBlocking.StdDevP95Ms:F2} |");
            sb.AppendLine($"| p99 latency (ms) | {scenario.ContentFilter.MeanP99Ms:F2} +/- {scenario.ContentFilter.StdDevP99Ms:F2} | {scenario.AdvancedBlocking.MeanP99Ms:F2} +/- {scenario.AdvancedBlocking.StdDevP99Ms:F2} |");
            sb.AppendLine($"| Correctness | {scenario.ContentFilter.MeanCorrectnessPct:F1}% | {scenario.AdvancedBlocking.MeanCorrectnessPct:F1}% |");
            sb.AppendLine($"| Total queries ({scenario.ContentFilter.Runs} runs) | {scenario.ContentFilter.TotalQueries:N0} | {scenario.AdvancedBlocking.TotalQueries:N0} |");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
