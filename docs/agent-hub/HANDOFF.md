# Agent Hub implementation handoff

See the [desktop stack decision and release criteria](STACK.md) before changing frameworks or moving a service boundary.

Start with [the ten stories](../research/multi-agent-war-room-2026-11.md#first-implementation-backlog) and [the interaction study](../prototypes/agent-hub/RESEARCH.md). The static prototype is a design fixture. It does not execute models or reflect live database state.

## First slice: can two agents safely edit one board?

Use existing project and task IDs in projects.db. The new db/hub_dependencies.py owns prerequisite edges and a graph revision; this revision does not protect unrelated edits to task titles or status. Re-read the snapshot after a conflict. Never release a task merely because another agent sent a chat message.

Codex supplied the store and its independent tests. Claude implemented the project-scoped HTTP read and add-prerequisite routes in app/api/routes/agent_hub.py, registered them in app/main.py and wrote route tests. Codex inspected those changes and ran all eight Hub tests from services/ai with `uv run --no-project --python 3.13 --with fastapi==0.163.1 --with httpx==0.28.1 python -m unittest discover -s tests -p '*hub*.py' -v`: eight passed. This covers the route and middleware in a test app, not native IPC or packaged startup. The existing Projects → Tasks view now reads prerequisites through the Rust bridge. For the Avalonia pilot, read that API; show an explicit offline state when the sidecar is absent. Verify two open views racing on the same revision, then verify recovery in an installed build.

## Decisions for the next slices

- **Desktop:** Maintain the current Tauri release. For new Hub UI and engine work, follow the Avalonia/C# migration experiment in [STACK.md](STACK.md). The existing React prerequisites view and Python API are behavior references; compare against them before retiring either.
- **Execution:** record run, task, attempt, lease, input revision and output revision. Persist the state transition and event in one transaction. Reconnect by event cursor; deduplicate by event ID. An expired lease can requeue a read; a write with unknown outcome needs reconciliation before retry. A reviewer accepts an exact output revision before dependent work starts.
- **Security:** loopback binding alone is not authentication. Verify sidecar credentials, project scope and input size on every operation; bound queued tasks, concurrency, retries and spend. Treat model output, files, web pages and tool text as data. They cannot authorize shell, network or file access. Run coding workers in isolated workspaces with OS-enforced limits; worktrees alone do not isolate processes. Gate external effects on the exact operation and revision. Keep credentials out of prompts, logs and the renderer. A future public relay needs authenticated quotas and edge DDoS protection; the offline desktop does not need a public gateway.
- **Interface:** show what the agent produced, what it needs, and who can unblock it. Use distinct activity types for progress, questions and decisions ([Linear](https://linear.app/developers/agent-interaction)); animate only active handoffs and respect reduced motion ([21st.dev](https://preview.21st.dev/@klyv-ui/components/klyv-animated-beam)). Use the board and activity feed as two views of the same persisted events.
- **Testing spend:** deterministic fake providers in CI. Use opt-in synthetic live prompts and a small prepaid balance, no auto-top-up. Test streaming, cancellation and tool support per model ID; a shared API format does not guarantee feature parity. [OpenRouter pricing](https://openrouter.ai/pricing) and [Poe API](https://creator.poe.com/docs/external-applications/openai-compatible-api).

## Shared-folder working agreement

One writer per slice. Claude and Codex can read the same saved files, but they do not share a private conversation or automatically wake each other. At handoff record changed files, test commands/results, remaining failures and next owner. Use the diff as evidence. No cloud smoke tests, pushes or releases as a side effect of review.

Use relevant Claude user plugins from the developer's existing installation. Do not vendor the personal plugin cache or credentials. Use Ponytail for narrow implementation work and either Humanizer or Unslop when editing user-facing prose. Do not assume shorter output implies a cheaper or more correct total run.
