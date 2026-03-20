namespace ContentFilter.Services;

/// <summary>
/// Summary of plugin state for health-check / status reporting (#27).
/// </summary>
public sealed class PluginStatus
{
    public int LoadedProfileCount { get; init; }
    public DateTime? LastBlockListRefresh { get; init; }
    public Dictionary<string, BlockListStatus> BlockListStatuses { get; init; } = new();
    public bool IsInitialized { get; init; }
    public int PendingRewriteCount { get; init; }
    public int PendingBlockCount { get; init; }
}
