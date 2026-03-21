# Technitium Content Filter

[![CI](https://github.com/coreyleavitt/technitium-content-filter/actions/workflows/ci.yaml/badge.svg)](https://github.com/coreyleavitt/technitium-content-filter/actions/workflows/ci.yaml)
[![CodeQL](https://github.com/coreyleavitt/technitium-content-filter/actions/workflows/codeql.yaml/badge.svg)](https://github.com/coreyleavitt/technitium-content-filter/actions/workflows/codeql.yaml)
[![C# coverage](https://img.shields.io/endpoint?url=https://coreyleavitt.github.io/technitium-content-filter/reports/dotnet-coverage/coverage-badge.json)](https://coreyleavitt.github.io/technitium-content-filter/reports/dotnet-coverage/htmlcov/)
[![Python coverage](https://img.shields.io/endpoint?url=https://coreyleavitt.github.io/technitium-content-filter/reports/python-coverage/coverage-badge.json)](https://coreyleavitt.github.io/technitium-content-filter/reports/python-coverage/htmlcov/)
[![mypy](https://img.shields.io/endpoint?url=https://coreyleavitt.github.io/technitium-content-filter/reports/mypy/mypy-badge.json)](https://coreyleavitt.github.io/technitium-content-filter/reports/mypy/)
[![Docs](https://img.shields.io/badge/Docs-coreyleavitt.github.io-blue.svg)](https://coreyleavitt.github.io/technitium-content-filter/)
[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/)
[![Python 3.12+](https://img.shields.io/badge/Python-3.12+-3776AB.svg)](https://python.org)

A content filtering DNS app plugin for [Technitium DNS Server](https://technitium.com/dns/). Provides domain blocking, allowlisting, DNS rewrites, service-level filtering, and per-client profile assignment -- all managed through a web UI.

## Features

- **Per-profile filtering** -- Create profiles with independent blocklists, allowlists, custom rules, regex rules, and DNS rewrites. Assign profiles to clients by IP, CIDR, or DNS-over-TLS/HTTPS client ID.
- **Blocklist subscriptions** -- Subscribe to remote blocklists (AdGuard, hosts, plain domain formats) and regex blocklists. Automatic refresh with HTTP conditional fetch (If-Modified-Since/ETag).
- **Blocked services** -- Block entire services (YouTube, TikTok, etc.) with 72 built-in service definitions. Define custom services with your own domain sets.
- **Regex rules** -- Block or allow domains by regex pattern, inline per-profile or via remote regex blocklists. Compiled with ReDoS timeout protection.
- **DNS rewrites** -- Redirect domains to alternate IPs or hostnames (e.g., force SafeSearch via CNAME rewrite).
- **Custom blocking responses** -- Return specific IP addresses or CNAME records instead of NXDOMAIN. Configurable globally or per-profile.
- **Blocking diagnostics** -- TXT query reports and EDNS Extended DNS Error (EDE code 15) with profile name, matched rule, and source.
- **Base profile inheritance** -- Designate a base profile whose filters merge into all other profiles. Profile-level allowlists override base-level blocks.
- **Time-based schedules** -- Enable/disable filtering per day-of-week with timezone support.
- **Web management UI** -- Dashboard with protection toggle, profile/client management, and a full Filters menu (blocklists, allowlists, services, custom rules, DNS rewrites).

## Architecture

The system has two components that share a config file (`dnsApp.config`):

- **C# Plugin** -- Runs inside Technitium DNS Server, intercepts DNS queries, and applies filtering rules. Compiles profiles into optimized in-memory structures for O(1) domain lookups.
- **Python Web UI** -- Starlette app that reads/writes the shared config file and triggers the plugin to reload via the Technitium API.

### Evaluation Order

When a DNS query arrives, the plugin evaluates in this order:

1. **Blocking disabled?** -- Allow
2. **Resolve client to profile** (DoT/DoH client ID, exact IP, CIDR longest prefix, default)
3. **No profile?** -- Use base profile if configured
4. **DNS rewrite match?** -- Return rewrite response (A/AAAA/CNAME)
5. **Domain in allowlist?** -- Allow
6. **Domain matches regex allow rule?** -- Allow
7. **Schedule inactive?** -- Allow
8. **Domain in merged block set?** -- Block (NXDOMAIN or custom blocking addresses)
9. **Domain matches regex block rule?** -- Block
10. **Default** -- Allow

## Project Structure

```
├── src/ContentFilter/              # C# DNS app plugin
│   ├── App.cs                      # Plugin entry point (IDnsApplication)
│   ├── Models/                     # Config and compiled profile models
│   └── Services/                   # FilteringService, BlockListManager,
│                                   # ProfileCompiler, ClientResolver,
│                                   # ScheduleEvaluator, DomainMatcher, etc.
├── tests/
│   ├── ContentFilter.Tests/        # C# unit + property tests (~395)
│   ├── ContentFilter.IntegrationTests/
│   └── ContentFilter.Benchmarks/
├── web/                            # Python web management UI
│   ├── app.py                      # Starlette app factory
│   ├── config.py                   # Config I/O, validation, shared state
│   ├── filtering.py                # Pure DNS filtering logic
│   ├── middleware.py               # Auth, CSRF, rate limiting middleware
│   ├── routes.py                   # Page and API route handlers
│   ├── templates/                  # Mako HTML templates
│   ├── static/                     # Vanilla JS + Tailwind CSS
│   └── tests/                      # Python tests
│       ├── test_*.py               # Unit, API, property tests (~285)
│       └── e2e/                    # Playwright browser tests (~98)
├── Dockerfile.build                # Plugin build (outputs ZIP)
├── Dockerfile.test                 # C# test runner
└── Dockerfile.integration-test     # Integration test runner
```

## Getting Started

### Prerequisites

- [Technitium DNS Server](https://technitium.com/dns/) v14.3+ (required because the `IDnsRequestBlockingHandler` interface used by this plugin was introduced in v14.3; older versions will not load the plugin)
- Docker (for building and testing)
- [uv](https://docs.astral.sh/uv/) (for web UI development)

### Build the Plugin

```bash
docker build -f Dockerfile.build -o dist .
```

This outputs `dist/ContentFilter.zip` -- the plugin archive.

### Install the Plugin

Upload via the Technitium DNS Server API:

```bash
curl -s -X POST "https://your-dns-server/api/apps/install" \
  -F "token=YOUR_API_TOKEN" \
  -F "name=ContentFilter" \
  -F "appZip=@dist/ContentFilter.zip"
```

To update an existing installation:

```bash
curl -s -X POST "https://your-dns-server/api/apps/update" \
  -F "token=YOUR_API_TOKEN" \
  -F "name=ContentFilter" \
  -F "appZip=@dist/ContentFilter.zip"
```

### Run the Web UI

```bash
cd web
uv sync
uv run hypercorn app:app --bind 0.0.0.0:8000
```

The web UI needs these environment variables:

| Variable | Description |
|----------|-------------|
| `CONFIG_PATH` | Path to the plugin's `dnsApp.config` |
| `BLOCKED_SERVICES_PATH` | Path to `blocked-services.json` |
| `TECHNITIUM_URL` | Technitium DNS Server URL |
| `TECHNITIUM_API_TOKEN` | API token for config reload |

### Docker Compose

A complete Docker Compose example is available at [`docker-compose.example.yaml`](docker-compose.example.yaml) with both the Technitium DNS Server and the Content Filter Web UI pre-configured with a shared volume.

## Testing

### C# Tests

```bash
# Unit + property tests
docker build -f Dockerfile.test -t content-filter-tests .
docker run --rm content-filter-tests

# With coverage
docker run --rm content-filter-tests \
  dotnet test --no-restore --settings /src/app/tests/coverlet.runsettings

# Integration tests (requires Docker socket)
docker build -f Dockerfile.integration-test -t content-filter-integration .
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock content-filter-integration
```

### Python Tests

```bash
cd web

# Unit, API, and property tests (~285 tests, 98% coverage)
uv sync --extra test
uv run pytest

# E2E browser tests (~98 tests)
uv run playwright install chromium
uv run pytest -m e2e --no-cov

# Everything
uv run pytest -m '' --no-cov
```

### Mutation Testing

```bash
dotnet tool install -g dotnet-stryker
dotnet stryker -f stryker-config.json
```

## Configuration

The plugin stores its configuration in a `dnsApp.config` file in Technitium's app folder. Both the C# plugin and the Python web UI read/write this file. Here's the structure:

```json
{
  "enableBlocking": true,
  "baseProfile": "base",
  "defaultProfile": "kids",
  "timeZone": "America/Denver",
  "scheduleAllDay": true,
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
  },
  "clients": [
    {
      "ids": ["192.168.1.100", "laptop.dns.leavitt.info"],
      "profile": "kids"
    }
  ],
  "blockLists": [
    {
      "url": "https://example.com/hosts.txt",
      "name": "Ad List",
      "enabled": true,
      "refreshHours": 24
    }
  ],
  "customServices": {
    "my-streaming": {
      "name": "My Streaming",
      "domains": ["stream.example.com"]
    }
  }
}
```

## Comparison with Advanced Blocking App

Content Filter is a replacement for Technitium's built-in [Advanced Blocking App](https://github.com/TechnitiumSoftware/DnsServer/tree/master/Apps/AdvancedBlockingApp) with full feature parity plus DNS rewrites, blocked services, schedules, base profile inheritance, and a web UI. See the [full comparison](docs/reference/advanced-blocking-comparison.md).

## License

[Apache License 2.0](LICENSE)
