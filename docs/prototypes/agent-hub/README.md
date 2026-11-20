# LocalMind Agent Hub

An offline prototype for a shared agent workspace. This replaces the earlier concept's name and makes the flow graph, team conversation and human decisions the primary interface.

Open `index.html` in a browser. From the repository root in PowerShell:

```powershell
Start-Process (Resolve-Path 'docs/prototypes/agent-hub/index.html').Path
```

## Try it

1. Select **Run demo**. Research and implementation progress in parallel; connectors animate only while the demo is running.
2. Select an agent to inspect its conversation. The **Context** tab shows its task, tools, adapter and prerequisites. Select the people icon to return to the entire team.
3. Open **Review**, inspect the API contract, confirm the sample revision, and accept it. Alternatively, enter a rework note and request changes. A new revision requires a new confirmation.
4. Continue the demo. QA waits for its prerequisites. The final project handoff needs your acceptance.
5. Send a message to everyone or a selected agent. Paste, drop or attach a PNG, JPEG or WebP screenshot up to 5 MB. Image previews stay in the browser.
6. Explore **Tasks**, **Outputs**, and the full **Conversation** view. Inspect a connector to distinguish an assignment from an actual prerequisite.

Pan the graph by dragging its background. Zoom with the controls and reset the viewport with **Fit graph**. Hover a node for its move handle. Nodes and connectors are keyboard-selectable; reduced-motion preferences turn off animations. Ctrl/Cmd + 1–4 changes the main view.

## Boundaries

All messages, costs, artifacts, progress and checks are fixtures. The app makes no model calls, runs no tools, uploads no images, and changes no project files. Agent roles illustrate a workflow, not a ranking of provider capabilities. Adapter selections are configuration demonstrations only.

The fixed six-agent graph is not a general workflow editor. The demo has two human review gates; other fixture tasks complete automatically. There is no durable state or backend. Reloading starts a new session. JSON export excludes image contents. Source links in **Design & research** open external websites only when selected.

The implementation uses plain HTML, CSS and JavaScript so it opens without a build step or external scripts. The graph is an original SVG implementation, not Mermaid. A production version could use React Flow for graph editing and accessible UI primitives after checking their integration and licenses.

## Files

- `index.html`: workspace, inspector and composer structure.
- `style.css`: responsive layout, state styling and reduced-motion behavior.
- `app.js`: scripted task lifecycle, graph interaction, chat and review.
- [RESEARCH.md](RESEARCH.md): primary references, source inspection and REA evidence.
- [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md): provider icon attribution.

Browser checks are recorded in the ignored local `artifacts/agent-hub/` folder. They exercise the UI simulation, not a real multi-agent runtime.
