# Security Policy

## Version information

Include the installed version or source commit in your report. The [latest release](https://github.com/Swarneil1905/LocalMind/releases/latest) identifies the current published version; `main` may contain changes that have not shipped. If you reproduced the issue on multiple versions, list them separately.

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

Local chat inference runs on the user's computer through Ollama. Optional cloud chat and image API requests use the user's own provider account and are blocked while local-only mode is on, which is the default. Optional features have separate network and credential requirements:

- **No built-in analytics or usage tracking.** Local chat does not require a LocalMind account. Optional connected services can require their own accounts.
- **Network use.** Web queries and retrieved pages, authorized connectors, reviewed Runway video requests, optional cloud chat and image API requests, model/runtime downloads and GitHub update checks can use the network. See the [data destinations](README.md#your-data) and [feature limitations](README.md#optional-and-experimental-features).
- **Cloud chat.** A cloud chat request goes to the selected provider's fixed HTTPS API: OpenAI, Anthropic or Google Gemini. Depending on the turn and its enabled tools, it can include the message and conversation history, memory, project or document excerpts, connected-app context, web evidence and attached images. The service sends it only when the provider, model and every included data category (messages, memory, knowledge, connectors, web, attachments) match an approval the user granted. That approval is standing per provider, model and data categories, not per message. It lasts until the user revokes it, changes the model or API key, disconnects the provider or turns local-only mode back on. A preview of the prepared request does not fix its content; retrieved context can change before sending. See [local and cloud chat](docs/cloud-chat.md).
- **Cloud images.** An image API request sends the prompt, and for an edit the selected reference image, to OpenAI. It is sent only after the user prepares, reviews and submits that request, within a reserved allowance. It does not use the chat data-category approval. See [media](docs/media.md#image-api-in-the-current-source-build).
- **What this describes.** The cloud chat and image statements above describe what the Python service enforces, not the desktop approval screens. Automated tests use mocked provider responses and do not establish live provider compatibility. The paid chat smoke test runs only on explicit opt-in, and live paid-image acceptance is still pending.
- **Credentials and stored data.** Gmail, Runway and cloud chat API keys (OpenAI, Anthropic and Google Gemini) use the native OS credential store with no plaintext fallback. Image API requests reuse the stored OpenAI chat key. Keys are scoped to the LocalMind profile and are not written to profile databases. Credentials are used to authenticate to their providers. Ordinary profile databases are not encrypted by LocalMind and rely on OS account permissions.
- **Sidecar authentication.** The Python sidecar listens on loopback and uses a per-session bearer token generated at startup. The token is never written to disk. This is a local desktop boundary, not a hosted multi-user authentication system.
- **Web fetching.** The fetcher checks destination addresses and redirect hops, rejecting private, loopback and link-local addresses. Report bypasses privately.
- **Untrusted context.** Web content is filtered and supplied as reference data. These measures do not guarantee protection against prompt injection. Action approval and scope checks must remain enforced outside model-generated text.

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
