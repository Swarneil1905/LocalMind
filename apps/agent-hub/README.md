# LocalMind Agent Hub desktop pilot

Requires the .NET 10 SDK. This is a native Avalonia application with compiled XAML bindings and a C# view model. It can run independently of Tauri, Node, Python and the AI service.

From the repository root:

    dotnet run --project apps/agent-hub
    dotnet run --project apps/agent-hub-checks -c Release

Choose **File → Open project file…** (Ctrl+O), then a LocalMind `projects.db` file, or pass its path after `--` on the run command. Prefer a backup made with SQLite's backup facility if the original profile is in use. The pilot opens the selected file in read-only mode and does not migrate it. **File → Refresh** (F5) reloads it and keeps the selected task if it still exists.

The workbench lists projects, tasks and recorded prerequisites. Selecting a task shows its recorded status, owner (always *Unassigned*: the schema has no assignee) and every prerequisite with its ID in the **Task details** pane. Older profiles without a prerequisites table remain readable. This is not yet a scheduler or an installed update to the existing desktop app. Provider runs, event streaming, reviews, crash recovery, and signed packages are separate milestones.

## Commands and shortcuts

Menus, buttons and shortcuts call the same command object, so each action runs once.

| Menu | Shortcut | Available when |
|---|---|---|
| File → Open project file… | Ctrl+O | always |
| File → Refresh | F5 | a file has been opened |
| View → Projects | Ctrl+B | always (hides the pane; the selected project stays selected) |
| View → Task details | Ctrl+I | always |
| Go → Blocker | Ctrl+G | the selected task has an unresolved prerequisite |
| Go → Back | Ctrl+Backspace | you followed a blocker and its source task still exists |

Back uses Ctrl+Backspace on purpose. Avalonia activates the menu bar when Alt is released, which pulled focus off the task after Alt+Left. On macOS the usual Command equivalents are untested; nothing here has been run on macOS.

Layout: at 920 DIPs or wider, Task details sits beside the list. Below that it opens on request in place of the list (**Task details** button, **Back to tasks** to return). Layout is not saved between runs.

## Windows smoke check (about 2 minutes)

1. Write a disposable database. It is a synthetic fixture, not your profile:

        dotnet run --project apps/agent-hub-checks -- --fixture outputs/agent-hub-planning/smoke/demo.db

2. Start the app on it:

        dotnet run --project apps/agent-hub -- outputs/agent-hub-planning/smoke/demo.db

3. Expect project **Launch** and three tasks: Review (open), Review (cancelled), Approve revision (open, "Requires Review (open) · 1 more unresolved"). Task details says "Select a task…".
4. Press F10, Right, Right, Down. The Go menu opens with **Blocker** and **Back** greyed out. Press Escape: the menu closes and focus returns to the control you were on.
5. Tab to the task list, arrow to **Approve revision**. Task details lists two prerequisites, IDs r1 and r2; r2 is cancelled and still has a **Go** button.
6. Press Ctrl+G. **Review** (r1) is selected and focused; Task details shows it. Press Ctrl+Backspace: focus returns to **Approve revision**.
7. Press Ctrl+I. The details pane closes and the list widens. Press Ctrl+I again to bring it back.
8. Shrink the window below about 920 DIPs wide. The details pane closes by itself; the **Task details** button brings it back in place of the list.

Run the automated checks with `dotnet run --project apps/agent-hub-checks -c Debug` (no arguments). They exit non-zero on the first failed check.

## Verified and not verified (Windows 11, 125% display scale, 2026-11-18)

- Verified in the compiled app: keyboard menu via F10 from an unfocused window, Escape, task selection, Ctrl+G, Ctrl+Backspace, Ctrl+I, wide and 800×600-DIP layouts, accessible task names.
- Not verified: Alt+F / Alt+G access keys from a focused control, the file picker dialog, 150% scale, mixed-DPI or multi-monitor setups, screen readers, macOS, Linux.
- Note: keyboard capture used Windows SendKeys, so only paced real-keyboard use confirms the access keys.
