# Custom Filtering Rules

Custom rules provide per-profile domain-level block and allow rules without needing a blocklist subscription or service definition.

## Rule Syntax

Navigate to **Filters > Custom Filtering Rules** and select a profile. Enter one rule per line:

```
# Block specific domains
bad-site.com
malware-domain.net

# Allow exceptions (prefix with @@)
@@safe-subdomain.bad-site.com

# Comments start with #
# This line is ignored
```

### Block Rules

A plain domain name blocks that domain and all its subdomains:

```
bad-site.com
```

This blocks `bad-site.com`, `www.bad-site.com`, `cdn.bad-site.com`, etc.

### Allow Rules

Prefix a domain with `@@` to create an exception rule:

```
@@exception.com
```

Exception rules from custom rules are merged into the profile's allowlist. They follow the same precedence -- allowlisted domains bypass all blocking.

### Comments

Lines starting with `#` are comments. They are preserved in the configuration but excluded from the rule count.

## Rule Count

The UI displays the number of active rules, excluding comments. This updates in real-time as you edit the textarea.

## Interaction with Other Filters

Custom block rules are compiled into the same blocked domain set as blocklists and services. Custom allow rules (`@@` prefix) are compiled into the allowlist alongside explicit allowlist entries.

The [filtering pipeline](../architecture/filtering.md) evaluates them identically -- there is no distinction between an allowlist entry and a `@@` custom rule at evaluation time.
