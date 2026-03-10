using System.Text.Json.Serialization;

namespace ContentFilter.Models;

/// <summary>
/// Defines a time window during which blocking is active for a specific day.
/// If a day has schedule entries, blocking is active only during the defined
/// windows and inactive outside them. Days without entries are always blocked.
/// </summary>
public sealed class ScheduleConfig
{
    // #17: Use Lazy<T> for thread-safe lazy initialization of parsed times
    private Lazy<TimeOnly?>? _startLazy;
    private Lazy<TimeOnly?>? _endLazy;

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
    /// #17: Thread-safe via Lazy&lt;T&gt;.
    /// Returns null if the string is not a valid time.
    /// </summary>
    [JsonIgnore]
    public TimeOnly? StartTime
    {
        get
        {
            // Initialize Lazy on first access (captures current Start value)
            _startLazy ??= new Lazy<TimeOnly?>(() => TimeOnly.TryParse(Start, out var t) ? t : null);
            return _startLazy.Value;
        }
    }

    /// <summary>
    /// Parsed End time. Cached after first access to avoid per-query parsing.
    /// #17: Thread-safe via Lazy&lt;T&gt;.
    /// Returns null if the string is not a valid time.
    /// </summary>
    [JsonIgnore]
    public TimeOnly? EndTime
    {
        get
        {
            _endLazy ??= new Lazy<TimeOnly?>(() => TimeOnly.TryParse(End, out var t) ? t : null);
            return _endLazy.Value;
        }
    }
}
