namespace ContentFilter.Models;

public enum FilterAction { Allow, Block, Rewrite }
public enum BlockSource { DomainBlocklist, RegexBlocklist }

public sealed class BlockReason
{
    public BlockSource Source { get; init; }
    public string? MatchedDomain { get; init; }
    public string? MatchedRegex { get; init; }
}

public sealed class FilterResult
{
    public FilterAction Action { get; init; }
    public string ProfileName { get; init; } = "";
    public string? ClientId { get; init; }
    public string ClientIp { get; init; } = "";
    public string QuestionDomain { get; init; } = "";
    public BlockReason? BlockReason { get; init; }
    public DnsRewriteConfig? Rewrite { get; init; }
    public string DebugSummary { get; init; } = "";
}
