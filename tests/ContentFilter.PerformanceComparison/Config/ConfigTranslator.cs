using System.Text.Json;

namespace ContentFilter.PerformanceComparison.Config;

/// <summary>
/// Generates equivalent plugin configurations for ContentFilter and Advanced Blocking
/// from a test scenario definition.
/// </summary>
public static class ConfigTranslator
{
    public static string ToContentFilterConfig(TestScenario scenario)
    {
        var config = new
        {
            enableBlocking = true,
            profiles = new Dictionary<string, object>
            {
                ["default"] = new
                {
                    customRules = scenario.BlockedDomains,
                    allowList = scenario.AllowedDomains,
                }
            },
            clients = new[]
            {
                new { name = "all", ids = new[] { "0.0.0.0/0" }, profile = "default" }
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    public static string ToAdvancedBlockingConfig(TestScenario scenario)
    {
        var config = new
        {
            enableBlocking = true,
            networkGroupMap = new Dictionary<string, string>
            {
                ["0.0.0.0/0"] = "default"
            },
            groups = new[]
            {
                new
                {
                    name = "default",
                    enableBlocking = true,
                    blockAsNxDomain = true,
                    blocked = scenario.BlockedDomains,
                    allowed = scenario.AllowedDomains,
                }
            }
        };

        return JsonSerializer.Serialize(config);
    }
}
