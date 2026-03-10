using ContentFilter.Models;
using ContentFilter.Services;

namespace ContentFilter.Tests;

[Trait("Category", "Unit")]
public class ScheduleConfigTests
{
    [Theory]
    [InlineData("09:00", 9, 0)]
    [InlineData("17:30", 17, 30)]
    [InlineData("00:00", 0, 0)]
    [InlineData("23:59:59", 23, 59)]
    public void StartTime_ValidParsed(string input, int expectedHour, int expectedMinute)
    {
        var schedule = new ScheduleConfig { Start = input };
        Assert.NotNull(schedule.StartTime);
        Assert.Equal(expectedHour, schedule.StartTime.Value.Hour);
        Assert.Equal(expectedMinute, schedule.StartTime.Value.Minute);
    }

    [Theory]
    [InlineData("not-a-time")]
    [InlineData("")]
    [InlineData("25:00")]
    public void StartTime_Invalid_ReturnsNull(string input)
    {
        var schedule = new ScheduleConfig { Start = input };
        Assert.Null(schedule.StartTime);
    }

    [Fact]
    public void DefaultValues()
    {
        var schedule = new ScheduleConfig();
        Assert.True(schedule.AllDay);
        Assert.Equal("00:00", schedule.Start);
        Assert.Equal("23:59:59", schedule.End);
    }

    [Fact]
    public void StartTime_CachedOnSecondAccess()
    {
        var schedule = new ScheduleConfig { Start = "14:30" };
        var first = schedule.StartTime;
        var second = schedule.StartTime;
        Assert.Equal(first, second);
    }
}

[Trait("Category", "Unit")]
public class IsBlockingActiveNowTests
{
    // Helper: create a DateTime at a specific UTC time on a given day of week.
    // Uses dynamic calculation from the current date to avoid hardcoded dates that expire.
    private static DateTime UtcAt(DayOfWeek day, int hour, int minute = 0)
    {
        var today = DateTime.UtcNow.Date;
        var daysUntil = ((int)day - (int)today.DayOfWeek + 7) % 7;
        if (daysUntil == 0) daysUntil = 7; // Always pick a future date to avoid edge cases at midnight
        var target = today.AddDays(daysUntil);
        return new DateTime(target.Year, target.Month, target.Day, hour, minute, 0, DateTimeKind.Utc);
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

    [Fact]
    public void NoSchedule_BlockingActive()
    {
        var profile = new ProfileConfig();
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", true));
    }

    [Fact]
    public void EmptySchedule_BlockingActive()
    {
        var profile = new ProfileConfig { Schedule = new() };
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", true));
    }

    [Fact]
    public void AllDay_BlockingActive()
    {
        var day = DayOfWeek.Wednesday;
        var profile = new ProfileConfig
        {
            Schedule = new() { [DayKey(day)] = [new ScheduleConfig { AllDay = true }] }
        };
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 12)));
    }

    [Fact]
    public void DayNotInSchedule_BlockingActive()
    {
        var profile = new ProfileConfig
        {
            Schedule = new() { ["tue"] = [new ScheduleConfig { AllDay = true }] }
        };
        // Query on Wednesday -- no schedule entry for that day
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(DayOfWeek.Wednesday, 12)));
    }

    [Fact]
    public void TimeWindow_InsideWindow_BlockingActive()
    {
        var day = DayOfWeek.Monday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "09:00", End = "17:00" }]
            }
        };
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 12)));
    }

    [Fact]
    public void TimeWindow_OutsideWindow_BlockingInactive()
    {
        var day = DayOfWeek.Monday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "09:00", End = "17:00" }]
            }
        };
        // At 20:00, outside the 09:00-17:00 window -> blocking inactive
        Assert.False(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 20)));
    }

    [Fact]
    public void TimeWindow_CrossMidnight_InsideLateNight()
    {
        var day = DayOfWeek.Friday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "22:00", End = "06:00" }]
            }
        };
        // At 23:00, inside the cross-midnight window
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 23)));
    }

    [Fact]
    public void TimeWindow_CrossMidnight_InsideEarlyMorning()
    {
        var day = DayOfWeek.Friday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "22:00", End = "06:00" }]
            }
        };
        // At 03:00, inside the cross-midnight window (currentTime <= end)
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 3)));
    }

    [Fact]
    public void TimeWindow_CrossMidnight_OutsideWindow()
    {
        var day = DayOfWeek.Friday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "22:00", End = "06:00" }]
            }
        };
        // At 12:00, outside the cross-midnight window
        Assert.False(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 12)));
    }

    [Fact]
    public void ScheduleAllDay_OverridesWindowTimes()
    {
        var day = DayOfWeek.Monday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "00:00", End = "00:01" }]
            }
        };
        // scheduleAllDay=true overrides the narrow window
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", true, UtcAt(day, 15)));
    }

    [Fact]
    public void InvalidTimeZone_FallsBackToUtc()
    {
        var day = DayOfWeek.Wednesday;
        var profile = new ProfileConfig
        {
            Schedule = new() { [DayKey(day)] = [new ScheduleConfig { AllDay = true }] }
        };
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "Fake/TimeZone", false, UtcAt(day, 12)));
    }

    [Fact]
    public void InvalidStartEnd_WindowSkipped_BlockingInactive()
    {
        var day = DayOfWeek.Monday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "invalid", End = "invalid" }]
            }
        };
        // Window is skipped (start/end null), no other windows -> blocking inactive
        Assert.False(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 12)));
    }

    [Fact]
    public void CaseInsensitiveDayKeys()
    {
        var profile = new ProfileConfig
        {
            Schedule = new() { ["MON"] = [new ScheduleConfig { AllDay = true }] }
        };
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(DayOfWeek.Monday, 12)));
    }

    [Fact]
    public void TimeZone_OffsetApplied()
    {
        // Use a timezone that's UTC-7 (America/Denver in winter)
        // If it's 03:00 UTC, it's 20:00 the previous day in Denver
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                // Schedule is for Sunday in Denver time
                ["sun"] = [new ScheduleConfig { AllDay = false, Start = "19:00", End = "21:00" }]
            }
        };
        // 03:00 UTC Monday = 20:00 MST Sunday (inside window)
        var utcMonday3am = new DateTime(2026, 1, 5, 3, 0, 0, DateTimeKind.Utc); // Monday
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "America/Denver", false, utcMonday3am));
    }

    [Fact]
    public void MultipleWindows_AnyMatchActivatesBlocking()
    {
        var day = DayOfWeek.Monday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] =
                [
                    new ScheduleConfig { AllDay = false, Start = "08:00", End = "12:00" },
                    new ScheduleConfig { AllDay = false, Start = "14:00", End = "18:00" }
                ]
            }
        };
        // At 10:00, inside first window
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 10)));
        // At 15:00, inside second window
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 15)));
        // At 13:00, between windows -> inactive
        Assert.False(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 13)));
    }

    [Fact]
    public void MultipleWindows_OutsideAll_BlockingInactive()
    {
        var day = DayOfWeek.Monday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] =
                [
                    new ScheduleConfig { AllDay = false, Start = "08:00", End = "12:00" },
                    new ScheduleConfig { AllDay = false, Start = "14:00", End = "18:00" }
                ]
            }
        };
        // At 20:00, outside all windows -> blocking inactive
        Assert.False(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 20)));
    }

    [Fact]
    public void WindowAtExactBoundary_StartTime()
    {
        var day = DayOfWeek.Monday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "09:00", End = "17:00" }]
            }
        };
        // Exactly at start time -> inside window
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 9)));
    }

    [Fact]
    public void WindowAtExactBoundary_EndTime()
    {
        var day = DayOfWeek.Monday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] = [new ScheduleConfig { AllDay = false, Start = "09:00", End = "17:00" }]
            }
        };
        // Exactly at end time -> inside window (uses <=)
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 17)));
    }

    [Fact]
    public void TimeZone_DstSpringForward_ClockSkipsAhead()
    {
        // US DST spring forward: 2026-03-08 at 2:00 AM MST -> 3:00 AM MDT
        // At 09:00 UTC on 2026-03-08 (Sunday), Denver time = 03:00 MDT
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                ["sun"] = [new ScheduleConfig { AllDay = false, Start = "02:30", End = "04:00" }]
            }
        };
        // 09:00 UTC = 03:00 MDT (inside window despite 2:30 not existing)
        var utc = new DateTime(2026, 3, 8, 9, 0, 0, DateTimeKind.Utc);
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "America/Denver", false, utc));
    }

    [Fact]
    public void TimeZone_DstFallBack_ClockRepeatsHour()
    {
        // US DST fall back: 2026-11-01 at 2:00 AM MDT -> 1:00 AM MST
        // At 08:00 UTC on 2026-11-01 (Sunday), Denver time = 01:00 MST
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                ["sun"] = [new ScheduleConfig { AllDay = false, Start = "00:00", End = "02:00" }]
            }
        };
        // 08:00 UTC = 01:00 MST (inside window)
        var utc = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "America/Denver", false, utc));
    }

    [Fact]
    public void TimeZone_AheadOfUtc_CorrectDayMapping()
    {
        // Tokyo is UTC+9. At 20:00 UTC Saturday, Tokyo is 05:00 Sunday.
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                ["sun"] = [new ScheduleConfig { AllDay = false, Start = "04:00", End = "06:00" }]
            }
        };
        // 20:00 UTC Saturday = 05:00 JST Sunday
        var utcSaturday = new DateTime(2026, 1, 10, 20, 0, 0, DateTimeKind.Utc); // Saturday
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "Asia/Tokyo", false, utcSaturday));
    }

    [Fact]
    public void TimeZone_BehindUtc_CorrectDayMapping()
    {
        // Hawaii is UTC-10. At 09:00 UTC Monday, Hawaii is 23:00 Sunday.
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                ["sun"] = [new ScheduleConfig { AllDay = false, Start = "22:00", End = "23:59:59" }]
            }
        };
        // 09:00 UTC Monday = 23:00 HST Sunday
        var utcMonday = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc); // Monday
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "Pacific/Honolulu", false, utcMonday));
    }

    [Fact]
    public void TimeZone_HalfHourOffset_India()
    {
        // India is UTC+5:30. At 00:00 UTC, India is 05:30.
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                ["mon"] = [new ScheduleConfig { AllDay = false, Start = "05:00", End = "06:00" }]
            }
        };
        // 00:00 UTC Monday = 05:30 IST Monday (inside 05:00-06:00 window)
        var utc = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "Asia/Kolkata", false, utc));
    }

    [Fact]
    public void MixedValidAndInvalidWindows_SkipsInvalid()
    {
        var day = DayOfWeek.Monday;
        var profile = new ProfileConfig
        {
            Schedule = new()
            {
                [DayKey(day)] =
                [
                    new ScheduleConfig { AllDay = false, Start = "invalid", End = "invalid" },
                    new ScheduleConfig { AllDay = false, Start = "09:00", End = "17:00" }
                ]
            }
        };
        // At 10:00, first window skipped (invalid), second window matches
        Assert.True(FilteringService.IsBlockingActiveNow(profile, "UTC", false, UtcAt(day, 10)));
    }
}
