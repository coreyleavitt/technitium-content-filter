# DNS Blocklists

Blocklists are remote domain lists that the plugin downloads and uses for blocking. They are defined globally and assigned to individual profiles.

## Supported Formats

The plugin parses three common blocklist formats:

=== "Plain Domain"
    ```
    ads.example.com
    tracker.example.com
    ```

=== "Hosts File"
    ```
    0.0.0.0 ads.example.com
    127.0.0.1 tracker.example.com
    ```

=== "AdGuard/ABP"
    ```
    ||ads.example.com^
    ||tracker.example.com^
    ```

Comment lines (starting with `#` or `!`) are ignored in all formats.

## Adding a Blocklist

Navigate to **Filters > DNS Blocklists** and click **Add Blocklist**.

| Field | Description |
|-------|-------------|
| Name | Display name for the blocklist |
| URL | HTTPS URL to the blocklist file |
| Refresh Hours | How often to re-download (default: 24) |
| Enabled | Toggle to enable/disable without removing |

## Assigning to Profiles

After adding a blocklist globally, assign it to profiles:

1. Go to **Profiles** and edit a profile
2. Check the blocklists you want active for that profile
3. Save the profile

A single blocklist can be assigned to multiple profiles. The list is downloaded once and shared.

## Refresh Behavior

- The plugin checks blocklists every 15 minutes in the background
- Each list is only re-downloaded if its `refreshHours` interval has elapsed
- Use the **Refresh All** button to force an immediate refresh of all lists
- Downloaded lists are cached on disk in the plugin's app folder

## Domain Counts

The blocklist table shows the number of domains loaded from each list. This count is updated after each refresh.

!!! tip
    Popular blocklists to consider:

    - [OISD](https://oisd.nl/) -- comprehensive ad/tracking list
    - [Steven Black's Hosts](https://github.com/StevenBlack/hosts) -- unified hosts with multiple extensions
    - [Hagezi's DNS Blocklists](https://github.com/hagezi/dns-blocklists) -- multi-level threat protection
