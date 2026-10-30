# Connections and owned local tools

These changes are in source after v0.7.1. They are not part of the published v0.7.1 installers.

## Gmail read-only pilot

Connections advertises authorization, read/sync, actions and revocation separately. Gmail has no action adapter. It requests only `https://www.googleapis.com/auth/gmail.readonly`. Read-only Gmail access is a Google restricted scope; a developer test client/account must be configured before live use. There is no send, draft, delete or mailbox-modification endpoint.

Create a Google Desktop OAuth client with the Gmail API enabled, then enter its client ID and secret in Connections. Sign-in uses the system browser, a loopback redirect, random state, PKCE S256 and a five-minute expiry. A successful token exchange, including verified credential persistence, completes the connection. Scope expansion is rejected. This implementation was tested with synthetic credentials and provider responses; live test-account consent remains pending.

Sync requests only the previous 30 days, validates each returned internal timestamp, follows at most ten pages, and fetches at most 100 unique messages. Only snippets are stored. A new sync is idempotent and can refresh an expired access token. Connected accounts sync on the existing 15-minute scheduler. There is no full-mailbox backfill or completeness claim when more than 100 messages fall in the window. Cached chat context is limited to recent snippets; source text is untrusted data.

Disconnect records a revocation-pending state before contacting Google. If the network fails, sync remains disabled across restart and the user can retry Disconnect. Successful revocation removes the token and client secret. Previously synced messages remain on disk. Older plaintext Gmail token files are never read or copied into new profiles; the exact obsolete files in the current profile are removed on successful Disconnect. Backups in an older `~/.localmind` profile are left untouched. Revoke older broad grants in your Google account before reconnecting.

## Credential boundary

`CredentialStore` accepts only registered provider keys. Its service namespace includes a hash of the profile path, so development profiles cannot read the everyday profile's tokens. It explicitly selects Windows Credential Manager, macOS Keychain or Linux Secret Service through pinned `keyring`; it does not select plugin backends, environment fallbacks or plaintext storage. Headless Linux without an unlocked Secret Service reports unavailable. Credential failures return a fixed recovery message without the backend exception text. Gmail diagnostics expose only status, a controlled detail message and read-only access, with no live mailbox fetch or credentials.

Automated fixtures cover locked/unavailable storage. The native Windows test writes, reloads and deletes one synthetic token through a new store instance. macOS/Linux native storage still needs a platform acceptance run. OS keychains protect stored secrets; they are not a sandbox against arbitrary code already running as the same OS user.

## Owned MCP pilot

Connections shows the executable, arguments, input schemas and declared scope before enable. The only configured server is LocalMind's owned fixture server. Development uses the current Python executable with `-m app.mcp_server`; a packaged sidecar uses its own executable with `--owned-mcp`. No command, argument, executable, environment override or remote server URL can be supplied from the UI or model.

The client implements the MCP `2025-06-18` stdio lifecycle: initialize, initialized notification, tools/list and tools/call, using bounded newline-delimited JSON-RPC. Discovery must exactly match the reviewed manifest. Each call is independently validated against the closed input schema and read-only scope. `read_fixture` accepts only the virtual name `sample.txt`; it never opens a filesystem path. `wait_fixture` accepts an integer from 0 to 20 for cancellation tests. Calls time out after five seconds, stop the owned process and require re-enabling. Stop, closing Connections, and service shutdown also terminate the process. Credentials and the sidecar bearer token are not inherited by the child.

Replies are bounded, validated and rendered as untrusted plain text. They cannot expand tool permissions. Chat has no MCP execution integration in this pilot. This is intentionally a narrow owned-server adapter, not support for arbitrary third-party servers or a general process sandbox.

## Built-in planner and repair recipe

The Local planner starts disabled. Review its `task.preview` permission and enable it in Connections. Enter up to twelve task titles, one per line, then preview them. The handler reuses the task flow's strict title and date validation; it does not call a model or save tasks. Chat's existing model-generated task proposals still require separate confirmation to save. Disabling the plugin removes its advertised tools and rejects direct calls. A handler failure disables it with a safe recovery message. There are no plugin subprocesses or background jobs.

The shipped manifest declares ID, semantic version, a fixed built-in entry point, permissions and typed tools. Registration rejects unknown fields, undeclared permissions, arbitrary entry points and schema changes before writing state. A changed stored manifest requires re-enabling. This is an owned-plugin foundation, not a plugin marketplace or third-party code loader.

Repair recipe export contains only a fixed declarative recipe and synthetic fixtures. It reads no chats, credentials or project files. Save the JSON, then open it or paste it into Connections and choose **Import and run checks**. Import uses the same exact-match preview function as project repair, with two temporary synthetic files. Exactly one match, valid Markdown, unchanged original and expected output must all pass before registration. Unsupported versions, additional fields, duplicate JSON keys, changed paths, missing checks and packages over 16 KiB are rejected. Failed imports preserve the prior registered recipe and permit a valid retry. Registration stores only the recipe ID and version. Import does not authorize access to any project folder.

## Beta availability

| Integration | Disposition | Evidence and remaining limit |
| --- | --- | --- |
| Gmail | Read-only pilot | Synthetic OAuth, expiry, sync and revoke tests; native Windows credential check. Consented live-account and macOS/Linux credential acceptance remain pending. |
| Owned MCP server | Local fixture pilot | Handshake, declared-tool validation, timeout, crash, cancellation and process cleanup tests. No arbitrary servers or automatic chat execution. |
| Local planner | Built-in preview pilot | Strict manifest, permission changes, disable, failure/re-enable and API tests. No task writes. |
| Repair recipe | Synthetic research | Export privacy, staged-copy checks, corrupt-import rejection and retry tests. Only the exact shipped package is accepted. |
| WhatsApp | Disabled | Unavailable adapter rejects connect, sync and actions. Older unverified browser adapter is not registered. |
| iMessage | Disabled on macOS | Unavailable adapter rejects reads/actions. No local message database access through this adapter. |
| Images | Not installed | Separate pinned model benchmark; no image execution endpoint or worker in this beta. |

Disabled adapters do not erase previously cached data or revoke older sessions with external services. Connections labels local/cloud access and exposes actual status, permission limits and verification evidence. Poll results started before an action cannot overwrite the state returned afterward. A failed sync refreshes the provider's offline/expired status instead of retaining a successful state.

## Verification commands

From the repository root, using the service dependencies:

```powershell
$env:PYTHONPATH = "$PWD/services/ai"
python -m unittest discover -s services/ai/tests -p "test_connections.py" -v
python -m unittest discover -s services/ai/tests -p "test_mcp.py" -v
python -m unittest discover -s services/ai/tests -p "test_extensions.py" -v
pnpm --filter desktop test
```

For a synthetic Windows Credential Manager persistence check, set `LOCALMIND_TEST_NATIVE_CREDENTIALS=1` before running `test_connections.py`. It uses a temporary profile namespace and removes its synthetic credential afterward. Tests never sign into a real Google account. Frontend tests cover review-before-enable, stop during a pending call, stale output, disconnect during a pending poll, failed sync, corrupt recipe recovery and unmount cleanup. CI runs the transport, extension and native credential checks on Windows. Release smoke tests also exercise these routes in the frozen service.

## Protocol and platform references

- [Google desktop OAuth, PKCE and revocation](https://developers.google.com/identity/protocols/oauth2/native-app)
- [Gmail scopes](https://developers.google.com/workspace/gmail/api/auth/scopes)
- [Python keyring native backends](https://keyring.readthedocs.io/en/stable/)
- [MCP stdio transport](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports)
- [MCP lifecycle](https://modelcontextprotocol.io/specification/2025-06-18/basic/lifecycle)
