# Security Policy

## Supported Versions

| Version | Supported          |
|---------|--------------------|
| 1.x     | Yes                |

## Reporting a Vulnerability

Please report security vulnerabilities through GitHub's [private vulnerability reporting](https://github.com/coreyleavitt/technitium-content-filter/security/advisories/new).

**Do not** open a public issue for security vulnerabilities.

### What to include

- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if any)

### Response timeline

- **Acknowledgment**: within 48 hours
- **Initial assessment**: within 1 week
- **Fix or mitigation**: depends on severity

## Security Scope

The following areas are in scope for security reports:

- **DNS filtering bypass**: ways to circumvent domain blocking or allowlisting
- **Configuration tampering**: unauthorized modification of filter configs
- **Web UI vulnerabilities**: XSS, CSRF, injection, or authentication bypass in the management interface
- **Information disclosure**: leaking of client identifiers, profile data, or internal state

### Out of scope

- Denial of service against the DNS server itself (upstream Technitium concern)
- Vulnerabilities in Technitium DNS Server core
- Social engineering attacks
