namespace ContentFilter.PerformanceComparison.LoadGenerator;

/// <summary>
/// Result of a single DNS query during a load test run.
/// </summary>
public record QueryResult(
    string Domain,
    byte Rcode,
    TimeSpan Latency,
    bool ExpectedBlocked,
    bool IsCorrect);
