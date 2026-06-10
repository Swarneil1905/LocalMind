# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| 0.5.x   | ✅ Active  |
| < 0.5   | ❌ No      |

## Reporting a Vulnerability

**Please do not file a public GitHub issue for security vulnerabilities.**

Report security issues by emailing **swarneil1905@gmail.com** with the subject line:

```
[LocalMind Security] <brief description>
```

Include:

- A description of the vulnerability and its potential impact
- Steps to reproduce or a proof-of-concept (if available)
- Any suggested mitigations you have identified

### Response SLA

| Stage | Target |
|-------|--------|
| Initial acknowledgement | 48 hours |
| Triage and severity assessment | 5 business days |
| Fix or mitigation released | 30 days for critical/high, 90 days for medium/low |
| Public disclosure | Coordinated with reporter after fix is available |

## Security Model

LocalMind is designed as a **fully local, privacy-first** application:

- **No telemetry.** No usage data is collected or transmitted. All settings default to off.
- **No account required.** The app runs without creating an account or authenticating to any cloud service.
- **No cloud LLM calls.** All inference is performed locally via Ollama. Enabling web search is the only optional network feature, and it is opt-in and clearly labelled.
- **API keys stored locally.** Any API keys (e.g., web search providers) are stored only in the local SQLite database on the user's machine and are never logged or transmitted.
- **Sidecar authentication.** The Python sidecar is authenticated with a per-session bearer token generated at startup. The token is never written to disk.
- **SSRF protection.** The web fetcher validates URLs against a blocklist of private IP ranges (RFC 1918, RFC 4193, loopback) before making any outbound request.
- **Prompt injection sanitization.** Content fetched from the web is stripped of instruction-like patterns before being injected into the model context.

## Scope

The following are in scope for security reports:

- Remote code execution via the Tauri app or sidecar
- Local privilege escalation
- Data exfiltration (e.g., memory or knowledge files sent to a remote host)
- SSRF bypasses in the web fetcher
- Prompt injection that causes the model to exfiltrate local data
- Authentication bypass for the sidecar API

The following are **out of scope**:

- Vulnerabilities in Ollama itself (report to the [Ollama project](https://github.com/ollama/ollama/security))
- Vulnerabilities in third-party npm/Python packages not introduced by LocalMind code
- Social engineering attacks
- Denial-of-service against local resources (CPU/RAM exhaustion by a large model)

## Acknowledgements

We thank all researchers who responsibly disclose security issues to us.
