# Configuration Reference

Complete reference for all configuration fields in `config.json`.

## Root Object

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `enableBlocking` | boolean | `true` | Master switch for all DNS filtering |
| `baseProfile` | string | `""` | Name of the profile whose filters are inherited by all other profiles |
| `defaultProfile` | string | `""` | Profile used for clients without explicit assignments |
| `timeZone` | string | `"UTC"` | IANA timezone identifier for schedule evaluation |
| `scheduleAllDay` | boolean | `true` | Default schedule behavior for profiles without explicit schedules |
| `profiles` | object | `{}` | Map of profile name to [ProfileConfig](#profileconfig) |
| `clients` | array | `[]` | List of [ClientConfig](#clientconfig) entries |
| `blockLists` | array | `[]` | List of [BlockListConfig](#blocklistconfig) entries |
| `customServices` | object | `{}` | Map of service ID to [CustomServiceConfig](#customserviceconfig) |

## ProfileConfig

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `description` | string | `""` | Human-readable profile description |
| `blockedServices` | string[] | `[]` | Service IDs to block (e.g., `["youtube", "tiktok"]`) |
| `blockLists` | string[] | `[]` | URLs of global blocklists to apply |
| `allowList` | string[] | `[]` | Domains to always allow (overrides blocks) |
| `customRules` | string[] | `[]` | Custom block/allow rules (see [syntax](guide/rules.md)) |
| `dnsRewrites` | [DnsRewriteConfig](#dnsrewriteconfig)[] | `[]` | DNS rewrite rules |
| `schedule` | object | `{}` | Map of day name to [ScheduleEntry](#scheduleentry) |

### Allowed `blockedServices` values

Built-in service IDs: `youtube`, `tiktok`, `facebook`, `instagram`, `twitter`, `snapchat`, `discord`, `twitch`, `netflix`, `reddit`, and others defined in `blocked-services.json`.

Custom service IDs defined in `customServices` can also be used.

### Custom rule syntax

- `domain.com` -- Block domain and subdomains
- `@@domain.com` -- Allow domain and subdomains (exception)
- `# comment` -- Ignored

## ClientConfig

| Field | Type | Description |
|-------|------|-------------|
| `identifier` | string[] | Network identifiers for this client |
| `profile` | string | Profile name to assign |
| `name` | string | Display name (optional, used in web UI) |

### Identifier types

| Type | Format | Example |
|------|--------|---------|
| IPv4 | Dotted decimal | `192.168.1.100` |
| IPv6 | Colon-separated | `2001:db8::1` |
| CIDR | IP/prefix | `192.168.1.0/24`, `2001:db8::/32` |
| MAC | Colon-separated hex | `AA:BB:CC:DD:EE:FF` |
| Client ID | DNS hostname | `laptop.dns.example.com` |

## BlockListConfig

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `url` | string | *required* | HTTPS URL to the blocklist file |
| `name` | string | `""` | Display name |
| `enabled` | boolean | `true` | Whether to download and apply this list |
| `refreshHours` | integer | `24` | Re-download interval in hours |

### Supported blocklist formats

The parser auto-detects the format:

| Format | Example Line | Parsed Domain |
|--------|-------------|---------------|
| Plain domain | `ads.example.com` | `ads.example.com` |
| Hosts file | `0.0.0.0 ads.example.com` | `ads.example.com` |
| Hosts file (127) | `127.0.0.1 ads.example.com` | `ads.example.com` |
| AdGuard/ABP | `\|\|ads.example.com^` | `ads.example.com` |

Lines starting with `#` or `!` are treated as comments.

## DnsRewriteConfig

| Field | Type | Description |
|-------|------|-------------|
| `domain` | string | Domain to match (subdomains included) |
| `answer` | string | Response value (IPv4, IPv6, or hostname) |

### Answer types

| Answer | DNS Record Type | Example |
|--------|----------------|---------|
| IPv4 address | A | `1.2.3.4` |
| IPv6 address | AAAA | `2001:db8::1` |
| Hostname | CNAME | `restrict.youtube.com` |

## ScheduleEntry

| Field | Type | Description |
|-------|------|-------------|
| `startTime` | string | Start time in `HH:MM` format (24-hour) |
| `endTime` | string | End time in `HH:MM` format (24-hour) |

### Day names

Schedule keys are lowercase day names: `monday`, `tuesday`, `wednesday`, `thursday`, `friday`, `saturday`, `sunday`.

## CustomServiceConfig

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Display name for the service |
| `domains` | string[] | Domains to block when this service is active |

## Example Configuration

```json
{
  "enableBlocking": true,
  "baseProfile": "base",
  "defaultProfile": "kids",
  "timeZone": "America/Denver",
  "scheduleAllDay": true,
  "profiles": {
    "base": {
      "description": "Shared filtering for all profiles",
      "blockedServices": [],
      "blockLists": ["https://big.oisd.nl/domainswild2"],
      "allowList": [],
      "customRules": [],
      "dnsRewrites": [
        { "domain": "google.com", "answer": "forcesafesearch.google.com" }
      ]
    },
    "kids": {
      "description": "Restricted profile for children",
      "blockedServices": ["youtube", "tiktok", "discord"],
      "blockLists": ["https://big.oisd.nl/domainswild2"],
      "allowList": ["khanacademy.org", "school.edu"],
      "customRules": ["gambling-site.com", "@@safe-gambling-education.org"],
      "dnsRewrites": [],
      "schedule": {
        "monday": { "startTime": "07:00", "endTime": "21:00" },
        "tuesday": { "startTime": "07:00", "endTime": "21:00" },
        "wednesday": { "startTime": "07:00", "endTime": "21:00" },
        "thursday": { "startTime": "07:00", "endTime": "21:00" },
        "friday": { "startTime": "07:00", "endTime": "22:00" },
        "saturday": { "startTime": "09:00", "endTime": "22:00" },
        "sunday": { "startTime": "09:00", "endTime": "21:00" }
      }
    },
    "adults": {
      "description": "Light filtering for adults",
      "blockedServices": [],
      "blockLists": ["https://big.oisd.nl/domainswild2"],
      "allowList": [],
      "customRules": [],
      "dnsRewrites": []
    }
  },
  "clients": [
    {
      "name": "Kid's Laptop",
      "identifier": ["192.168.1.100", "kid-laptop.dns.example.com"],
      "profile": "kids"
    },
    {
      "name": "Living Room TV",
      "identifier": ["192.168.1.50"],
      "profile": "kids"
    }
  ],
  "blockLists": [
    {
      "url": "https://big.oisd.nl/domainswild2",
      "name": "OISD Big",
      "enabled": true,
      "refreshHours": 24
    }
  ],
  "customServices": {
    "family-streaming": {
      "name": "Family Streaming",
      "domains": ["stream.example.com", "cdn.stream.example.com"]
    }
  }
}
```
