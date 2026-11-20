# LocalMind war room concept

An interactive, offline prototype for coordinating several agents on one project. Open `index.html` in a browser. No build or installation is required.

From the repository root in Windows PowerShell:

```powershell
Start-Process (Resolve-Path 'docs/prototypes/war-room/index.html').Path
```

## Try the workflow

1. Select **Run demo**. Claude and Gemini progress on their assigned tasks.
2. Open **LM-04**, inspect its sample evidence, check the review confirmation, and select **Accept & unblock**.
3. Review completed tasks as they arrive. Kimi and the local test assistant start only after their prerequisites are accepted.
4. Use **Request rework** to return a result for another pass. Inspect **Dependencies** to see what it holds up.
5. Open a queued task to change its owner, or add a task with a dependency and acceptance criterion.
6. Explore **Agents & connections**, **Deliverables**, the team channel, and the light theme.

The provider roles are examples, not benchmarks or claims about the best model for each job. A local model is represented by the Ollama icon.

## What is simulated

All agent activity, messages, progress, evidence, test results, costs, and adapter selections are fixtures. There are no provider connections, API keys, paid calls, shell commands, or modifications to project files. Notes are inert text. Reloading resets the session; exporting downloads the demo state as JSON. The page blocks network connections through its content security policy.

The dependency diagram draws the seven sample tasks. Newly added tasks appear on the board and in the dependency list. This is a product prototype, not an implementation of durable agent execution or a security boundary for running tools.

## Verification

Checked with headless Chrome using Playwright on Windows, with the browser context offline:

- Provider icons, task details, acceptance confirmation, prerequisite blocking and reassignment.
- Run/pause, draft focus across updates, all seven tasks reaching acceptance, and rework.
- Custom text escaping, keyboard access to graph nodes, adapter controls, light/dark themes, and a 390px viewport without page overflow.
- No browser JavaScript errors; `node --check` passes for `app.js`.

The test script and screenshots are in the local, ignored `artifacts/war-room/` folder. These checks validate the simulation only. Real agent runs, recovery after crashes, operating-system sandboxing, provider billing, and installed-app behavior remain unimplemented here.

See [research and implementation plan](../../research/multi-agent-war-room-2026-11.md). Provider icons and their license are credited in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
