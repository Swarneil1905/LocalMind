# Independent desktop installations

LocalMind is a desktop application with one private profile per OS account. The
requested multi-user deployment is **independent installations for each person**.
There is no shared application server, account service, or tenant database.

## Boundaries and ownership

```text
React webview
  | typed Tauri commands / request-scoped streaming events
Rust desktop process
  | random loopback port + fresh 256-bit bearer token
Python AI service (one process owns each profile)
  |-- SQLite: conversations, memories, projects, connector messages, people
  |-- LanceDB: document embeddings
  |-- connector sessions and tokens
  `-- Ollama on loopback: locally installed inference models
```

Rust owns launch, readiness, process supervision, and cancellation. Python owns
retrieval, connector work, and persistence. React owns presentation and explicit
user actions; the private sidecar token stays in Rust. Existing request IDs keep
streaming results attached to the correct conversation.

This boundary follows the component-boundary and quality-attribute approach in
the reviewed [architecture skills](https://github.com/45ck/software-architecture-skills):
derive boundaries from ownership, deployment, and failure behavior. Separate
network services and distributed tenancy would add operational work without
serving this deployment requirement.

## Profile storage

All Python stores resolve through `app.storage`. Rust supplies the same directory
when launching the Python script or packaged sidecar.

| Platform | Default profile directory |
| --- | --- |
| Windows | `%APPDATA%\LocalMind` |
| macOS | `~/Library/Application Support/LocalMind` |
| Linux | `$XDG_DATA_HOME/LocalMind`, or `~/.local/share/LocalMind` |

Existing Windows installations keep their current directory. An explicit,
absolute `LOCALMIND_DATA_DIR` selects an isolated developer/test profile. Relative
paths are rejected. A missing OS data directory is an actionable startup error;
the launcher never falls back to a shared working directory. Standalone Python
uses the same per-user defaults rather than the old temporary `localmind_dev`.

Earlier releases placed connector databases, OAuth files, and WhatsApp profiles
under `~/.localmind`, ignoring the launcher's profile directory. On startup,
the default profile copies missing legacy connector files into the canonical
directory. SQLite backup includes committed WAL data. Migration publishes staged
copies, never overwrites an existing target, and retains the original files.
Successful migration records a completion marker so a later disconnect or
deletion cannot resurrect a legacy session on the next launch.
Explicit custom profiles import nothing. The old shared temporary development
directory is deliberately not imported because its ownership is ambiguous.

An OS file lock permits one AI service per profile. This prevents duplicate
background sync jobs and concurrent migrations. The lock is released by process
exit, including a crash. Two separate profiles can run with separate sidecar
ports; two windows using the same profile do not create competing sync engines.
The second instance reports startup failure until the existing instance closes.

People sharing the same OS login intentionally share its default LocalMind
profile. Separate OS accounts or devices supply the requested user isolation.
Custom profile paths are a development facility, not an in-app account system.

## Authentication and lifecycle

- Every HTTP route, including `/health` and API metadata, requires the current
  launch token. Missing or whitespace-only configuration fails closed. Token
  comparison uses a constant-time comparison.
- Python binds only to `127.0.0.1`. A bundled Ollama process is explicitly bound
  to loopback as well. Ollama readiness checks require successful HTTP status
  codes before treating a response as success.
- Rust loopback clients bypass system proxies, and Python Ollama clients ignore
  proxy environment variables. A corporate `HTTP_PROXY` cannot receive local
  prompts or session tokens. External web-search clients keep their normal proxy
  behavior.
- IPC mutations reject HTTP failures before emitting success or deleting an item
  from the interface. This includes memory, knowledge, projects, tasks, chats,
  connector changes, and search configuration.
- Readiness waits up to approximately 90 seconds and notices an exited process
  immediately instead of spending the entire timeout polling a dead child.
- `get_runtime_status` returns `{status, error}` with `starting`, `ready`, or
  `failed`. It retains startup failures for late subscribers and reloads.
  Existing `get_sidecar_status`, `sidecar-ready`, and `sidecar-failed` remain
  compatible.
- The supervisor checks the owned process every two seconds. If it exits,
  pending chat work is cancelled and the UI receives the failed state.
- `restart_sidecar` retries a failed runtime. Concurrent retries are rejected;
  a healthy or starting runtime cannot be interrupted by this command. There is
  no automatic replay of a question or connector mutation after a crash.
- Normal FastAPI shutdown cancels startup/background sync before releasing the
  profile lock. Rust kills and reaps its owned sidecar when the handle is dropped.

The supervisor detects process exit, not all possible hangs inside a live
process. Per-request timeouts still handle unresponsive model calls. An existing
system Ollama is a separate, user-managed local service and keeps its existing
model directory. LocalMind does not turn that service into a multi-tenant API.

Connector tokens remain files protected by the OS account's directory
permissions; this change does not claim encryption at rest or protection against
administrators/malware running with the same account. Native keychain storage
would be a separate credential-migration change.

## First-run behavior and persistence

Model-download progress is parsed as buffered newline-delimited JSON so split
network chunks do not disappear. HTTP/model errors fail the command, and an
incomplete stream is not reported as a successful installation. A successful
download requires Ollama's explicit terminal `success` record.

The conversations store owns its `project_id` schema migration. Project-to-chat
association opens that initialized store, so a new profile can organize its very
first conversation without restarting. Schema setup no longer depends on Python
module import order.

## Regression scenarios

| Trigger | Required result | Coverage |
| --- | --- | --- |
| Switch between two isolated profiles | Chats, connector DB paths, and search settings do not leak | Python profile tests |
| Token absent, empty, or from another launch | All protected requests rejected | Python auth + HTTP middleware tests |
| Health probe without token | `401`; authenticated probe returns `200` | Python HTTP middleware test |
| Second service opens the same profile | Clear failure; next service can start after lock release | Python lock test |
| Legacy connector DB has uncheckpointed WAL | Committed records copied; source and newer target preserved | Python migration test |
| Fresh install associates its first chat | Association works without restarting | Python first-profile regression |
| Child exits before becoming ready | Immediate failure before readiness deadline | Rust sidecar test |
| Model download JSON splits mid-Unicode character | Complete records reconstructed | Rust download parser test |
| Model download emits an error | Error reaches caller, not success | Rust download parser test |
| Backend rejects a deletion with `404`/`500` | IPC rejects; no successful deletion result | Rust loopback HTTP test |
| System has an HTTP proxy and no bypass list | Ollama request goes directly to loopback | Python local transport test |

Validation commands:

```sh
cd services/ai
python -m unittest discover -s tests -v
ruff check .
# From repository root:
cargo check -p localmind-app
cargo test -p localmind-core -p localmind-app --lib
```

Full Python requirements were installed in an isolated test dependency folder. A real FastAPI application import and two startup/shutdown cycles passed with a disposable profile: authenticated health, saved sources, first-launch project association, task CRUD/error responses, persistence after reopening, and profile-lock release. Background external sync work was stubbed. These checks do not replace building and smoke-testing the packaged installer on each supported OS.
