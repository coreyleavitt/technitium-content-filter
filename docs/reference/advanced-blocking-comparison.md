# Comparison with Advanced Blocking App

Technitium ships a built-in [Advanced Blocking App](https://github.com/TechnitiumSoftware/DnsServer/tree/master/Apps/AdvancedBlockingApp). Content Filter started as a replacement and has reached full feature parity plus additional capabilities.

| Feature | Advanced Blocking | Content Filter |
|---|---|---|
| Domain blocklists (hosts, plain, AdBlock) | Yes | Yes |
| Regex blocklists (inline + remote) | Yes | Yes |
| Domain allowlists + `@@` syntax | Yes | Yes |
| Regex allowlists (inline) | Via URL only | Yes |
| Regex allowlists (remote URL) | Yes | No |
| Custom blocking addresses (IP/CNAME) | Per-group + per-list | Per-profile + global |
| NXDOMAIN blocking | Yes | Yes |
| TXT blocking diagnostics | Yes | Yes |
| Extended DNS Error (EDE code 15) | Yes | Yes |
| HTTP conditional fetch (ETag/If-Modified-Since) | Yes | Yes |
| Client assignment by IP/CIDR | Yes | Yes |
| Client assignment by DoT/DoH client ID | No | Yes |
| Background blocklist refresh | Yes | Yes |
| Blocklist deduplication across groups | Yes | Yes |
| Disk cache fallback on download failure | Yes | Yes |
| Global enable/disable kill switch | Yes | Yes |
| DNS rewrites (A/AAAA/CNAME per-profile) | No | Yes |
| Blocked services (72 built-in + custom) | No | Yes |
| Time-based schedules with timezone support | No | Yes |
| Base profile inheritance | No | Yes |
| Web management UI | No | Yes |
| Plugin status endpoint | No | Yes |
| Local endpoint group mapping (IP:port) | Yes | No |
| `file://` protocol for blocklists | Yes | No |
| Per-blocklist blocking mode override | Yes | No (profile-level) |
| Configurable blocking TTL | Yes | No (fixed 60s/300s) |
