# Blocked Services

Services provide a way to block entire platforms by name rather than managing individual domains. Each service maps to a set of domains that encompass the platform's infrastructure.

## Built-in Services

The plugin ships with built-in definitions for popular services:

- YouTube
- TikTok
- Facebook
- Instagram
- Twitter/X
- Snapchat
- Discord
- Twitch
- Netflix
- Reddit
- And more

Built-in service definitions are embedded in the plugin and updated with new releases. They cannot be edited or deleted.

## Custom Services

Define your own services for platforms not covered by built-in definitions.

Navigate to **Filters > Blocked Services** and click **Add Custom Service**.

| Field | Description |
|-------|-------------|
| Service ID | Unique identifier (lowercase, hyphens allowed) |
| Name | Display name |
| Domains | One domain per line |

```
streaming.example.com
cdn.streaming.example.com
api.streaming.example.com
```

!!! warning
    Custom service IDs must not conflict with built-in service IDs. The UI prevents saving if a conflict is detected.

## Assigning Services to Profiles

Services are assigned to profiles in the profile edit modal:

1. Go to **Profiles** and edit a profile
2. Check the services you want blocked
3. Save the profile

Both built-in and custom services appear in the same checklist.

## How Service Blocking Works

When a service is assigned to a profile, all of its domains are added to the profile's compiled blocked domain set. Domain matching includes subdomains -- blocking `youtube.com` also blocks `www.youtube.com`, `m.youtube.com`, etc.
