using System.Text.Json;
using ContentFilter.Models;

namespace ContentFilter.Tests;

[Trait("Category", "Unit")]
public class BlockListsConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static ProfileConfig Parse(string blockListsJson)
    {
        var json = "{\"blockLists\": " + blockListsJson + "}";
        return JsonSerializer.Deserialize<ProfileConfig>(json, Options)!;
    }

    [Fact]
    public void NewFormat_StringArray()
    {
        var profile = Parse("""["https://a.com/list.txt", "https://b.com/list.txt"]""");
        Assert.Equal(2, profile.BlockLists.Count);
        Assert.Contains("https://a.com/list.txt", profile.BlockLists);
    }

    [Fact]
    public void OldFormat_ObjectArray()
    {
        var profile = Parse("""[{"url": "https://old.com/list.txt", "name": "Old", "enabled": true}]""");
        Assert.Single(profile.BlockLists);
        Assert.Contains("https://old.com/list.txt", profile.BlockLists);
    }

    [Fact]
    public void MixedFormat_ObjectsAndStrings()
    {
        var profile = Parse("""[{"url": "https://old.com/list.txt", "name": "Old"}, "https://new.com/list.txt"]""");
        Assert.Equal(2, profile.BlockLists.Count);
        Assert.Contains("https://old.com/list.txt", profile.BlockLists);
        Assert.Contains("https://new.com/list.txt", profile.BlockLists);
    }

    [Fact]
    public void NullValue_ReturnsNull()
    {
        // System.Text.Json handles null before calling the converter's Read method,
        // so the property is set to null despite the converter returning empty list for null tokens.
        var profile = Parse("null");
        Assert.Null(profile.BlockLists);
    }

    [Fact]
    public void EmptyArray_ReturnsEmptyList()
    {
        var profile = Parse("[]");
        Assert.Empty(profile.BlockLists);
    }

    [Fact]
    public void WhitespaceUrls_Filtered()
    {
        var profile = Parse("""["", "  ", "https://valid.com/list.txt"]""");
        Assert.Single(profile.BlockLists);
        Assert.Contains("https://valid.com/list.txt", profile.BlockLists);
    }

    [Fact]
    public void OldFormat_EmptyUrl_Filtered()
    {
        var profile = Parse("""[{"url": "", "name": "Empty"}, {"url": "https://valid.com/list.txt"}]""");
        Assert.Single(profile.BlockLists);
    }

    [Fact]
    public void InvalidTokenType_Throws()
    {
        Assert.Throws<JsonException>(() => Parse("123"));
    }

    [Fact]
    public void Write_AlwaysStringArray()
    {
        var profile = new ProfileConfig { BlockLists = ["https://a.com", "https://b.com"] };
        var json = JsonSerializer.Serialize(profile, Options);
        var parsed = JsonSerializer.Deserialize<ProfileConfig>(json, Options)!;
        Assert.Equal(2, parsed.BlockLists.Count);
        // Verify it round-trips as strings, not objects
        Assert.Contains("\"blockLists\":[\"https://a.com\",\"https://b.com\"]", json.Replace(" ", ""));
    }

    [Fact]
    public void UnexpectedTokenInArray_Skipped()
    {
        // Numbers in the array should be skipped
        var profile = Parse("""["https://valid.com/list.txt", 42, true]""");
        Assert.Single(profile.BlockLists);
    }
}

[Trait("Category", "Unit")]
public class ScheduleMapConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static ProfileConfig Parse(string scheduleJson)
    {
        var json = "{\"schedule\": " + scheduleJson + "}";
        return JsonSerializer.Deserialize<ProfileConfig>(json, Options)!;
    }

    [Fact]
    public void NullSchedule_ReturnsNull()
    {
        var profile = Parse("null");
        Assert.Null(profile.Schedule);
    }

    [Fact]
    public void EmptyObject_ReturnsEmptyDict()
    {
        var profile = Parse("{}");
        Assert.NotNull(profile.Schedule);
        Assert.Empty(profile.Schedule);
    }

    [Fact]
    public void SingleObject_WrappedInList()
    {
        var profile = Parse("""{"mon": {"allDay": true, "action": "block"}}""");
        Assert.NotNull(profile.Schedule);
        Assert.Single(profile.Schedule["mon"]);
        Assert.True(profile.Schedule["mon"][0].AllDay);
    }

    [Fact]
    public void Array_PreservedAsList()
    {
        var json = """
        {"mon": [
            {"allDay": false, "start": "09:00", "end": "12:00"},
            {"allDay": false, "start": "14:00", "end": "18:00"}
        ]}
        """;
        var profile = Parse(json);
        Assert.Equal(2, profile.Schedule!["mon"].Count);
        Assert.Equal("09:00", profile.Schedule["mon"][0].Start);
        Assert.Equal("14:00", profile.Schedule["mon"][1].Start);
    }

    [Fact]
    public void MixedDays_SingleAndArray()
    {
        var json = """
        {
            "mon": {"allDay": true},
            "tue": [{"allDay": false, "start": "09:00", "end": "17:00"}]
        }
        """;
        var profile = Parse(json);
        Assert.Single(profile.Schedule!["mon"]);
        Assert.Single(profile.Schedule["tue"]);
    }

    [Fact]
    public void InvalidTokenType_Throws()
    {
        Assert.Throws<JsonException>(() => Parse("[]"));
    }

    [Fact]
    public void InvalidDayValue_Throws()
    {
        Assert.Throws<JsonException>(() => Parse("""{"mon": "invalid"}"""));
    }

    [Fact]
    public void Write_SingleElementList_WrittenAsObject()
    {
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                ["mon"] = [new ScheduleConfig { AllDay = true }]
            }
        };
        var json = JsonSerializer.Serialize(profile, Options);
        var compact = json.Replace(" ", "").Replace("\n", "").Replace("\r", "");

        // Single-element list should be written as object, not array
        // Look for "mon":{ pattern (not "mon":[)
        Assert.Contains("\"mon\":{", compact);
        Assert.DoesNotContain("\"mon\":[", compact);
    }

    [Fact]
    public void Write_MultiElementList_WrittenAsArray()
    {
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                ["mon"] =
                [
                    new ScheduleConfig { AllDay = false, Start = "09:00", End = "12:00" },
                    new ScheduleConfig { AllDay = false, Start = "14:00", End = "18:00" }
                ]
            }
        };
        var json = JsonSerializer.Serialize(profile, Options);
        var reparsed = JsonSerializer.Deserialize<ProfileConfig>(json, Options)!;
        Assert.Equal(2, reparsed.Schedule!["mon"].Count);
    }

    [Fact]
    public void Write_NullSchedule_WritesNull()
    {
        var profile = new ProfileConfig { Schedule = null };
        var json = JsonSerializer.Serialize(profile, Options);
        Assert.Contains("\"schedule\":null", json.Replace(" ", "").Replace("\n", "").Replace("\r", ""));
    }

    [Fact]
    public void CaseInsensitiveDayKeys()
    {
        var profile = Parse("""{"MON": {"allDay": true}}""");
        // Keys stored as given
        Assert.True(profile.Schedule!.ContainsKey("MON"));
        // OrdinalIgnoreCase dict means lowercase lookup works too
        Assert.True(profile.Schedule.ContainsKey("mon"));
    }

    [Fact]
    public void RoundTrip_PreservesData()
    {
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                ["mon"] = [new ScheduleConfig { AllDay = false, Start = "09:00", End = "17:00" }],
                ["fri"] =
                [
                    new ScheduleConfig { AllDay = false, Start = "08:00", End = "12:00" },
                    new ScheduleConfig { AllDay = false, Start = "15:00", End = "17:00" }
                ]
            }
        };

        var json = JsonSerializer.Serialize(profile, Options);
        var roundTripped = JsonSerializer.Deserialize<ProfileConfig>(json, Options)!;

        Assert.Single(roundTripped.Schedule!["mon"]);
        Assert.Equal("09:00", roundTripped.Schedule["mon"][0].Start);
        Assert.Equal(2, roundTripped.Schedule["fri"].Count);
        Assert.Equal("15:00", roundTripped.Schedule["fri"][1].Start);
    }
}
