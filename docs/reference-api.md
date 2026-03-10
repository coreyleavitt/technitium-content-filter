# REST API Reference

The Content Filter Web UI exposes a JSON REST API for managing configuration. All endpoints read from and write to the shared `dnsApp.config` file and trigger a Technitium config reload after writes.

## Authentication

The API does not implement its own authentication. Protect access using a reverse proxy, firewall rules, or network segmentation.

## Response Format

All mutating endpoints return a JSON object:

```json
{ "ok": true }
```

On error:

```json
{ "ok": false, "error": "Description of the problem" }
```

---

## Configuration

### GET /api/config

Returns the full configuration object.

**Response**: The complete `dnsApp.config` JSON object (see [Configuration Reference](reference.md)).

### POST /api/config

Replaces the entire configuration.

**Request body**: A complete configuration JSON object.

**Response**:

```json
{ "ok": true, "reloaded": true }
```

The `reloaded` field indicates whether the Technitium reload API call succeeded.

---

## Profiles

### POST /api/profiles

Create or update a profile. If a profile with the given name exists, it is overwritten.

**Request body**:

```json
{
  "name": "kids",
  "description": "Restricted profile for children",
  "blockedServices": ["youtube", "tiktok"],
  "blockLists": ["https://example.com/hosts.txt"],
  "allowList": ["khanacademy.org"],
  "customRules": ["bad-site.com"],
  "dnsRewrites": [
    { "domain": "google.com", "answer": "forcesafesearch.google.com" }
  ],
  "schedule": {
    "mon": { "allDay": false, "start": "08:00", "end": "20:00" }
  }
}
```

The `name` field is extracted and used as the profile key. All other fields are stored as the profile object.

### DELETE /api/profiles

Delete a profile. Clients assigned to the deleted profile have their assignment cleared.

**Request body**:

```json
{ "name": "kids" }
```

---

## Clients

### POST /api/clients

Create or update a client entry. If `index` is provided and valid, the client at that position is replaced. Otherwise, a new client is appended.

**Request body**:

```json
{
  "name": "Kid's Laptop",
  "ids": ["192.168.1.100", "kid-laptop.dns.example.com"],
  "profile": "kids",
  "index": 0
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | No | Display name |
| `ids` | string[] | Yes | Network identifiers (IP, CIDR, MAC, or DoT client ID) |
| `profile` | string | Yes | Profile name to assign |
| `index` | integer | No | Position in the clients array to update (omit to append) |

### DELETE /api/clients

Delete a client entry by index.

**Request body**:

```json
{ "index": 0 }
```

---

## Settings

### POST /api/settings

Update global settings (blocking toggle, default/base profile, timezone, schedule mode).

**Request body**:

```json
{
  "enableBlocking": true,
  "defaultProfile": "kids",
  "baseProfile": "base",
  "timeZone": "America/Denver",
  "scheduleAllDay": true
}
```

---

## Services

### GET /api/services

Returns all available services (built-in and custom), merged into a single object. Each key is a service ID mapping to its definition.

**Response**:

```json
{
  "youtube": {
    "name": "YouTube",
    "domains": ["youtube.com", "youtu.be", "ytimg.com"]
  },
  "my-custom-service": {
    "name": "My Custom Service",
    "domains": ["example.com"]
  }
}
```

### POST /api/custom-services

Create or update a custom service definition.

**Request body**:

```json
{
  "id": "my-streaming",
  "name": "My Streaming",
  "domains": ["stream.example.com", "cdn.stream.example.com"]
}
```

### DELETE /api/custom-services

Delete a custom service definition.

**Request body**:

```json
{ "id": "my-streaming" }
```

---

## Blocklists

### POST /api/blocklists

Create or update a blocklist subscription. If a blocklist with the same URL already exists, it is updated.

**Request body**:

```json
{
  "url": "https://example.com/hosts.txt",
  "name": "Ad List",
  "enabled": true,
  "refreshHours": 24
}
```

### DELETE /api/blocklists

Delete a blocklist subscription. Also removes the URL from any profile that references it.

**Request body**:

```json
{ "url": "https://example.com/hosts.txt" }
```

### POST /api/blocklists/refresh

Trigger a config reload in Technitium, which starts a blocklist refresh cycle.

**Request body**: None (empty or `{}`).

**Response**:

```json
{ "ok": true, "reloaded": true }
```

---

## Allowlists

### POST /api/allowlists

Set the allowlist for a profile. Replaces the entire allowlist.

**Request body**:

```json
{
  "profile": "kids",
  "domains": ["khanacademy.org", "school.edu"]
}
```

Returns `400` if the profile is not found.

---

## Custom Rules

### POST /api/rules

Set custom block/allow rules for a profile. Replaces all existing rules.

**Request body**:

```json
{
  "profile": "kids",
  "rules": ["bad-site.com", "@@exception.com"]
}
```

Rule syntax: `domain.com` to block, `@@domain.com` to allow (exception), `# comment` for comments.

Returns `400` if the profile is not found.

---

## DNS Rewrites

### POST /api/rewrites

Create or update a DNS rewrite rule for a profile. If a rewrite with the same domain exists, it is updated.

**Request body**:

```json
{
  "profile": "kids",
  "domain": "google.com",
  "answer": "forcesafesearch.google.com"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `profile` | string | Profile name |
| `domain` | string | Domain to match (normalized to lowercase, trailing dot removed) |
| `answer` | string | Response value: IPv4 (A record), IPv6 (AAAA record), or hostname (CNAME) |

Returns `400` if the profile is not found or if domain/answer are empty.

### DELETE /api/rewrites

Delete a DNS rewrite rule from a profile.

**Request body**:

```json
{
  "profile": "kids",
  "domain": "google.com"
}
```

Returns `400` if the profile is not found.
