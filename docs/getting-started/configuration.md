# Configuration

The plugin stores its configuration in a JSON file managed by Technitium DNS Server. The file is located in the app's data directory and can be edited through the web UI or directly.

## Configuration Structure

```json
{
  "enableBlocking": true,
  "baseProfile": "base",
  "defaultProfile": "kids",
  "timeZone": "America/Denver",
  "scheduleAllDay": true,
  "profiles": { },
  "clients": [ ],
  "blockLists": [ ],
  "customServices": { }
}
```

## Global Settings

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `enableBlocking` | boolean | `true` | Master switch for all filtering |
| `baseProfile` | string | `""` | Profile whose filters merge into all other profiles |
| `defaultProfile` | string | `""` | Profile used for unmatched clients |
| `timeZone` | string | `"UTC"` | IANA timezone for schedule evaluation |
| `scheduleAllDay` | boolean | `true` | When true, schedules apply 24/7 unless overridden |

## Profiles

Each profile defines a set of filtering rules. See [Profiles](../guide/profiles.md) for details.

```json
{
  "profiles": {
    "kids": {
      "description": "Restricted profile for children",
      "blockedServices": ["youtube", "tiktok"],
      "blockLists": ["https://example.com/hosts.txt"],
      "allowList": ["khanacademy.org", "school.edu"],
      "customRules": ["bad-site.com", "@@exception.com"],
      "dnsRewrites": [
        { "domain": "google.com", "answer": "forcesafesearch.google.com" }
      ],
      "schedule": {
        "mon": { "allDay": false, "start": "08:00", "end": "20:00" }
      }
    }
  }
}
```

## Clients

Client entries map network identifiers to profiles. See [Clients](../guide/clients.md) for details.

```json
{
  "clients": [
    {
      "ids": ["192.168.1.100", "laptop.dns.leavitt.info"],
      "profile": "kids"
    }
  ]
}
```

Supported identifier types:

- **IPv4 address**: `192.168.1.100`
- **IPv6 address**: `2001:db8::1`
- **CIDR range**: `192.168.1.0/24`
- **MAC address**: `AA:BB:CC:DD:EE:FF`
- **DNS-over-TLS client ID**: `laptop.dns.example.com`

## Blocklists

Global blocklist subscriptions. See [Blocklists](../guide/blocklists.md) for details.

```json
{
  "blockLists": [
    {
      "url": "https://example.com/hosts.txt",
      "name": "Ad List",
      "enabled": true,
      "refreshHours": 24
    }
  ]
}
```

## Custom Services

User-defined service definitions for service-level blocking. See [Blocked Services](../guide/services.md) for details.

```json
{
  "customServices": {
    "my-streaming": {
      "name": "My Streaming",
      "domains": ["stream.example.com", "cdn.stream.example.com"]
    }
  }
}
```

## Full Reference

See [Configuration Reference](../reference.md) for every field and its accepted values.
