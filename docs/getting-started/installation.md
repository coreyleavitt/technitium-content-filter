# Installation

## Prerequisites

- [Technitium DNS Server](https://technitium.com/dns/) v14.3 or later
- Docker (for building the plugin)

## Build the Plugin

The plugin builds inside a Docker container that provides the Technitium SDK dependencies. The output is a ZIP archive ready for installation.

```bash
docker build -f Dockerfile.build -o dist .
```

This produces `dist/ParentalControlsApp.zip`.

!!! note
    The build clones specific Technitium SDK versions (`dns-server-v14.3.0`) inside the container. No local .NET SDK installation is required.

## Install to Technitium

### First-time installation

```bash
curl -s -X POST "https://your-dns-server/api/apps/install" \
  -F "token=YOUR_API_TOKEN" \
  -F "name=ParentalControlsApp" \
  -F "appZip=@dist/ParentalControlsApp.zip"
```

### Updating an existing installation

```bash
curl -s -X POST "https://your-dns-server/api/apps/update" \
  -F "token=YOUR_API_TOKEN" \
  -F "name=ParentalControlsApp" \
  -F "appZip=@dist/ParentalControlsApp.zip"
```

!!! warning
    Always use `apps/update` for upgrades. Using `apps/uninstall` followed by `apps/install` will **delete your configuration**.

## Enable the App

After installation, the app appears in the Technitium DNS Server admin panel under **Apps**. To activate filtering:

1. Go to **Settings** > **Blocking** in the Technitium admin
2. Set the blocking app to **ParentalControlsApp**
3. Configure your profiles and clients (see [Configuration](configuration.md))

## Next Steps

- [Configuration](configuration.md) -- Set up profiles, clients, and filters
- [Web UI Setup](web-ui.md) -- Deploy the management interface
