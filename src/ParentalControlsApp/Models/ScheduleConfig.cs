using System.Text.Json.Serialization;

namespace ParentalControlsApp.Models;

/// <summary>
/// Defines a time window during which blocking is active for a specific day.
/// If a day has schedule entries, blocking is active only during the defined
/// windows and inactive outside them. Days without entries are always blocked.
/// </summary>
public sealed class ScheduleConfig
{
    private TimeOnly? _startParsed;
    private TimeOnly? _endParsed;

    /// <summary>
    /// When true, the window covers the entire day regardless of start/end values.
    /// </summary>
    [JsonPropertyName("allDay")]
    public bool AllDay { get; set; } = true;

    [JsonPropertyName("start")]
    public string Start { get; set; } = "00:00";

    [JsonPropertyName("end")]
    public string End { get; set; } = "23:59:59";

    /// <summary>
    /// Parsed Start time. Cached after first access to avoid per-query parsing.
    /// Returns null if the string is not a valid time.
    /// </summary>
    [JsonIgnore]
    public TimeOnly? StartTime => _startParsed ??= TimeOnly.TryParse(Start, out var t) ? t : null;

    /// <summary>
    /// Parsed End time. Cached after first access to avoid per-query parsing.
    /// Returns null if the string is not a valid time.
    /// </summary>
    [JsonIgnore]
    public TimeOnly? EndTime => _endParsed ??= TimeOnly.TryParse(End, out var t) ? t : null;
}
