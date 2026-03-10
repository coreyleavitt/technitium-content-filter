# Profiles

Profiles are the core organizational unit. Each profile defines a complete set of filtering rules that apply to clients assigned to it.

## Creating a Profile

In the web UI, navigate to **Profiles** and click **Add Profile**. Each profile has:

| Field | Description |
|-------|-------------|
| Name | Unique identifier (used in client assignments and URL hashes) |
| Description | Optional text describing the profile's purpose |
| Blocked Services | Services to block (YouTube, TikTok, etc.) |
| Blocklists | Which global blocklist subscriptions to apply |
| Schedule | Day-of-week time windows for active filtering |

## Profile Fields

### Blocked Services

Select from built-in services or custom-defined services. Each service maps to a set of domains that will be blocked. See [Blocked Services](services.md).

### Blocklists

Check which global blocklist subscriptions should apply to this profile. Blocklists are defined globally and referenced by URL. See [Blocklists](blocklists.md).

### Per-Profile Filters

These are edited on their own pages under the **Filters** menu, with a profile picker to select which profile to edit:

- [Allowlists](allowlists.md) -- Domains that are always allowed (override blocks)
- [Custom Rules](rules.md) -- Additional block/allow rules
- [DNS Rewrites](rewrites.md) -- Domain-to-IP/hostname redirects

### Schedule

Optional time-based filtering. When a schedule is configured, filtering only applies during the specified time windows. Outside those windows, all queries are allowed. See [Schedules](schedules.md).

## Renaming a Profile

Renaming a profile in the edit modal creates a new profile with the new name and deletes the old one. Client assignments are updated automatically.

## Deleting a Profile

Deleting a profile removes it and all its associated filters. Clients assigned to the deleted profile become unassigned.

!!! warning
    If the deleted profile is set as the **base profile** or **default profile** in settings, those references become invalid. Update the dashboard settings after deleting.
