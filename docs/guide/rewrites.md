# DNS Rewrites

DNS rewrites redirect queries for specific domains to alternate IP addresses or hostnames. This is useful for enforcing safe search, redirecting services to self-hosted instances, or custom DNS routing.

## How Rewrites Work

When a DNS query matches a rewrite rule, the plugin responds directly with the rewrite answer instead of forwarding the query upstream. The response type depends on the answer format:

| Answer Format | Response Type | Example |
|---------------|--------------|---------|
| IPv4 address | A record | `1.2.3.4` |
| IPv6 address | AAAA record | `2001:db8::1` |
| Hostname | CNAME record | `restrict.youtube.com` |

Rewrites are evaluated **before** blocking in the [filtering pipeline](../architecture/filtering.md). A rewrite takes precedence over both allowlists and blocklists.

## Managing Rewrites

Navigate to **Filters > DNS Rewrites** and select a profile from the picker.

### Adding a Rewrite

Enter the domain and answer in the inline form and click **Add**. The table updates immediately without a page reload.

### Updating a Rewrite

Adding a rewrite for a domain that already has one replaces the existing answer (upsert behavior).

### Deleting a Rewrite

Click **Delete** on a rewrite row. The row is removed immediately.

## Common Use Cases

### Force SafeSearch

Redirect search engines to their safe search endpoints:

| Domain | Answer |
|--------|--------|
| `google.com` | `forcesafesearch.google.com` |
| `www.google.com` | `forcesafesearch.google.com` |
| `youtube.com` | `restrict.youtube.com` |
| `www.youtube.com` | `restrict.youtube.com` |

### Block with Custom IP

Redirect a domain to a local server showing a block page:

| Domain | Answer |
|--------|--------|
| `blocked.example.com` | `192.168.1.10` |

### Self-hosted Service Redirect

Point a public domain to a local instance:

| Domain | Answer |
|--------|--------|
| `search.example.com` | `10.0.0.50` |

## Subdomain Matching

Rewrite rules use the same subdomain-walking logic as blocking. A rewrite for `example.com` also matches `www.example.com` and any other subdomain, unless a more specific rewrite exists.

!!! note
    Rewrites are per-profile. When using a [base profile](base-profile.md), the base profile's rewrites are merged with the child profile's rewrites. On conflict, the child profile's rewrite wins.
