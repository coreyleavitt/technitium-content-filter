using ContentFilter.Models;

namespace ContentFilter.Services;

/// <summary>
/// Evaluates whether blocking is currently active based on profile schedule configuration.
///
/// Schedule semantics:
///   - No schedule at all -> blocking always active
///   - Day has no entry -> blocking active (always-on for unscheduled days)
///   - Day has windows -> blocking active only DURING those windows, inactive outside
/// </summary>
internal static class ScheduleEvaluator
{
    /// <summary>
    /// Determines whether blocking is active right now for the given profile.
    /// </summary>
    internal static bool IsBlockingActiveNow(ProfileConfig profile, string timeZoneId, bool scheduleAllDay, DateTime? utcNow = null)
    {
        if (profile.Schedule is null || profile.Schedule.Count == 0)
            return true;

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.Utc;
        }

        var now = TimeZoneInfo.ConvertTimeFromUtc(utcNow ?? DateTime.UtcNow, tz);
        var dayKey = now.DayOfWeek switch
        {
            DayOfWeek.Sunday => "sun",
            DayOfWeek.Monday => "mon",
            DayOfWeek.Tuesday => "tue",
            DayOfWeek.Wednesday => "wed",
            DayOfWeek.Thursday => "thu",
            DayOfWeek.Friday => "fri",
            DayOfWeek.Saturday => "sat",
            _ => ""
        };

        if (!profile.Schedule.TryGetValue(dayKey, out var windows) || windows.Count == 0)
            return true;

        var currentTime = TimeOnly.FromDateTime(now);

        foreach (var window in windows)
        {
            if (scheduleAllDay || window.AllDay)
                return true;

            var start = window.StartTime;
            var end = window.EndTime;
            if (start is null || end is null)
                continue;

            var inWindow = start.Value <= end.Value
                ? currentTime >= start.Value && currentTime <= end.Value
                : currentTime >= start.Value || currentTime <= end.Value;

            if (inWindow)
                return true;
        }

        // Outside all defined windows for this day -> blocking inactive
        return false;
    }
}
