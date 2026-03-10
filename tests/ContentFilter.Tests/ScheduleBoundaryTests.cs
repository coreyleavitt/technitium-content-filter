using ContentFilter.Models;
using ContentFilter.Services;

namespace ContentFilter.Tests;

/// <summary>
/// Issue #37: Tests for schedule boundary conditions.
/// Tests midnight crossing, edge times (23:59, 00:00, 00:01), and DST scenarios.
/// </summary>
[Trait("Category", "Unit")]
public class ScheduleBoundaryTests
{
    private static DateTime UtcAt(DayOfWeek day, int hour, int minute = 0, int second = 0)
    {
        var today = DateTime.UtcNow.Date;
        var daysUntil = ((int)day - (int)today.DayOfWeek + 7) % 7;
        if (daysUntil == 0) daysUntil = 7;
        var target = today.AddDays(daysUntil);
        return new DateTime(target.Year, target.Month, target.Day, hour, minute, second, DateTimeKind.Utc);
    }

    private static string DayKey(DayOfWeek day) => day switch
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

    // --- Midnight crossing ---

    [Fact]
    public void MidnightCrossing_At2359_InsideWindow()
    {
        var day = DayOfWeek.Friday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "22:00", End = "02:00" }]
            }
        };
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 23, 59)));
    }

    [Fact]
    public void MidnightCrossing_At0000_InsideWindow()
    {
        var day = DayOfWeek.Friday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "22:00", End = "02:00" }]
            }
        };
        // At exactly midnight, still inside the cross-midnight window
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 0, 0)));
    }

    [Fact]
    public void MidnightCrossing_At0001_InsideWindow()
    {
        var day = DayOfWeek.Friday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "22:00", End = "02:00" }]
            }
        };
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 0, 1)));
    }

    [Fact]
    public void MidnightCrossing_At0200_BoundaryInclusive()
    {
        var day = DayOfWeek.Friday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "22:00", End = "02:00" }]
            }
        };
        // At exactly end time (02:00), boundary is inclusive (<=)
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 2, 0)));
    }

    [Fact]
    public void MidnightCrossing_At0201_OutsideWindow()
    {
        var day = DayOfWeek.Friday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "22:00", End = "02:00" }]
            }
        };
        // One minute past end time
        Assert.False(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 2, 1)));
    }

    // --- Edge times ---

    [Fact]
    public void EdgeTime_2359_InsideFullDayWindow()
    {
        var day = DayOfWeek.Monday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "00:00", End = "23:59" }]
            }
        };
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 23, 59)));
    }

    [Fact]
    public void EdgeTime_235959_EndWithSeconds()
    {
        var day = DayOfWeek.Monday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "00:00", End = "23:59:59" }]
            }
        };
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 23, 59, 59)));
    }

    [Fact]
    public void EdgeTime_0000_Start()
    {
        var day = DayOfWeek.Monday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "00:00", End = "06:00" }]
            }
        };
        // Exactly at start time
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 0, 0)));
    }

    [Fact]
    public void NarrowWindow_OneMinute()
    {
        var day = DayOfWeek.Monday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "12:00", End = "12:01" }]
            }
        };
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 12, 0)));
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 12, 1)));
        Assert.False(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 12, 2)));
        Assert.False(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 11, 59)));
    }

    [Fact]
    public void StartEqualsEnd_SinglePointWindow()
    {
        var day = DayOfWeek.Monday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "12:00", End = "12:00" }]
            }
        };
        // When start == end, the condition start <= end is true, so it checks
        // currentTime >= start && currentTime <= end, which is only true at exactly 12:00
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 12, 0)));
        Assert.False(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 12, 1)));
    }

    // --- Midnight crossing with full day ---

    [Fact]
    public void MidnightCrossing_2200To0600_EntireNight()
    {
        var day = DayOfWeek.Saturday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "22:00", End = "06:00" }]
            }
        };

        // Inside: 22:00, 23:00, 00:00, 01:00, 05:00, 06:00
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 22, 0)));
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 23, 0)));
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 0, 0)));
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 1, 0)));
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 5, 0)));
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 6, 0)));

        // Outside: 07:00, 12:00, 21:00
        Assert.False(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 7, 0)));
        Assert.False(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 12, 0)));
        Assert.False(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 21, 0)));
    }

    // --- DST scenarios ---

    [Fact]
    public void DST_SpringForward_TimeSkipsAhead()
    {
        // US DST spring forward 2026: March 8, 2:00 AM -> 3:00 AM
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                ["sun"] = [new ScheduleConfig { AllDay = false, Start = "01:00", End = "04:00" }]
            }
        };

        // 08:00 UTC on March 8 = 02:00 MST but clocks spring forward to 03:00 MDT
        // TimeZoneInfo.ConvertTimeFromUtc handles the gap: 08:00 UTC = 02:00 MDT
        var utc = new DateTime(2026, 3, 8, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "America/Denver", false, utc));
    }

    [Fact]
    public void DST_FallBack_ClockRepeatsHour()
    {
        // US DST fall back 2026: November 1, 2:00 AM -> 1:00 AM
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                ["sun"] = [new ScheduleConfig { AllDay = false, Start = "00:00", End = "03:00" }]
            }
        };

        // At 08:00 UTC on November 1, Denver time = 01:00 MST (after fallback)
        var utc = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "America/Denver", false, utc));
    }

    [Fact]
    public void DST_SpringForward_MidnightCrossingWindow()
    {
        // A midnight-crossing window during DST spring forward
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                ["sun"] = [new ScheduleConfig { AllDay = false, Start = "23:00", End = "05:00" }]
            }
        };

        // March 8, 2026 (Sunday). At 09:00 UTC = 03:00 MDT (inside window)
        var utc = new DateTime(2026, 3, 8, 9, 0, 0, DateTimeKind.Utc);
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "America/Denver", false, utc));
    }

    // --- Multiple windows with midnight crossing ---

    [Fact]
    public void MultipleWindows_OneSpansMidnight()
    {
        var day = DayOfWeek.Saturday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] =
                [
                    new ScheduleConfig { AllDay = false, Start = "08:00", End = "12:00" },
                    new ScheduleConfig { AllDay = false, Start = "22:00", End = "02:00" }
                ]
            }
        };

        // Inside first window
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 10, 0)));
        // Inside second window (before midnight)
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 23, 0)));
        // Inside second window (after midnight)
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 1, 0)));
        // Between windows
        Assert.False(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 15, 0)));
    }

    // --- Timezone offset boundary ---

    [Fact]
    public void TimezoneOffset_DayBoundary_CorrectDayUsed()
    {
        // At 23:30 UTC on Saturday, in UTC+2 it's already 01:30 Sunday
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                ["sun"] = [new ScheduleConfig { AllDay = false, Start = "00:00", End = "06:00" }]
            }
        };

        // 23:30 UTC Saturday = 01:30 EET Sunday
        var utcSaturday = new DateTime(2026, 1, 10, 23, 30, 0, DateTimeKind.Utc);
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "Europe/Helsinki", false, utcSaturday));
    }

    [Fact]
    public void TimezoneOffset_NegativeOffset_DayBoundary()
    {
        // At 02:00 UTC Sunday, in UTC-8 it's still 18:00 Saturday
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                ["sat"] = [new ScheduleConfig { AllDay = false, Start = "17:00", End = "23:00" }]
            }
        };

        // 02:00 UTC Sunday = 18:00 PST Saturday
        var utcSunday = new DateTime(2026, 1, 11, 2, 0, 0, DateTimeKind.Utc);
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "America/Los_Angeles", false, utcSunday));
    }

    // --- Invalid timezone fallback ---

    [Fact]
    public void InvalidTimezone_FallsBackToUtc_DoesNotThrow()
    {
        var day = DayOfWeek.Wednesday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "10:00", End = "18:00" }]
            }
        };

        // Invalid timezone falls back to UTC
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "Invalid/Timezone", false, UtcAt(day, 14, 0)));
        Assert.False(FilteringService.IsBlockingActiveNow(profile, "Invalid/Timezone", false, UtcAt(day, 20, 0)));
    }
}
