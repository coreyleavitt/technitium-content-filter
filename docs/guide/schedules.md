# Schedules

Schedules control when filtering is active for a profile. Outside the scheduled time windows, all DNS queries for that profile are allowed regardless of other filtering rules.

## How Schedules Work

Each profile can define time windows for each day of the week. Filtering is only active during those windows.

```json
{
  "schedule": {
    "monday": { "startTime": "08:00", "endTime": "20:00" },
    "tuesday": { "startTime": "08:00", "endTime": "20:00" },
    "wednesday": { "startTime": "08:00", "endTime": "20:00" },
    "thursday": { "startTime": "08:00", "endTime": "20:00" },
    "friday": { "startTime": "08:00", "endTime": "22:00" },
    "saturday": { "startTime": "10:00", "endTime": "22:00" },
    "sunday": { "startTime": "10:00", "endTime": "20:00" }
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

## Schedule All Day

The global `scheduleAllDay` setting determines the default behavior when a profile has no schedule defined:

- **`true`** (default): Filtering is active 24/7 for profiles without schedules
- **`false`**: Filtering is inactive for profiles without schedules

## Editing Schedules

In the profile edit modal, toggle each day on/off and set start/end times using the schedule grid. Days without a time window configured mean filtering is inactive for that day.

## Evaluation in the Pipeline

Schedule checks happen at step 6 in the [filtering pipeline](../architecture/filtering.md) -- after rewrites and allowlists but before blocking. This means:

- DNS rewrites always apply regardless of schedule
- Allowlisted domains are always allowed regardless of schedule
- Only blocking decisions are affected by the schedule
