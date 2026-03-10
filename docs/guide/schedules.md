# Schedules

Schedules control when filtering is active for a profile. Outside the scheduled time windows, all DNS queries for that profile are allowed regardless of other filtering rules.

## How Schedules Work

Each profile can define time windows for each day of the week. Filtering is only active during those windows.

```json
{
  "schedule": {
    "mon": { "allDay": false, "start": "08:00", "end": "20:00" },
    "tue": { "allDay": false, "start": "08:00", "end": "20:00" },
    "wed": { "allDay": false, "start": "08:00", "end": "20:00" },
    "thu": { "allDay": false, "start": "08:00", "end": "20:00" },
    "fri": { "allDay": false, "start": "08:00", "end": "22:00" },
    "sat": { "allDay": false, "start": "10:00", "end": "22:00" },
    "sun": { "allDay": false, "start": "10:00", "end": "20:00" }
  }
}
```

In this example, filtering is active from 8 AM to 8 PM on weekdays, with extended hours on Friday and later starts on weekends.

## Timezone

Schedules are evaluated in the timezone configured in the global settings (`timeZone` field). This uses IANA timezone identifiers:

- `America/New_York`
- `America/Denver`
- `Europe/London`
- `Asia/Tokyo`

The timezone setting applies to **all** profiles.

## Schedule All Day (Global Setting)

The `scheduleAllDay` field is a **global** setting configured at the root of the configuration (not per-profile). It is labeled "24-hour schedule mode" in the web UI. It controls how schedule entries are interpreted across all profiles.

### When `scheduleAllDay` is `true` (default)

On days that have a schedule entry, blocking is active for the **full 24 hours**. The `start` and `end` time fields are ignored. This is the simpler mode -- you only need to check which days should have filtering, without worrying about specific hours.

```json
{
  "scheduleAllDay": true,
  "profiles": {
    "kids": {
      "schedule": {
        "mon": { "allDay": true },
        "tue": { "allDay": true },
        "wed": { "allDay": true },
        "thu": { "allDay": true },
        "fri": { "allDay": true }
      }
    }
  }
}
```

In this example, filtering is active all day Monday through Friday and inactive on weekends.

### When `scheduleAllDay` is `false`

You must specify `start` and `end` times for each day. Filtering is only active during the specified time window. The per-day `allDay` field can still be set to `true` to override and block for the full day on that specific day.

```json
{
  "scheduleAllDay": false,
  "profiles": {
    "kids": {
      "schedule": {
        "mon": { "allDay": false, "start": "08:00", "end": "20:00" },
        "tue": { "allDay": false, "start": "08:00", "end": "20:00" },
        "sat": { "allDay": true },
        "sun": { "allDay": true }
      }
    }
  }
}
```

In this example, filtering runs 8 AM to 8 PM on Monday and Tuesday, all day on Saturday and Sunday, and is inactive on other days.

### ScheduleEntry Fields

| Field | Type | Description |
|-------|------|-------------|
| `allDay` | boolean | When `true`, filtering is active for the full day on this day |
| `start` | string | Start time in `HH:MM` format (used when `allDay` is `false`) |
| `end` | string | End time in `HH:MM` format (used when `allDay` is `false`) |

Days use 3-letter lowercase abbreviations: `mon`, `tue`, `wed`, `thu`, `fri`, `sat`, `sun`.

## Editing Schedules

In the profile edit modal, toggle each day on/off and set start/end times using the schedule grid. Days without a schedule entry mean filtering is inactive for that day.

## Evaluation in the Pipeline

Schedule checks happen at step 6 in the [filtering pipeline](../architecture/filtering.md) -- after rewrites and allowlists but before blocking. This means:

- DNS rewrites always apply regardless of schedule
- Allowlisted domains are always allowed regardless of schedule
- Only blocking decisions are affected by the schedule
