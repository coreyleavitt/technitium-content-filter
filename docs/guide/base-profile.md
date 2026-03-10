# Base Profile

The base profile is a special profile whose filters are inherited by all other profiles. This provides a way to define shared filtering rules once and apply them everywhere.

## Setting the Base Profile

In the web UI dashboard, use the **Base Profile** dropdown in the settings section to designate a profile as the base.

Any existing profile can be used as the base. It continues to function as a normal profile for clients assigned to it directly.

## Merge Behavior

When compiling a profile that is not the base profile, the plugin merges the base profile's filters:

| Filter Type | Merge Strategy |
|-------------|---------------|
| Blocked domains | Union (base + profile) |
| Allowed domains | Union (base + profile) |
| DNS rewrites | Union, profile wins on conflict |
| Blocked services | Each profile selects its own services |
| Blocklists | Each profile selects its own lists |

### Allow Overrides Block

The key design principle: a child profile's allowlist entries override base profile block entries. This is enforced by the [evaluation order](../architecture/filtering.md) -- allowlists are checked before blocklists.

**Example**: If the base profile blocks `social-media.com` via a blocklist, a child profile can add `social-media.com` to its allowlist to restore access for its clients.

## Use Cases

### Shared Ad Blocking

Set up a base profile with ad-blocking and tracking-protection blocklists. All other profiles inherit these blocks automatically without needing to subscribe to the same lists individually.

### Layered Restrictions

```
Base Profile ("base")
├── Ad blocking blocklists
├── Malware domain blocklists
└── DNS rewrites for safe search

Children Profile ("kids")
├── Inherits all base blocks
├── + Blocked services: YouTube, TikTok
└── + Custom rules for age-inappropriate sites

Adults Profile ("adults")
├── Inherits all base blocks
└── + Allowlist exceptions for specific sites
```

## Base Profile vs Default Profile

These are two distinct concepts that are often confused:

| | Base Profile | Default Profile |
|---|---|---|
| **Purpose** | Filter inheritance -- its rules merge into all other profiles | Fallback assignment -- used for clients not assigned a specific profile |
| **Setting** | `baseProfile` in global settings | `defaultProfile` in global settings |
| **Effect** | Blocklists, allowlists, rewrites, and custom rules from the base are merged into every other compiled profile | Clients without an explicit profile assignment are treated as if they belong to this profile |
| **UI label** | "Base Profile" dropdown | "Default Profile" dropdown (shows "None (allow all)" when empty) |

They can be set to different profiles. For example:

```json
{
  "baseProfile": "shared-security",
  "defaultProfile": "adults"
}
```

In this setup:

- **shared-security** provides malware/phishing blocklists that are inherited by all profiles (kids, adults, guests, etc.)
- **adults** is the fallback for any client without an explicit assignment, applying the adults profile's own filters plus the inherited shared-security filters
- A client explicitly assigned to "kids" gets the kids profile's filters plus the inherited shared-security filters

If `defaultProfile` is empty, unassigned clients fall through to the base profile. If both are empty, unassigned clients are unfiltered.

## Profiles Without a Base

When no base profile is configured, each profile operates independently. Its compiled domain sets contain only its own filters.

!!! tip
    The base profile only affects filter compilation. Client-to-profile assignment is unchanged -- clients still need explicit assignments or fall through to the default profile.
