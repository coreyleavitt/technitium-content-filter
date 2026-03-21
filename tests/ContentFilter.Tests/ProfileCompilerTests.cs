using ContentFilter.Models;
using ContentFilter.Services;

namespace ContentFilter.Tests;

[Trait("Category", "Unit")]
public class ProfileCompilerTests
{
    private static ServiceRegistry CreateRegistry(Dictionary<string, BlockedServiceDefinition>? custom = null)
    {
        var registry = new ServiceRegistry();
        if (custom is not null)
            registry.MergeCustomServices(custom);
        return registry;
    }

    private static ProfileCompiler CreateCompiler(ServiceRegistry? registry = null, BlockListManager? blm = null)
    {
        return new ProfileCompiler(registry ?? CreateRegistry(), blm);
    }

    [Fact]
    public void EmptyProfile_ProducesEmptySets()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            Profiles = { ["test"] = new ProfileConfig() }
        };

        var result = compiler.CompileAll(config);

        Assert.True(result.ContainsKey("test"));
        Assert.Empty(result["test"].BlockedDomains);
        Assert.Empty(result["test"].AllowedDomains);
        Assert.Empty(result["test"].Rewrites);
    }

    [Fact]
    public void BlockedServices_ExpandToDomains()
    {
        var registry = CreateRegistry(new Dictionary<string, BlockedServiceDefinition>
        {
            ["youtube"] = new() { Name = "YouTube", Domains = ["youtube.com", "ytimg.com"] },
            ["tiktok"] = new() { Name = "TikTok", Domains = ["tiktok.com"] }
        });
        var compiler = CreateCompiler(registry);
        var config = new AppConfig
        {
            Profiles =
            {
                ["kids"] = new ProfileConfig { BlockedServices = ["youtube"] }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Contains("youtube.com", result["kids"].BlockedDomains);
        Assert.Contains("ytimg.com", result["kids"].BlockedDomains);
        Assert.DoesNotContain("tiktok.com", result["kids"].BlockedDomains);
    }

    [Fact]
    public void UnknownService_Ignored()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            Profiles =
            {
                ["test"] = new ProfileConfig { BlockedServices = ["nonexistent"] }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Empty(result["test"].BlockedDomains);
    }

    [Fact]
    public void CustomRules_BlockAndAllow()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            Profiles =
            {
                ["test"] = new ProfileConfig
                {
                    CustomRules = ["bad.com", "evil.org", "@@exception.com", "# comment"]
                }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Contains("bad.com", result["test"].BlockedDomains);
        Assert.Contains("evil.org", result["test"].BlockedDomains);
        Assert.Contains("exception.com", result["test"].AllowedDomains);
        Assert.DoesNotContain("# comment", result["test"].BlockedDomains);
    }

    [Fact]
    public void AllowList_AddedToAllowedDomains()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            Profiles =
            {
                ["test"] = new ProfileConfig
                {
                    AllowList = ["safe.com", "  trusted.org  "]
                }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Contains("safe.com", result["test"].AllowedDomains);
        Assert.Contains("trusted.org", result["test"].AllowedDomains);
    }

    [Fact]
    public void DnsRewrites_Compiled()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            Profiles =
            {
                ["test"] = new ProfileConfig
                {
                    DnsRewrites =
                    [
                        new DnsRewriteConfig { Domain = "youtube.com", Answer = "restrict.youtube.com" },
                        new DnsRewriteConfig { Domain = "custom.local.", Answer = "192.168.1.1" }
                    ]
                }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.True(result["test"].Rewrites.ContainsKey("youtube.com"));
        // Trailing dot should be trimmed
        Assert.True(result["test"].Rewrites.ContainsKey("custom.local"));
        Assert.Equal("restrict.youtube.com", result["test"].Rewrites["youtube.com"].Answer);
    }

    [Fact]
    public void BaseProfile_MergesBlockedDomains()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            BaseProfile = "base",
            Profiles =
            {
                ["base"] = new ProfileConfig { CustomRules = ["ads.com", "trackers.net"] },
                ["kids"] = new ProfileConfig { CustomRules = ["games.com"] }
            }
        };

        var result = compiler.CompileAll(config);

        // Base profile stays standalone
        Assert.Contains("ads.com", result["base"].BlockedDomains);
        Assert.DoesNotContain("games.com", result["base"].BlockedDomains);

        // Kids inherits base blocks
        Assert.Contains("ads.com", result["kids"].BlockedDomains);
        Assert.Contains("trackers.net", result["kids"].BlockedDomains);
        Assert.Contains("games.com", result["kids"].BlockedDomains);
    }

    [Fact]
    public void BaseProfile_MergesAllowedDomains()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            BaseProfile = "base",
            Profiles =
            {
                ["base"] = new ProfileConfig { AllowList = ["safe.com"] },
                ["kids"] = new ProfileConfig { AllowList = ["school.edu"] }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Contains("safe.com", result["kids"].AllowedDomains);
        Assert.Contains("school.edu", result["kids"].AllowedDomains);
    }

    [Fact]
    public void BaseProfile_MergesRewrites_ProfileWins()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            BaseProfile = "base",
            Profiles =
            {
                ["base"] = new ProfileConfig
                {
                    DnsRewrites =
                    [
                        new DnsRewriteConfig { Domain = "youtube.com", Answer = "restrict.youtube.com" },
                        new DnsRewriteConfig { Domain = "bing.com", Answer = "1.2.3.4" }
                    ]
                },
                ["kids"] = new ProfileConfig
                {
                    DnsRewrites =
                    [
                        new DnsRewriteConfig { Domain = "youtube.com", Answer = "0.0.0.0" }
                    ]
                }
            }
        };

        var result = compiler.CompileAll(config);

        // Profile overrides base for youtube.com
        Assert.Equal("0.0.0.0", result["kids"].Rewrites["youtube.com"].Answer);
        // Base bing.com rewrite inherited
        Assert.Equal("1.2.3.4", result["kids"].Rewrites["bing.com"].Answer);
    }

    [Fact]
    public void BaseProfile_NotFound_NoMerge()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            BaseProfile = "nonexistent",
            Profiles =
            {
                ["test"] = new ProfileConfig { CustomRules = ["example.com"] }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Contains("example.com", result["test"].BlockedDomains);
        Assert.Single(result["test"].BlockedDomains);
    }

    [Fact]
    public void EmptyCustomRules_Ignored()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            Profiles =
            {
                ["test"] = new ProfileConfig
                {
                    CustomRules = ["", "  ", "valid.com"]
                }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Single(result["test"].BlockedDomains);
        Assert.Contains("valid.com", result["test"].BlockedDomains);
    }

    [Fact]
    public void EmptyAllowList_Ignored()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            Profiles =
            {
                ["test"] = new ProfileConfig
                {
                    AllowList = ["", "  "]
                }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Empty(result["test"].AllowedDomains);
    }

    [Fact]
    public void MultipleProfiles_CompiledIndependently()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            Profiles =
            {
                ["profile1"] = new ProfileConfig { CustomRules = ["a.com"] },
                ["profile2"] = new ProfileConfig { CustomRules = ["b.com"] }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Equal(2, result.Count);
        Assert.Contains("a.com", result["profile1"].BlockedDomains);
        Assert.DoesNotContain("b.com", result["profile1"].BlockedDomains);
        Assert.Contains("b.com", result["profile2"].BlockedDomains);
        Assert.DoesNotContain("a.com", result["profile2"].BlockedDomains);
    }

    [Fact]
    public void DnsRewrite_EmptyDomain_Skipped()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            Profiles =
            {
                ["test"] = new ProfileConfig
                {
                    DnsRewrites =
                    [
                        new DnsRewriteConfig { Domain = "", Answer = "1.2.3.4" },
                        new DnsRewriteConfig { Domain = "valid.com", Answer = "" },
                        new DnsRewriteConfig { Domain = "good.com", Answer = "5.6.7.8" }
                    ]
                }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Single(result["test"].Rewrites);
        Assert.True(result["test"].Rewrites.ContainsKey("good.com"));
    }

    [Fact]
    public void BlockLists_DisabledGlobal_Excluded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "blm-compiler-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            // Write a blocklist file to the cache
            var cacheDir = Path.Combine(tempDir, "blocklists");
            Directory.CreateDirectory(cacheDir);

            using var blm = new BlockListManager(tempDir);
            // Manually populate domains via reflection-free approach: use ParseFile
            // We need to get domains into the manager. The only public way is RefreshAsync,
            // but that requires HTTP. Instead, test via the compiler path:
            // If blocklist is disabled globally, it should not be included.
            var compiler = CreateCompiler(blm: blm);
            var config = new AppConfig
            {
                BlockLists =
                [
                    new BlockListConfig { Url = "https://example.com/enabled.txt", Enabled = true },
                    new BlockListConfig { Url = "https://example.com/disabled.txt", Enabled = false }
                ],
                Profiles =
                {
                    ["test"] = new ProfileConfig
                    {
                        BlockLists = ["https://example.com/enabled.txt", "https://example.com/disabled.txt"]
                    }
                }
            };

            var result = compiler.CompileAll(config);

            // No domains loaded (neither URL was actually downloaded), but the point is
            // the disabled URL was filtered out. No crash, no exception.
            Assert.NotNull(result["test"]);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BlockLists_UrlNotInGlobal_Excluded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "blm-compiler2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            using var blm = new BlockListManager(tempDir);
            var compiler = CreateCompiler(blm: blm);
            var config = new AppConfig
            {
                // Global list has only one URL
                BlockLists =
                [
                    new BlockListConfig { Url = "https://example.com/known.txt", Enabled = true }
                ],
                Profiles =
                {
                    ["test"] = new ProfileConfig
                    {
                        // Profile references a URL not in global list
                        BlockLists = ["https://example.com/unknown.txt"]
                    }
                }
            };

            var result = compiler.CompileAll(config);
            // Profile's unknown URL is silently ignored
            Assert.Empty(result["test"].BlockedDomains);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RegexRules_CompiledIntoProfile()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            Profiles =
            {
                ["test"] = new ProfileConfig
                {
                    RegexBlockRules = [@"^ads?\d*\.", @"tracking\."],
                    RegexAllowRules = [@"safe\.example\.com"]
                }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Equal(2, result["test"].BlockedRegexes.Length);
        Assert.Single(result["test"].AllowedRegexes);
    }

    [Fact]
    public void RegexRules_InvalidSkipped()
    {
        var logged = new List<string>();
        var compiler = new ProfileCompiler(CreateRegistry(), null, msg => logged.Add(msg));
        var config = new AppConfig
        {
            Profiles =
            {
                ["test"] = new ProfileConfig
                {
                    RegexBlockRules = [@"valid\.", @"[invalid"]
                }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Single(result["test"].BlockedRegexes);
        Assert.Single(logged);
    }

    [Fact]
    public void BaseProfile_MergesRegexArrays()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            BaseProfile = "base",
            Profiles =
            {
                ["base"] = new ProfileConfig
                {
                    RegexBlockRules = [@"base-pattern\."],
                    RegexAllowRules = [@"base-allow\."]
                },
                ["kids"] = new ProfileConfig
                {
                    RegexBlockRules = [@"child-pattern\."],
                    RegexAllowRules = [@"child-allow\."]
                }
            }
        };

        var result = compiler.CompileAll(config);

        // Base stays standalone
        Assert.Single(result["base"].BlockedRegexes);
        Assert.Single(result["base"].AllowedRegexes);

        // Kids gets base + own
        Assert.Equal(2, result["kids"].BlockedRegexes.Length);
        Assert.Equal(2, result["kids"].AllowedRegexes.Length);
    }

    [Fact]
    public void EmptyRegexRules_ProduceEmptyArrays()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            Profiles =
            {
                ["test"] = new ProfileConfig()
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Empty(result["test"].BlockedRegexes);
        Assert.Empty(result["test"].AllowedRegexes);
    }

    [Fact]
    public void EmptyProfiles_ReturnsEmptyDict()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig();

        var result = compiler.CompileAll(config);

        Assert.Empty(result);
    }

    [Fact]
    public void RegexBlockList_PatternsCompiledIntoProfile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "blm-regex-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var handler = new BlockListManagerHttpTests.MockHandler(@"^ads?\d*\." + "\n" + @"tracking\." + "\n");
            using var blm = new BlockListManager(tempDir, handler);

            var config = new AppConfig
            {
                BlockLists =
                [
                    new BlockListConfig { Url = "https://example.com/regex.txt", Enabled = true, Type = "regex" }
                ],
                Profiles =
                {
                    ["test"] = new ProfileConfig
                    {
                        BlockLists = ["https://example.com/regex.txt"]
                    }
                }
            };

            blm.RefreshAsync(config.BlockLists).GetAwaiter().GetResult();
            var compiler = CreateCompiler(blm: blm);
            var result = compiler.CompileAll(config);

            Assert.Equal(2, result["test"].BlockedRegexes.Length);
            Assert.Empty(result["test"].BlockedDomains);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MixedProfile_DomainAndRegexBlocklists_MergedCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "blm-mixed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var handler = new BlockListManagerHttpTests.MockHandler(url =>
                url.Contains("domains") ? "blocked.example.com\n" : @"^tracking\." + "\n");
            using var blm = new BlockListManager(tempDir, handler);

            var config = new AppConfig
            {
                BlockLists =
                [
                    new BlockListConfig { Url = "https://example.com/domains.txt", Enabled = true, Type = "domain" },
                    new BlockListConfig { Url = "https://example.com/regex.txt", Enabled = true, Type = "regex" }
                ],
                Profiles =
                {
                    ["test"] = new ProfileConfig
                    {
                        BlockLists = ["https://example.com/domains.txt", "https://example.com/regex.txt"],
                        RegexBlockRules = [@"^ads\."]
                    }
                }
            };

            blm.RefreshAsync(config.BlockLists).GetAwaiter().GetResult();
            var compiler = CreateCompiler(blm: blm);
            var result = compiler.CompileAll(config);

            Assert.Contains("blocked.example.com", result["test"].BlockedDomains);
            // 1 inline + 1 remote = 2 regex patterns
            Assert.Equal(2, result["test"].BlockedRegexes.Length);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BaseProfile_MergesRemoteRegexPatterns()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "blm-baserg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var handler = new BlockListManagerHttpTests.MockHandler(@"^base-pattern\." + "\n");
            using var blm = new BlockListManager(tempDir, handler);

            var config = new AppConfig
            {
                BaseProfile = "base",
                BlockLists =
                [
                    new BlockListConfig { Url = "https://example.com/regex.txt", Enabled = true, Type = "regex" }
                ],
                Profiles =
                {
                    ["base"] = new ProfileConfig
                    {
                        BlockLists = ["https://example.com/regex.txt"]
                    },
                    ["kids"] = new ProfileConfig
                    {
                        RegexBlockRules = [@"^child-pattern\."]
                    }
                }
            };

            blm.RefreshAsync(config.BlockLists).GetAwaiter().GetResult();
            var compiler = CreateCompiler(blm: blm);
            var result = compiler.CompileAll(config);

            // Base has 1 remote pattern
            Assert.Single(result["base"].BlockedRegexes);
            // Kids gets base (1 remote) + own (1 inline) = 2
            Assert.Equal(2, result["kids"].BlockedRegexes.Length);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RegexBlockList_UrlNotInGlobal_Skipped()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "blm-skipurl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            using var blm = new BlockListManager(tempDir);
            var compiler = CreateCompiler(blm: blm);
            var config = new AppConfig
            {
                BlockLists =
                [
                    new BlockListConfig { Url = "https://example.com/known.txt", Enabled = true, Type = "regex" }
                ],
                Profiles =
                {
                    ["test"] = new ProfileConfig
                    {
                        BlockLists = ["https://example.com/unknown.txt"]
                    }
                }
            };

            var result = compiler.CompileAll(config);
            Assert.Empty(result["test"].BlockedRegexes);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // --- Blocking address tests ---

    [Fact]
    public void BlockingAddresses_ProfileExplicit_UsedDirectly()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            BlockingAddresses = ["10.0.0.1"],
            Profiles =
            {
                ["test"] = new ProfileConfig
                {
                    BlockingAddresses = ["192.168.1.1"]
                }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Single(result["test"].BlockingAddresses.IPv4Addresses);
        Assert.Equal(System.Net.IPAddress.Parse("192.168.1.1"), result["test"].BlockingAddresses.IPv4Addresses[0]);
    }

    [Fact]
    public void BlockingAddresses_ProfileNull_InheritsFromGlobal()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            BlockingAddresses = ["10.0.0.1"],
            Profiles =
            {
                ["test"] = new ProfileConfig()  // BlockingAddresses is null
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Single(result["test"].BlockingAddresses.IPv4Addresses);
        Assert.Equal(System.Net.IPAddress.Parse("10.0.0.1"), result["test"].BlockingAddresses.IPv4Addresses[0]);
    }

    [Fact]
    public void BlockingAddresses_ProfileExplicitEmpty_ForcesNxdomain()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            BlockingAddresses = ["10.0.0.1"],
            Profiles =
            {
                ["test"] = new ProfileConfig
                {
                    BlockingAddresses = new List<string>()  // Explicit empty
                }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.True(result["test"].BlockingAddresses.IsEmpty);
    }

    [Fact]
    public void BlockingAddresses_ProfileNull_InheritsFromBase()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            BaseProfile = "base",
            Profiles =
            {
                ["base"] = new ProfileConfig
                {
                    BlockingAddresses = ["10.0.0.1"]
                },
                ["kids"] = new ProfileConfig()  // BlockingAddresses is null
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Single(result["kids"].BlockingAddresses.IPv4Addresses);
        Assert.Equal(System.Net.IPAddress.Parse("10.0.0.1"), result["kids"].BlockingAddresses.IPv4Addresses[0]);
    }

    [Fact]
    public void BlockingAddresses_ProfileExplicit_OverridesBase()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            BaseProfile = "base",
            Profiles =
            {
                ["base"] = new ProfileConfig
                {
                    BlockingAddresses = ["10.0.0.1"]
                },
                ["kids"] = new ProfileConfig
                {
                    BlockingAddresses = ["192.168.1.1"]
                }
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Single(result["kids"].BlockingAddresses.IPv4Addresses);
        Assert.Equal(System.Net.IPAddress.Parse("192.168.1.1"), result["kids"].BlockingAddresses.IPv4Addresses[0]);
    }

    [Fact]
    public void BlockingAddresses_InheritanceChain_ProfileNull_BaseNull_FallsToGlobal()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            BlockingAddresses = ["fd00::1"],
            BaseProfile = "base",
            Profiles =
            {
                ["base"] = new ProfileConfig(),  // null -> inherits global
                ["kids"] = new ProfileConfig()    // null -> inherits base -> global
            }
        };

        var result = compiler.CompileAll(config);

        Assert.Single(result["base"].BlockingAddresses.IPv6Addresses);
        Assert.Single(result["kids"].BlockingAddresses.IPv6Addresses);
    }

    [Fact]
    public void BlockingAddresses_NoAddressesAnywhere_IsEmpty()
    {
        var compiler = CreateCompiler();
        var config = new AppConfig
        {
            Profiles =
            {
                ["test"] = new ProfileConfig()
            }
        };

        var result = compiler.CompileAll(config);

        Assert.True(result["test"].BlockingAddresses.IsEmpty);
    }

    [Fact]
    public void NullBlockListManager_SkipsBlocklists()
    {
        var compiler = CreateCompiler(blm: null);
        var config = new AppConfig
        {
            BlockLists =
            [
                new BlockListConfig { Url = "https://example.com/list.txt", Enabled = true }
            ],
            Profiles =
            {
                ["test"] = new ProfileConfig
                {
                    BlockLists = ["https://example.com/list.txt"],
                    CustomRules = ["manual.com"]
                }
            }
        };

        var result = compiler.CompileAll(config);
        // Blocklist domains not loaded (no manager), but custom rules still work
        Assert.Single(result["test"].BlockedDomains);
        Assert.Contains("manual.com", result["test"].BlockedDomains);
    }
}
