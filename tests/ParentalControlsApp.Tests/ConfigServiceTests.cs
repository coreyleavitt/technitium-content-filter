using System.Text.Json;
using ParentalControlsApp.Models;
using ParentalControlsApp.Services;

namespace ParentalControlsApp.Tests;

[Trait("Category", "Unit")]
public class ConfigServiceTests
{
    private static ConfigService CreateService()
    {
        return new ConfigService(Path.GetTempPath());
    }

    [Fact]
    public void Load_NullString_CreatesDefaultConfig()
    {
        var service = CreateService();
        service.Load(null!);
        Assert.NotNull(service.Config);
        Assert.True(service.Config.EnableBlocking);
        Assert.Empty(service.Config.Profiles);
    }

    [Fact]
    public void Load_EmptyString_CreatesDefaultConfig()
    {
        var service = CreateService();
        service.Load("");
        Assert.NotNull(service.Config);
        Assert.True(service.Config.EnableBlocking);
    }

    [Fact]
    public void Load_WhitespaceString_CreatesDefaultConfig()
    {
        var service = CreateService();
        service.Load("   ");
        Assert.NotNull(service.Config);
    }

    [Fact]
    public void Load_ValidJson_Parses()
    {
        var json = """
        {
            "enableBlocking": false,
            "profiles": {
                "test": {
                    "blockedServices": ["youtube"],
                    "customRules": ["bad.com"]
                }
            },
            "clients": [
                { "name": "Phone", "ids": ["192.168.1.50"], "profile": "test" }
            ],
            "defaultProfile": "test",
            "timeZone": "America/Denver"
        }
        """;

        var service = CreateService();
        service.Load(json);

        Assert.False(service.Config.EnableBlocking);
        Assert.True(service.Config.Profiles.ContainsKey("test"));
        Assert.Contains("youtube", service.Config.Profiles["test"].BlockedServices);
        Assert.Contains("bad.com", service.Config.Profiles["test"].CustomRules);
        Assert.Single(service.Config.Clients);
        Assert.Equal("test", service.Config.DefaultProfile);
        Assert.Equal("America/Denver", service.Config.TimeZone);
    }

    [Fact]
    public void Load_MinimalJson_DefaultsApplied()
    {
        var service = CreateService();
        service.Load("{}");

        Assert.True(service.Config.EnableBlocking);
        Assert.Empty(service.Config.Profiles);
        Assert.Empty(service.Config.Clients);
        Assert.Null(service.Config.DefaultProfile);
        Assert.Equal("UTC", service.Config.TimeZone);
        Assert.True(service.Config.ScheduleAllDay);
    }

    [Fact]
    public void Load_WithBlockLists_ParsesGlobal()
    {
        var json = """
        {
            "blockLists": [
                { "url": "https://example.com/list.txt", "name": "Test", "enabled": true, "refreshHours": 12 }
            ]
        }
        """;

        var service = CreateService();
        service.Load(json);

        Assert.Single(service.Config.BlockLists);
        Assert.Equal("https://example.com/list.txt", service.Config.BlockLists[0].Url);
        Assert.True(service.Config.BlockLists[0].Enabled);
        Assert.Equal(12, service.Config.BlockLists[0].RefreshHours);
    }

    [Fact]
    public void Load_WithBaseProfile()
    {
        var json = """
        {
            "baseProfile": "base",
            "profiles": {
                "base": { "customRules": ["ads.com"] }
            }
        }
        """;

        var service = CreateService();
        service.Load(json);

        Assert.Equal("base", service.Config.BaseProfile);
    }

    [Fact]
    public void Load_WithDnsRewrites()
    {
        var json = """
        {
            "profiles": {
                "test": {
                    "dnsRewrites": [
                        { "domain": "youtube.com", "answer": "restrict.youtube.com" }
                    ]
                }
            }
        }
        """;

        var service = CreateService();
        service.Load(json);

        var rewrites = service.Config.Profiles["test"].DnsRewrites;
        Assert.Single(rewrites);
        Assert.Equal("youtube.com", rewrites[0].Domain);
        Assert.Equal("restrict.youtube.com", rewrites[0].Answer);
    }

    [Fact]
    public void Load_WithSchedule_SingleObject()
    {
        var json = """
        {
            "profiles": {
                "test": {
                    "schedule": {
                        "mon": { "allDay": true, "action": "block" },
                        "tue": { "allDay": false, "start": "09:00", "end": "17:00", "action": "block" }
                    }
                }
            }
        }
        """;

        var service = CreateService();
        service.Load(json);

        var schedule = service.Config.Profiles["test"].Schedule;
        Assert.NotNull(schedule);
        Assert.True(schedule.ContainsKey("mon"));
        Assert.Single(schedule["mon"]);
        Assert.True(schedule["mon"][0].AllDay);
        Assert.False(schedule["tue"][0].AllDay);
        Assert.Equal("09:00", schedule["tue"][0].Start);
    }

    [Fact]
    public void Load_WithSchedule_Array()
    {
        var json = """
        {
            "profiles": {
                "test": {
                    "schedule": {
                        "mon": [
                            { "allDay": false, "start": "08:00", "end": "12:00", "action": "block" },
                            { "allDay": false, "start": "14:00", "end": "18:00", "action": "block" }
                        ]
                    }
                }
            }
        }
        """;

        var service = CreateService();
        service.Load(json);

        var schedule = service.Config.Profiles["test"].Schedule;
        Assert.NotNull(schedule);
        Assert.Equal(2, schedule["mon"].Count);
    }

    [Fact]
    public void Load_OldFormatBlockLists_ExtractsUrls()
    {
        // Old format had full BlockListConfig objects in profile.blockLists
        var json = """
        {
            "profiles": {
                "test": {
                    "blockLists": [
                        { "url": "https://example.com/list.txt", "name": "Test", "enabled": true },
                        "https://other.com/list.txt"
                    ]
                }
            }
        }
        """;

        var service = CreateService();
        service.Load(json);

        var blockLists = service.Config.Profiles["test"].BlockLists;
        Assert.Equal(2, blockLists.Count);
        Assert.Contains("https://example.com/list.txt", blockLists);
        Assert.Contains("https://other.com/list.txt", blockLists);
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var json = """
        {
            "enableBlocking": true,
            "profiles": {
                "test": {
                    "blockedServices": ["youtube"],
                    "customRules": ["bad.com", "@@good.com"],
                    "allowList": ["safe.com"],
                    "dnsRewrites": [
                        { "domain": "yt.com", "answer": "restrict.youtube.com" }
                    ]
                }
            },
            "clients": [
                { "name": "Phone", "ids": ["192.168.1.50"], "profile": "test" }
            ],
            "defaultProfile": "test",
            "baseProfile": null,
            "timeZone": "UTC",
            "scheduleAllDay": true,
            "blockLists": []
        }
        """;

        var service = CreateService();
        service.Load(json);

        var serialized = service.Serialize();
        var service2 = CreateService();
        service2.Load(serialized);

        Assert.True(service2.Config.EnableBlocking);
        Assert.Contains("youtube", service2.Config.Profiles["test"].BlockedServices);
        Assert.Contains("bad.com", service2.Config.Profiles["test"].CustomRules);
        Assert.Contains("@@good.com", service2.Config.Profiles["test"].CustomRules);
        Assert.Single(service2.Config.Profiles["test"].DnsRewrites);
    }

    [Fact]
    public async Task SaveAsync_RoundTripsToFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cfg-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var service = new ConfigService(tempDir);
            service.Load("""
            {
                "enableBlocking": false,
                "profiles": {
                    "kids": {
                        "blockedServices": ["youtube"],
                        "customRules": ["bad.com", "@@good.com"],
                        "allowList": ["safe.com"],
                        "dnsRewrites": [{ "domain": "yt.com", "answer": "restrict.youtube.com" }]
                    }
                },
                "clients": [{ "name": "Phone", "ids": ["192.168.1.50"], "profile": "kids" }],
                "defaultProfile": "kids",
                "baseProfile": "base",
                "timeZone": "America/Denver"
            }
            """);

            await service.SaveAsync();

            // Load from disk in a new instance
            var configPath = Path.Combine(tempDir, "dnsApp.config");
            Assert.True(File.Exists(configPath));

            var service2 = new ConfigService(tempDir);
            service2.Load(File.ReadAllText(configPath));

            Assert.False(service2.Config.EnableBlocking);
            Assert.Equal("kids", service2.Config.DefaultProfile);
            Assert.Equal("base", service2.Config.BaseProfile);
            Assert.Equal("America/Denver", service2.Config.TimeZone);
            Assert.Contains("youtube", service2.Config.Profiles["kids"].BlockedServices);
            Assert.Contains("bad.com", service2.Config.Profiles["kids"].CustomRules);
            Assert.Contains("@@good.com", service2.Config.Profiles["kids"].CustomRules);
            Assert.Contains("safe.com", service2.Config.Profiles["kids"].AllowList);
            Assert.Single(service2.Config.Profiles["kids"].DnsRewrites);
            Assert.Equal("yt.com", service2.Config.Profiles["kids"].DnsRewrites[0].Domain);
            Assert.Single(service2.Config.Clients);
            Assert.Equal("Phone", service2.Config.Clients[0].Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_AtomicWrite_NoPartialFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cfg-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var service = new ConfigService(tempDir);
            service.Load("{}");
            await service.SaveAsync();

            // The temp file should be cleaned up (moved to final path)
            var tmpFile = Path.Combine(tempDir, "dnsApp.config.tmp");
            Assert.False(File.Exists(tmpFile));
            Assert.True(File.Exists(Path.Combine(tempDir, "dnsApp.config")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cfg-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var service = new ConfigService(tempDir);

            // Save first config
            service.Load("""{"enableBlocking": true}""");
            await service.SaveAsync();

            // Save different config
            service.Load("""{"enableBlocking": false}""");
            await service.SaveAsync();

            // Verify second save won
            var service2 = new ConfigService(tempDir);
            service2.Load(File.ReadAllText(Path.Combine(tempDir, "dnsApp.config")));
            Assert.False(service2.Config.EnableBlocking);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_NonExistentDirectory_Throws()
    {
        var service = new ConfigService("/tmp/nonexistent-dir-" + Guid.NewGuid().ToString("N"));
        service.Load("{}");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.SaveAsync());
    }

    [Fact]
    public void Load_CustomServices()
    {
        var json = """
        {
            "customServices": {
                "myservice": {
                    "name": "My Service",
                    "domains": ["myservice.com", "api.myservice.com"]
                }
            }
        }
        """;

        var service = CreateService();
        service.Load(json);

        Assert.True(service.Config.CustomServices.ContainsKey("myservice"));
        Assert.Equal("My Service", service.Config.CustomServices["myservice"].Name);
        Assert.Equal(2, service.Config.CustomServices["myservice"].Domains.Count);
    }
}
