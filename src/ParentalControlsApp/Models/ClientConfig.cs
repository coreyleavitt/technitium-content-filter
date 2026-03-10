using System.Text.Json.Serialization;

namespace ParentalControlsApp.Models;

public sealed class ClientConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("ids")]
    public List<string> Ids { get; set; } = new();

    [JsonPropertyName("profile")]
    public string Profile { get; set; } = "";
}
