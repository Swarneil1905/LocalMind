# LocalMind Agent Hub desktop pilot

Requires the .NET 10 SDK. This is a native Avalonia application with compiled XAML bindings and a C# view model. It can run independently of Tauri, Node, Python and the AI service.

From the repository root:

    dotnet run --project apps/agent-hub
    dotnet run --project apps/agent-hub-checks -c Release

Choose **Open database…**, then a LocalMind `projects.db` file, or pass its path after `--` on the run command. Prefer a backup made with SQLite's backup facility if the original profile is in use. The pilot opens the selected file in read-only mode and does not migrate it. Refresh reloads the selected database.

The first slice lists projects, tasks, and recorded prerequisites. Older profiles without a prerequisites table remain readable. This is not yet a scheduler or an installed update to the existing desktop app. Provider runs, event streaming, reviews, crash recovery, and signed packages are separate milestones.
