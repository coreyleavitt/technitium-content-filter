using System.Text.Json.Serialization;

namespace ParentalControlsApp.Models;

public sealed class BlockedServiceDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("domains")]
    public List<string> Domains { get; set; } = new();
}
