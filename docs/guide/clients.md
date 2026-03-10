# Clients

Clients map network identifiers to profiles. When a DNS query arrives, the plugin resolves the source to a client entry and applies the assigned profile's filters.

## Client Identifiers

Each client entry has one or more identifiers. The plugin checks all identifiers when resolving a query's source.

| Type | Example | Notes |
|------|---------|-------|
| IPv4 address | `192.168.1.100` | Exact match |
| IPv6 address | `2001:db8::1` | Exact match |
| CIDR range | `192.168.1.0/24` | Matches any IP in the range |
| MAC address | `AA:BB:CC:DD:EE:FF` | Requires Technitium MAC resolution |
| DoT/DoH client ID | `laptop.dns.example.com` | Extracted from TLS SNI |

Multiple identifiers can be assigned to the same client. This is useful when a device has both a static IP and a DNS-over-TLS client ID.

## Client Resolution Order

When a DNS query arrives:

1. The plugin extracts the remote IP address and any TLS client ID from the request metadata
2. It checks client entries for a matching identifier (exact IP, CIDR range, MAC, or client ID)
3. If a match is found, the client's assigned profile is used
4. If no match is found, the **default profile** is used (if configured in dashboard settings)
5. If no default profile is set, the **base profile** is used (if configured)
6. If nothing matches, the query is allowed

## Managing Clients

In the web UI, navigate to **Clients** to add, edit, or delete client mappings.

### Adding a Client

Click **Add Client** and provide:

- **Name**: A friendly label for the client (e.g., "Kid's Laptop")
- **Identifiers**: One or more network identifiers (one per line)
- **Profile**: The filtering profile to assign

### Editing a Client

Click **Edit** on a client row to modify its identifiers or profile assignment.

### Deleting a Client

Click **Delete** on a client row. The client's devices will fall through to the default/base profile or be unfiltered.
