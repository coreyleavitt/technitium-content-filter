using System.Text.Json.Serialization;

namespace ContentFilter.Models;

public sealed class AppConfig
{
    [JsonPropertyName("enableBlocking")]
    public bool EnableBlocking { get; set; } = true;

    [JsonPropertyName("profiles")]
    public Dictionary<string, ProfileConfig> Profiles { get; set; } = new();

    [JsonPropertyName("clients")]
    public List<ClientConfig> Clients { get; set; } = new();

    [JsonPropertyName("defaultProfile")]
    public string? DefaultProfile { get; set; }

    [JsonPropertyName("baseProfile")]
    public string? BaseProfile { get; set; }

    [JsonPropertyName("timeZone")]
    public string TimeZone { get; set; } = "UTC";

    /// <summary>
    /// When true (default), schedules block for the entire day on checked days.
    /// When false, each day's schedule uses start/end times.
    /// </summary>
    [JsonPropertyName("scheduleAllDay")]
    public bool ScheduleAllDay { get; set; } = true;

    [JsonPropertyName("customServices")]
    public Dictionary<string, BlockedServiceDefinition> CustomServices { get; set; } = new();

    [JsonPropertyName("blockLists")]
    public List<BlockListConfig> BlockLists { get; set; } = new();

    [JsonPropertyName("allowTxtBlockingReport")]
    public bool AllowTxtBlockingReport { get; set; }
}
