using System.Text.Json.Serialization;

namespace ParentalControlsApp.Models;

public sealed class BlockListConfig
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("refreshHours")]
    public int RefreshHours { get; set; } = 24;
}
