namespace ContentFilter.PerformanceComparison.Config;

/// <summary>
/// Defines a performance test scenario with blocked domains, query patterns, and load parameters.
/// </summary>
public sealed class TestScenario
{
    public required string Name { get; init; }
    public required List<string> BlockedDomains { get; init; }
    public List<string> AllowedDomains { get; init; } = [];
    public required List<TestQuery> Queries { get; init; }
    public int Concurrency { get; init; } = 50;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);
    public int WarmupQueries { get; init; } = 500;
    public int Runs { get; init; } = 3;

    public override string ToString() => Name;

    public static TestScenario SmallBlocklist() => new()
    {
        Name = "SmallBlocklist (1K domains, 30s)",
        BlockedDomains = GenerateDomains(1_000),
        Queries = GenerateQueries(1_000, blockedRatio: 1.0),
        Duration = TimeSpan.FromSeconds(30),
    };

    public static TestScenario MediumBlocklist() => new()
    {
        Name = "MediumBlocklist (100K domains, 30s)",
        BlockedDomains = GenerateDomains(100_000),
        Queries = GenerateQueries(100_000, blockedRatio: 1.0),
        Duration = TimeSpan.FromSeconds(30),
    };

    public static TestScenario LargeBlocklist() => new()
    {
        Name = "LargeBlocklist (500K domains, 60s)",
        BlockedDomains = GenerateDomains(500_000),
        Queries = GenerateQueries(500_000, blockedRatio: 1.0),
        Duration = TimeSpan.FromSeconds(60),
    };

    public static TestScenario MixedTraffic() => new()
    {
        Name = "MixedTraffic (100K blocked, 50/50 mix, 30s)",
        BlockedDomains = GenerateDomains(100_000),
        Queries = GenerateMixedQueries(100_000),
        Duration = TimeSpan.FromSeconds(30),
    };

    public static TestScenario SubdomainMatch() => new()
    {
        Name = "SubdomainMatch (100K blocked, subdomain queries, 30s)",
        BlockedDomains = GenerateDomains(100_000),
        Queries = GenerateSubdomainQueries(100_000),
        Duration = TimeSpan.FromSeconds(30),
    };

    private static List<string> GenerateDomains(int count)
    {
        var domains = new List<string>(count);
        for (int i = 0; i < count; i++)
            domains.Add($"domain{i}.example.com");
        return domains;
    }

    private static List<TestQuery> GenerateQueries(int domainCount, double blockedRatio)
    {
        var queries = new List<TestQuery>();
        int blockedCount = (int)(1000 * blockedRatio);
        for (int i = 0; i < blockedCount; i++)
            queries.Add(new TestQuery($"domain{i % domainCount}.example.com", ExpectBlocked: true));
        return queries;
    }

    private static List<TestQuery> GenerateMixedQueries(int domainCount)
    {
        var queries = new List<TestQuery>();
        // 500 blocked queries
        for (int i = 0; i < 500; i++)
            queries.Add(new TestQuery($"domain{i % domainCount}.example.com", ExpectBlocked: true));
        // 500 allowed queries (domains not in blocklist)
        for (int i = 0; i < 500; i++)
            queries.Add(new TestQuery($"allowed{i}.notblocked.com", ExpectBlocked: false));
        return queries;
    }

    private static List<TestQuery> GenerateSubdomainQueries(int domainCount)
    {
        var queries = new List<TestQuery>();
        for (int i = 0; i < 1000; i++)
            queries.Add(new TestQuery($"sub.deep.child.domain{i % domainCount}.example.com", ExpectBlocked: true));
        return queries;
    }
}

public record TestQuery(string Domain, bool ExpectBlocked);
