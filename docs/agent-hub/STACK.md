# Agent Hub desktop stack decision

Selected target for the next desktop generation: C# on .NET 10 LTS, Avalonia/XAML, and a .NET worker that will own orchestration. The [first runnable desktop slice](../../apps/agent-hub/README.md) reads project data; the worker is not implemented yet. Keep the existing app as a behavior reference while replacing it. The selection is not a claim of measured superiority.

| Boundary | Owner | Failure behavior we want |
| --- | --- | --- |
| Project board and transcript | Avalonia views + C# view models | Render the last known state with its timestamp or an explicit unavailable state. Virtualize long transcripts; support keyboard navigation, screen readers, and screenshot paste. |
| Desktop lifecycle and privileged operations | C# desktop host | Own one service per profile, supervise only owned children, keep diagnostics and signed updates accessible if the service fails. Requalify platform packaging, signing, and updates before replacing Tauri. |
| Runs and model adapters | Separate .NET worker | Check project scope and budgets before dispatch, persist transitions, cancel with deadlines, contain a failed provider to its attempt. Start with the existing Python API as a reference; retain Python for AI-specific tooling where it earns its place. |
| Tasks, dependencies and run history | SQLite in the existing profile | One authoritative writer path per transition. Commit the transition and its event together; reject stale revisions. Never replay a write of unknown outcome without reconciliation. |
| Coding tools | Separate restricted worker processes | A model may propose an action; code outside the model checks the workspace, allowed operation and user approval. A separate process or Git worktree alone is not a sandbox. |

## Evidence and alternatives

The current executable already launches a companion service, probes readiness, supervises it and initializes an updater. Startup failure in an otherwise rendered window establishes a failure boundary, not the root cause. Reproduce against the installer before blaming the webview, Python packaging, process ownership or timeouts.

[SourceGit](https://github.com/sourcegit-scm/sourcegit) is a relevant Avalonia example: a cross-platform desktop workbench with a graph, lists and detailed inspection. [Telegram Desktop](https://github.com/telegramdesktop/tdesktop) demonstrates C++/Qt for a demanding chat client. [VS Code](https://github.com/microsoft/vscode/wiki/Source-Code-Organization) illustrates a different choice: a web workbench split from privileged services. These establish feasibility, not performance for LocalMind. Angular on Electron would retain the browser/native bridge. Qt/C++ is the strongest alternative if custom rendering becomes the measured bottleneck. JavaFX is viable with a JVM investment; LocalMind has none to leverage. See [Avalonia platforms](https://docs.avaloniaui.net/docs/supported-platforms) and [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## Migration experiment

Build a sibling client in this repository with an isolated test profile. Start read-only against the existing project API; do not let two engines write the same live profile. Then implement one durable run in the .NET worker: implementer submits a revision, reviewer requests rework, user accepts the replacement, downstream task unblocks. Kill the worker halfway through. Restore the board and event cursor without reissuing accepted work. Use deterministic workers so provider latency and subscription costs do not skew results.

Compare packaged builds on the same Windows and macOS hardware: cold time to usable board, idle and peak memory with the same transcript, scrolling a 10,000-event history, screenshot paste, multi-window focus, keyboard and screen-reader operation. Record figures for both; investigate regressions before expanding migration. Exercise the release checks below before switching the default. Migrate features in slices with a backed-up test copy of the old profile. Keep existing Rust and Python implementations until their replacements pass the same behavior tests.

## Release evidence

1. Install the release artifact on clean Windows and macOS profiles. Record launch-to-usable-board separately from model warm-up and first token, across cold and warm starts.
2. Launch twice against one profile; terminate the owned service mid-run. Verify one writer, visible interruption, saved work intact, and no duplicated external action after reopening.
3. Upgrade a populated previous-version profile using signed artifacts. Test interrupted download and service unavailability; the old version must remain usable if installation never starts. Verify saved projects and task revisions after successful migration.
4. Simulate an unavailable provider and malformed tool output. Show the actual affected run, keep other work usable, require authorization for external writes, and check diagnostics for credentials and prompt contents.

If native packaging, transcript rendering or access to assistive technology fails the comparison, retain the shipping app and narrow the failing layer before expanding the port. If hosted concurrent teams become a requirement, design a server data store and authenticated relay separately from the offline profile.
