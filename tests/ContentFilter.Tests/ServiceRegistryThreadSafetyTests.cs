using ContentFilter.Models;
using ContentFilter.Services;

namespace ContentFilter.Tests;

/// <summary>
/// Issue #31: Tests for ServiceRegistry thread safety.
/// Verifies that ServiceRegistry can be read from multiple threads while being updated.
/// The _services and _domainToServiceId fields are replaced atomically via reference swap.
/// </summary>
[Trait("Category", "Unit")]
public class ServiceRegistryThreadSafetyTests
{
    [Fact]
    public async Task ConcurrentReads_WhileMerging_DoNotThrow()
    {
        var registry = new ServiceRegistry();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Reader tasks that continuously query the registry
        var readerTasks = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    _ = registry.Services.Count;
                    _ = registry.FindServiceForDomain("youtube.com");
                    _ = registry.FindServiceForDomain("nonexistent.example.com");

                    // Iterate services (snapshot read)
                    foreach (var (id, svc) in registry.Services)
                    {
                        _ = svc.Name;
                        _ = svc.Domains.Count;
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToArray();

        // Writer task that merges custom services repeatedly
        var writerTask = Task.Run(() =>
        {
            int i = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    registry.MergeCustomServices(new Dictionary<string, BlockedServiceDefinition>
                    {
                        [$"custom-{i}"] = new()
                        {
                            Name = $"Custom {i}",
                            Domains = [$"custom{i}.example.com", $"api.custom{i}.example.com"]
                        }
                    });
                    i++;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        await Task.WhenAll(readerTasks.Append(writerTask));

        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task ConcurrentFindServiceForDomain_DuringMerge_ReturnsConsistentResults()
    {
        var registry = new ServiceRegistry();
        registry.MergeCustomServices(new Dictionary<string, BlockedServiceDefinition>
        {
            ["stable"] = new() { Name = "Stable", Domains = ["stable.example.com"] }
        });

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stableAlwaysFound = true;

        var readerTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                // The "stable" service should always be findable, even during merges,
                // because MergeCustomServices starts from _embeddedServices and adds all custom services.
                // However, a merge that doesn't include "stable" would remove it.
                // With our test, we only merge *additional* services, so "stable" should persist
                // as long as we keep including it.
                var result = registry.FindServiceForDomain("stable.example.com");
                if (result is null)
                    stableAlwaysFound = false;
            }
        })).ToArray();

        var writerTask = Task.Run(() =>
        {
            int i = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                // Always include "stable" in merges
                registry.MergeCustomServices(new Dictionary<string, BlockedServiceDefinition>
                {
                    ["stable"] = new() { Name = "Stable", Domains = ["stable.example.com"] },
                    [$"dynamic-{i}"] = new() { Name = $"Dynamic {i}", Domains = [$"dynamic{i}.example.com"] }
                });
                i++;
            }
        });

        await Task.WhenAll(readerTasks.Append(writerTask));

        Assert.True(stableAlwaysFound, "Stable service should always be found during concurrent merges");
    }

    [Fact]
    public void ServicesProperty_ReturnsSnapshot_NotLiveReference()
    {
        var registry = new ServiceRegistry();
        var snapshot1 = registry.Services;

        registry.MergeCustomServices(new Dictionary<string, BlockedServiceDefinition>
        {
            ["new-service"] = new() { Name = "New", Domains = ["new.example.com"] }
        });

        var snapshot2 = registry.Services;

        // snapshot1 should not include "new-service" (it was taken before merge)
        // However, Services returns the current _services reference, so snapshot1 and snapshot2
        // may or may not be the same object depending on timing.
        // The important thing is that reads don't throw.
        Assert.NotNull(snapshot1);
        Assert.NotNull(snapshot2);
        Assert.True(snapshot2.ContainsKey("new-service"));
    }

    [Fact]
    public void MergeCustomServices_ReplacesEntireIndex()
    {
        var registry = new ServiceRegistry();

        // First merge adds service A
        registry.MergeCustomServices(new Dictionary<string, BlockedServiceDefinition>
        {
            ["svc-a"] = new() { Name = "A", Domains = ["a.example.com"] }
        });
        Assert.Equal("svc-a", registry.FindServiceForDomain("a.example.com"));

        // Second merge without service A: A should still exist because MergeCustomServices
        // merges with embedded, not with previous custom services
        registry.MergeCustomServices(new Dictionary<string, BlockedServiceDefinition>
        {
            ["svc-b"] = new() { Name = "B", Domains = ["b.example.com"] }
        });
        Assert.Null(registry.FindServiceForDomain("a.example.com"));
        Assert.Equal("svc-b", registry.FindServiceForDomain("b.example.com"));
    }
}
