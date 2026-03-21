using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContentFilter.Models;

public sealed class ProfileConfig
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("blockedServices")]
    public List<string> BlockedServices { get; set; } = new();

    [JsonPropertyName("blockLists")]
    [JsonConverter(typeof(BlockListsConverter))]
    public List<string> BlockLists { get; set; } = new();

    [JsonPropertyName("allowList")]
    public List<string> AllowList { get; set; } = new();

    [JsonPropertyName("customRules")]
    public List<string> CustomRules { get; set; } = new();

    [JsonPropertyName("regexBlockRules")]
    public List<string> RegexBlockRules { get; set; } = new();

    [JsonPropertyName("regexAllowRules")]
    public List<string> RegexAllowRules { get; set; } = new();

    [JsonPropertyName("dnsRewrites")]
    public List<DnsRewriteConfig> DnsRewrites { get; set; } = new();

    [JsonPropertyName("blockingAddresses")]
    public List<string>? BlockingAddresses { get; set; }

    /// <summary>
    /// Schedule keyed by day abbreviation (mon, tue, etc.).
    /// Each day can have one or more time windows.
    /// Accepts both a single object and an array per day for backward compatibility.
    /// </summary>
    [JsonPropertyName("schedule")]
    [JsonConverter(typeof(ScheduleMapConverter))]
    public Dictionary<string, List<ScheduleConfig>>? Schedule { get; set; }
}

/// <summary>
/// Reads blockLists that are either List&lt;BlockListConfig&gt; (old format, extracts URLs)
/// or List&lt;string&gt; (new format). Always writes as List&lt;string&gt;.
/// </summary>
internal sealed class BlockListsConverter : JsonConverter<List<string>>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return new List<string>();

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected array for blockLists.");

        var result = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var url = reader.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                    result.Add(url);
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                var obj = JsonSerializer.Deserialize<BlockListConfig>(ref reader, options);
                if (obj is not null && !string.IsNullOrWhiteSpace(obj.Url))
                    result.Add(obj.Url);
            }
            else
            {
                reader.Skip();
            }
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}

/// <summary>
/// Reads schedule entries that are either a single object or an array per day key.
/// Writes always as an array for consistency.
/// </summary>
internal sealed class ScheduleMapConverter : JsonConverter<Dictionary<string, List<ScheduleConfig>>?>
{
    public override Dictionary<string, List<ScheduleConfig>>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected object for schedule.");

        var result = new Dictionary<string, List<ScheduleConfig>>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var dayKey = reader.GetString()!;
            reader.Read();

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var list = JsonSerializer.Deserialize<List<ScheduleConfig>>(ref reader, options)
                           ?? new List<ScheduleConfig>();
                result[dayKey] = list;
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                var single = JsonSerializer.Deserialize<ScheduleConfig>(ref reader, options)
                             ?? new ScheduleConfig();
                result[dayKey] = new List<ScheduleConfig> { single };
            }
            else
            {
                throw new JsonException($"Unexpected token for schedule day '{dayKey}': {reader.TokenType}");
            }
        }

        return result;
    }

    public override void Write(
        Utf8JsonWriter writer, Dictionary<string, List<ScheduleConfig>>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        // Write single-entry lists as a plain object for backward compatibility with the web UI
        writer.WriteStartObject();
        foreach (var (day, list) in value)
        {
            writer.WritePropertyName(day);
            if (list.Count == 1)
                JsonSerializer.Serialize(writer, list[0], options);
            else
                JsonSerializer.Serialize(writer, list, options);
        }
        writer.WriteEndObject();
    }
}
