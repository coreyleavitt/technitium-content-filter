# Installation

## Prerequisites

- [Technitium DNS Server](https://technitium.com/dns/) v14.3 or later
- Docker (for building the plugin)

!!! important "Why v14.3?"
    The Content Filter plugin implements the `IDnsRequestBlockingHandler` interface, which was introduced in Technitium DNS Server v14.3. Older versions do not recognize this interface and will fail to load the plugin. If you attempt to install the plugin on an older version, it will appear in the Apps list but will not activate or process DNS queries.

## Build the Plugin

The plugin builds inside a Docker container that provides the Technitium SDK dependencies. The output is a ZIP archive ready for installation.

```bash
docker build -f Dockerfile.build -o dist .
```

This produces `dist/ContentFilter.zip`.

!!! note
    The build clones specific Technitium SDK versions (`dns-server-v14.3.0`) inside the container. No local .NET SDK installation is required.

## Install to Technitium

### First-time installation

```bash
curl -s -X POST "https://your-dns-server/api/apps/install" \
  -F "token=YOUR_API_TOKEN" \
  -F "name=ContentFilter" \
  -F "appZip=@dist/ContentFilter.zip"
```

### Updating an existing installation

```bash
curl -s -X POST "https://your-dns-server/api/apps/update" \
  -F "token=YOUR_API_TOKEN" \
  -F "name=ContentFilter" \
  -F "appZip=@dist/ContentFilter.zip"
```

!!! warning
    Always use `apps/update` for upgrades. Using `apps/uninstall` followed by `apps/install` will **delete your configuration**.

## Plugin Data Directory

Technitium DNS Server stores plugin data in a well-known directory structure. Understanding these paths is important for configuring the web UI and troubleshooting.

### Default Paths

| Path | Description |
|------|-------------|
| `/etc/dns/config/apps/ContentFilter/` | Plugin data directory (Linux default) |
| `/etc/dns/config/apps/ContentFilter/dnsApp.config` | Plugin configuration file (JSON) |
| `/etc/dns/config/apps/ContentFilter/blocked-services.json` | Built-in service definitions (exported by the plugin on startup) |

The `dnsApp.config` file is the shared configuration read by both the C# plugin and the web UI. The web UI's `CONFIG_PATH` environment variable should point to this file.

### Docker Deployments

When running Technitium DNS Server in Docker, the data volume typically maps to `/etc/dns/` inside the container. The plugin data directory is therefore at:

```
<technitium-volume>/config/apps/ContentFilter/
```

For example, if your Docker Compose volume is `technitium-data:/etc/dns`, the config file is at `/etc/dns/config/apps/ContentFilter/dnsApp.config` from within any container that mounts the same volume.

!!! tip
    The `blocked-services.json` file is written automatically by the plugin when it starts. You do not need to create it manually. The web UI's `BLOCKED_SERVICES_PATH` should point to this file if you want the UI to display built-in service definitions.

## Enable the App

After installation, the app appears in the Technitium DNS Server admin panel under **Apps**. To activate filtering:

1. Go to **Settings** > **Blocking** in the Technitium admin
2. Set the blocking app to **ContentFilter**
3. Configure your profiles and clients (see [Configuration](configuration.md))

## Next Steps

- [Configuration](configuration.md) -- Set up profiles, clients, and filters
- [Web UI Setup](web-ui.md) -- Deploy the management interface
