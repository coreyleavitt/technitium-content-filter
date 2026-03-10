# Web UI Setup

The web management UI is a Python application that runs alongside Technitium DNS Server. It reads and writes the plugin's configuration file directly and triggers Technitium to reload after changes.

## Running with Docker

The recommended deployment uses Docker. The web UI container needs access to the plugin's configuration file via a shared volume.

!!! tip
    A complete Docker Compose example with both Technitium DNS Server and the Web UI is available at [`docker-compose.example.yaml`](https://github.com/coreyleavitt/technitium-content-filter/blob/main/docker-compose.example.yaml) in the repository root.

```yaml
services:
  parental-controls-web:
    build: ./web
    environment:
      - CONFIG_PATH=/data/dnsApp.config
      - TECHNITIUM_URL=http://technitium:5380
      - TECHNITIUM_API_TOKEN_FILE=/run/secrets/api_token
      - BASE_PATH=/parental/
    volumes:
      - technitium-data:/data
    secrets:
      - api_token
```

## Environment Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `CONFIG_PATH` | Yes | `/data/dnsApp.config` | Path to the plugin's config file |
| `TECHNITIUM_URL` | Yes | `http://technitium:5380` | Technitium DNS Server HTTP API URL |
| `TECHNITIUM_API_TOKEN` | Yes* | `""` | API token for triggering config reloads |
| `TECHNITIUM_API_TOKEN_FILE` | Yes* | - | Path to file containing the API token (preferred over env var) |
| `APP_NAME` | No | `ContentFilter` | App name registered in Technitium |
| `BASE_PATH` | No | `/` | URL prefix when behind a reverse proxy |

*Provide either `TECHNITIUM_API_TOKEN` or `TECHNITIUM_API_TOKEN_FILE`.

## Running Locally

For development:

```bash
cd web
uv sync
uv run hypercorn app:app --bind 0.0.0.0:8000
```

## Reverse Proxy

When running behind a reverse proxy (e.g., Traefik), set `BASE_PATH` to match the proxy's path prefix. For example, if the UI is served at `/parental/`:

```
BASE_PATH=/parental/
```

The UI uses relative URLs for all API calls and static assets, so path stripping at the proxy level works correctly.

## Pages

The web UI provides:

| Page | Path | Description |
|------|------|-------------|
| Dashboard | `/` | Protection toggle, stats, client table, profile summary |
| Profiles | `/profiles` | Create, edit, delete filtering profiles |
| Clients | `/clients` | Map client identifiers to profiles |
| DNS Blocklists | `/filters/blocklists` | Manage blocklist subscriptions |
| DNS Allowlists | `/filters/allowlists` | Per-profile allowed domains |
| Blocked Services | `/filters/services` | Built-in and custom service blocking |
| Custom Rules | `/filters/rules` | Per-profile block/allow rules |
| DNS Rewrites | `/filters/rewrites` | Per-profile domain rewrites |
