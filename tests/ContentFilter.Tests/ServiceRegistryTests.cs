using ContentFilter.Models;
using ContentFilter.Services;

namespace ContentFilter.Tests;

[Trait("Category", "Unit")]
public class ServiceRegistryTests
{
    [Fact]
    public void EmbeddedServices_Loaded()
    {
        var registry = new ServiceRegistry();
        // The embedded blocked-services.json has 72 services
        Assert.True(registry.Services.Count > 0);
    }

    [Fact]
    public void FindServiceForDomain_ExactMatch()
    {
        var registry = new ServiceRegistry();
        registry.MergeCustomServices(new Dictionary<string, BlockedServiceDefinition>
        {
            ["testservice"] = new() { Name = "Test", Domains = ["test.example.com"] }
        });

        Assert.Equal("testservice", registry.FindServiceForDomain("test.example.com"));
    }

    [Fact]
    public void FindServiceForDomain_SubdomainMatch()
    {
        var registry = new ServiceRegistry();
        registry.MergeCustomServices(new Dictionary<string, BlockedServiceDefinition>
        {
            ["testservice"] = new() { Name = "Test", Domains = ["example.com"] }
        });

        Assert.Equal("testservice", registry.FindServiceForDomain("www.example.com"));
    }

    [Fact]
    public void FindServiceForDomain_NoMatch()
    {
        var registry = new ServiceRegistry();
        Assert.Null(registry.FindServiceForDomain("definitely-not-a-service.example.test"));
    }

    [Fact]
    public void FindServiceForDomain_TrailingDot()
    {
        var registry = new ServiceRegistry();
        registry.MergeCustomServices(new Dictionary<string, BlockedServiceDefinition>
        {
            ["testservice"] = new() { Name = "Test", Domains = ["example.com"] }
        });

        Assert.Equal("testservice", registry.FindServiceForDomain("example.com."));
    }

    [Fact]
    public void FindServiceForDomain_CaseInsensitive()
    {
        var registry = new ServiceRegistry();
        registry.MergeCustomServices(new Dictionary<string, BlockedServiceDefinition>
        {
            ["testservice"] = new() { Name = "Test", Domains = ["Example.COM"] }
        });

        Assert.Equal("testservice", registry.FindServiceForDomain("example.com"));
    }

    [Fact]
    public void MergeCustomServices_OverridesEmbedded()
    {
        var registry = new ServiceRegistry();
        // YouTube is an embedded service -- override it with custom domains
        var originalDomains = registry.Services.ContainsKey("youtube")
            ? registry.Services["youtube"].Domains.ToList()
            : new List<string>();

        registry.MergeCustomServices(new Dictionary<string, BlockedServiceDefinition>
        {
            ["youtube"] = new() { Name = "Custom YouTube", Domains = ["custom-yt.example.com"] }
        });

        Assert.Equal("Custom YouTube", registry.Services["youtube"].Name);
        Assert.Contains("custom-yt.example.com", registry.Services["youtube"].Domains);
    }

    [Fact]
    public void MergeCustomServices_AddsNew()
    {
        var registry = new ServiceRegistry();
        var initialCount = registry.Services.Count;

        registry.MergeCustomServices(new Dictionary<string, BlockedServiceDefinition>
        {
            ["brand-new"] = new() { Name = "Brand New", Domains = ["brandnew.com"] }
        });

        Assert.Equal(initialCount + 1, registry.Services.Count);
        Assert.True(registry.Services.ContainsKey("brand-new"));
    }

    [Fact]
    public void MergeCustomServices_RebuildsDomainIndex()
    {
        var registry = new ServiceRegistry();

        // Before merge, custom domain should not match
        Assert.Null(registry.FindServiceForDomain("custom.test.com"));

        registry.MergeCustomServices(new Dictionary<string, BlockedServiceDefinition>
        {
            ["custom"] = new() { Name = "Custom", Domains = ["custom.test.com"] }
        });

        Assert.Equal("custom", registry.FindServiceForDomain("custom.test.com"));
    }

    [Fact]
    public void EmbeddedServices_ContainYouTube()
    {
        var registry = new ServiceRegistry();
        Assert.True(registry.Services.ContainsKey("youtube"));
        Assert.Contains("youtube.com", registry.Services["youtube"].Domains);
    }

    [Fact]
    public void ExportToAppFolder_WritesBlockedServicesJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sr-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var registry = new ServiceRegistry();
            registry.ExportToAppFolder(tempDir);

            var path = Path.Combine(tempDir, "blocked-services.json");
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.Contains("youtube", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ExportToAppFolder_WritesDefaultBlocklistsJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sr-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var registry = new ServiceRegistry();
            registry.ExportToAppFolder(tempDir);

            var path = Path.Combine(tempDir, "default-blocklists.json");
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.True(content.Length > 2, "default-blocklists.json should have content");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ExportToAppFolder_OverwritesExistingFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sr-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            // Write dummy file first
            File.WriteAllText(Path.Combine(tempDir, "blocked-services.json"), "dummy");

            var registry = new ServiceRegistry();
            registry.ExportToAppFolder(tempDir);

            var content = File.ReadAllText(Path.Combine(tempDir, "blocked-services.json"));
            Assert.NotEqual("dummy", content);
            Assert.Contains("youtube", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void EmbeddedServices_ContainCrealityCloud()
    {
        var registry = new ServiceRegistry();
        Assert.True(registry.Services.ContainsKey("creality-cloud"));
        Assert.Contains("crealitycloud.com", registry.Services["creality-cloud"].Domains);
    }
}
