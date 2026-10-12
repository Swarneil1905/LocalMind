# LocalMind product redesign

## Scope and decisions

The requested multi-user setup is independent desktop installations. LocalMind keeps React, Tauri/Rust, FastAPI, SQLite/LanceDB, and Ollama. Each installation owns its local profile and service lifecycle. No hosted accounts, shared database, synchronization service, or new cloud inference dependency was introduced.

The original screenshots point to orchestration and retrieval defects, not evidence that an open model is inherently unusable. Request identity, startup gating, cancellation, source freshness, follow-up resolution, and model selection are documented in [CHAT_RELIABILITY.md](CHAT_RELIABILITY.md). A stronger model cannot correct a reply attached to the wrong request.

## Implemented interface

- One navigation sidebar combines workspace destinations and searchable, date-grouped chat history. Rename is an explicit action; delete has a confirmation. New-chat keyboard shortcut: Ctrl/Cmd+Shift+O.
- Chat uses a readable answer column, distinct user messages, per-answer source title/domain/publication dates, copy, reasoning disclosure, stop, and retry. Retry replaces failed/stopped turns; scroll follows new turns without interrupting someone reading earlier content.
- Drafts survive failed chat creation, newer typing during startup, follow-up actions, and navigation. Failed history loading blocks new turns until recovery, preventing replies based on incomplete context.
- The composer stays available during model startup so a user can draft a message. Sending waits for the service and selected model. State and recovery actions explain what happened without exposing implementation jargon as an assistant reply.
- Knowledge, Memory, Projects, Tasks, Overview, Connections, Settings, onboarding, and update notices use shared page/control styles. Loading, empty, failed, and saved states are separate. Mutations report failures rather than removing data optimistically and pretending success.
- Settings persists selected chat models. Download models reopens the local model downloader, including after skipping onboarding or installing only an embedding model. Voice is explicitly unavailable; fake recording/transcription behavior was removed.
- Original LocalMind mark is integrated into the shell, setup, favicon, and desktop icons. IBM Plex Sans is bundled with its OFL license. No runtime font CDN request is needed. See [BRAND.md](BRAND.md) for asset provenance, exact generation prompt, and paths.
- Cool blue-gray surfaces, restrained accent, shared light/dark tokens, OS-following theme, reduced-motion behavior, keyboard focus, modal drawer focus management, and responsive layouts replace scattered inline styling.

## Engineering boundaries

[DESKTOP_RUNTIME.md](DESKTOP_RUNTIME.md) records profile storage, safe legacy migration, service authentication, crash supervision, retries, local proxy bypass, and distribution limitations. Workspace hooks subscribe before initial loads, refresh when the service becomes ready, clean up under React StrictMode, and reject stale results after navigation. Rust propagates unsuccessful HTTP responses to the interface.

The design guidance was read at its source. [DESIGN_RESEARCH.md](DESIGN_RESEARCH.md) links the supplied Snyk/Kimi pages and the actual Anthropic, UI Craft, Vercel, and architecture skills they reference. Those repositories informed concrete behavior and review criteria; their code was not blindly installed or copied into the product.

## Verification evidence

- TypeScript and ESLint pass. Production Vite build succeeds. Rust Clippy passes with warnings denied.
- Frontend regressions cover draft preservation, blocked history failure, stop-before-first-token retry, service restart, event identity, listener readiness, StrictMode, startup refresh, stale navigation responses, mutation failure, knowledge search submission, and model download recovery.
- Python tests cover retrieval policy, source persistence, auth, profile isolation/locking/migration, first-run project associations, and local client proxy behavior. Rust tests cover partial stream framing, model-download errors, and non-success mutation responses.
- Headless Edge exercises the actual Vite UI using an isolated Tauri bridge fixture. Checks include all workspace destinations, search settings, history search/rename/delete confirmation, saved-history failure/retry, failed create, newer draft preservation, stop/retry, runtime restart, and narrow drawer keyboard behavior. No page errors or horizontal document overflow were observed at the tested desktop/390px widths. Local screenshots and the verification harness are under ignored `artifacts/`.
- Earlier targeted live Ollama checks with Qwen3 8B passed a greeting after news history and a publication-date question with synthetic dated evidence. A live DuckDuckGo request returned results. These are limited probes, not a general model benchmark.

Browser fixtures do not prove native WebView/IPC packaging. A fresh installer and clean-machine native smoke test remain release work; the installed application was not replaced. Connector credentials still use local files rather than an OS credential vault. Search relevance remains heuristic and model answers still require evidence checks. Voice is not implemented.

## Reproduce

From `apps/desktop`: `pnpm typecheck`, `pnpm lint`, `pnpm test`, `pnpm build`.

From `services/ai` with `requirements.txt` installed: `python -m unittest discover -s tests -v`, `ruff check .`.

From repository root: `cargo test --workspace`, `cargo clippy -p localmind-app -- -D warnings`.

No commits, pushes, release uploads, or deployment changes were performed.

A subsequent full-stack Python check entered the real FastAPI application lifespan using a disposable profile and streamed an authenticated request to the installed Qwen3 8B model. A greeting after news history completed with HTTP 200, a greeting response, and no search sources. Full app contract regression also checks startup/shutdown, auth, persistence, projects, and tasks with real stores.

Search settings now expose only operational providers (DuckDuckGo and SearxNG). The SearxNG base address persists and reaches retrieval. Unsupported legacy provider selections are shown explicitly until the user chooses a supported provider. Nonfunctional API-key, strict-privacy, and global-enable controls were removed; web access is controlled explicitly in the composer.

Final local results: 28 frontend tests, 26 Python tests, and 7 Rust workspace tests passed. TypeScript, ESLint, Ruff, Clippy with warnings denied, production Vite build, and Git whitespace checks passed. The browser workflow harness completed with zero page errors and no horizontal content overflow across all workspace pages at 390px.
