# DNS Allowlists

Allowlists define domains that should always be permitted for a specific profile, regardless of blocklists, blocked services, or custom rules.

## How It Works

When a domain appears in a profile's allowlist, it bypasses all blocking checks. This is evaluated **before** blocklists and services in the [filtering pipeline](../architecture/filtering.md), so allowlist entries always take precedence.

Allowlist matching supports subdomains: adding `example.com` to the allowlist also allows `sub.example.com`, `deep.sub.example.com`, etc.

## Editing Allowlists

Navigate to **Filters > DNS Allowlists** and select a profile from the picker. Enter one domain per line in the textarea.

```
khanacademy.org
school.edu
educational-site.org
```

Click **Save** to persist changes. The domain count updates automatically.

## Use Cases

- **Override base profile blocks**: If the base profile blocks a domain via a blocklist, a child profile's allowlist can restore access
- **School/work resources**: Ensure educational or productivity sites are never blocked
- **Troubleshooting**: Temporarily allow a domain to diagnose filtering issues

!!! note
    Allowlists are per-profile. Each profile maintains its own independent allowlist. When using a [base profile](base-profile.md), the base profile's allowlist merges with the child profile's allowlist.
