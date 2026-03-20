namespace ContentFilter.PerformanceComparison;

/// <summary>
/// Wraps both plugin fixtures so they can be started in parallel
/// and shared across all performance comparison tests.
/// </summary>
public sealed class DualFixture : IAsyncLifetime
{
    public ContentFilterFixture ContentFilter { get; } = new();
    public AdvancedBlockingFixture AdvancedBlocking { get; } = new();

    public Task InitializeAsync() => Task.WhenAll(
        ContentFilter.InitializeAsync(),
        AdvancedBlocking.InitializeAsync());

    public Task DisposeAsync() => Task.WhenAll(
        ContentFilter.DisposeAsync(),
        AdvancedBlocking.DisposeAsync());
}

[CollectionDefinition("Performance")]
public class PerformanceCollection : ICollectionFixture<DualFixture>;
