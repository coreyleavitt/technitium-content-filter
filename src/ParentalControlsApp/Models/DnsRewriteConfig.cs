using System.Text.Json.Serialization;

namespace ParentalControlsApp.Models;

public sealed class DnsRewriteConfig
{
    [JsonPropertyName("domain")]
    public string Domain { get; set; } = "";

    [JsonPropertyName("answer")]
    public string Answer { get; set; } = "";
}
