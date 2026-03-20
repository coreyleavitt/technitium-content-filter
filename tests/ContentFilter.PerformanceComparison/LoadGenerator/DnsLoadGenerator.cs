using System.Collections.Concurrent;
using System.Diagnostics;
using ContentFilter.PerformanceComparison.Config;

namespace ContentFilter.PerformanceComparison.LoadGenerator;

/// <summary>
/// Drives concurrent DNS queries against a fixture for a fixed duration,
/// collecting per-query timing and correctness results.
/// </summary>
public static class DnsLoadGenerator
{
    public static async Task<(List<QueryResult> Results, TimeSpan WallClock)> RunAsync(
        BaseTechnitiumFixture fixture, TestScenario scenario)
    {
        // Warmup phase
        await fixture.FlushCacheAsync();
        await RunWarmupAsync(fixture, scenario);
        await fixture.FlushCacheAsync();

        // Measurement phase
        var results = new ConcurrentBag<QueryResult>();
        var cts = new CancellationTokenSource(scenario.Duration);
        var wallClock = Stopwatch.StartNew();

        var tasks = new Task[scenario.Concurrency];
        for (int i = 0; i < scenario.Concurrency; i++)
        {
            int taskId = i;
            tasks[i] = Task.Run(async () =>
            {
                var queries = scenario.Queries;
                int index = taskId % queries.Count;

                while (!cts.Token.IsCancellationRequested)
                {
                    var query = queries[index];
                    index = (index + 1) % queries.Count;

                    try
                    {
                        var (rcode, latency) = await fixture.TimedQueryAsync(query.Domain);
                        bool isBlocked = rcode == 3; // NXDOMAIN
                        bool isCorrect = isBlocked == query.ExpectBlocked;
                        results.Add(new QueryResult(query.Domain, rcode, latency, query.ExpectBlocked, isCorrect));
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Count failed queries as incorrect with zero latency
                        results.Add(new QueryResult(query.Domain, 255, TimeSpan.Zero, query.ExpectBlocked, false));
                    }
                }
            });
        }

        await Task.WhenAll(tasks);
        wallClock.Stop();

        return (results.ToList(), wallClock.Elapsed);
    }

    private static async Task RunWarmupAsync(BaseTechnitiumFixture fixture, TestScenario scenario)
    {
        var queries = scenario.Queries;
        int count = Math.Min(scenario.WarmupQueries, queries.Count * 5);

        var tasks = new List<Task>();
        for (int i = 0; i < count; i++)
        {
            var query = queries[i % queries.Count];
            tasks.Add(fixture.QueryAsync(query.Domain));

            // Limit concurrency during warmup
            if (tasks.Count >= scenario.Concurrency)
            {
                await Task.WhenAll(tasks);
                tasks.Clear();
            }
        }

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }
}
