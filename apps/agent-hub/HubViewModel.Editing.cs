using LocalMind.AgentHub.Core;

namespace LocalMind.AgentHub;

/// <summary>Board columns and the commands that change a workspace. Every write goes through the worker with a fresh command ID.</summary>
public sealed partial class HubViewModel
{
    private string? nextSelectId;

    public IReadOnlyList<TaskItem> OpenTasks { get; private set; } = [];
    public IReadOnlyList<TaskItem> ActiveTasks { get; private set; } = [];
    public IReadOnlyList<TaskItem> DoneTasks { get; private set; } = [];
    public IReadOnlyList<TaskItem> CancelledTasks { get; private set; } = [];
    /// <summary>The Cancelled column takes space only when something is in it.</summary>
    public Avalonia.Controls.GridLength CancelledColumnWidth => CancelledTasks.Count > 0 ? new(1, Avalonia.Controls.GridUnitType.Star) : new(0);
    public string OpenHeader => $"Open · {OpenTasks.Count}";
    public string ActiveHeader => $"In progress · {ActiveTasks.Count}";
    public string DoneHeader => $"Done · {DoneTasks.Count}";
    public string CancelledHeader => $"Cancelled · {CancelledTasks.Count}";

    /// <summary>True when the open file is an Agent Hub workspace (editable through the worker), false for a read-only legacy file.</summary>
    public bool IsWorkspace => workspaceName != null && workspaces != null;
    public bool CanEdit => CanWrite && selectedTask != null;

    public string? Notice { get; private set; }
    public bool HasNotice => Notice != null;
    /// <summary>The separate Dismiss button is hidden when the notice's own action already is Dismiss.</summary>
    public bool ShowDismiss => NoticeActionLabel != "Dismiss";
    public string? NoticeActionLabel { get; private set; }
    public WorkspaceCommand? NoticeCommand { get; private set; }
    public WorkspaceCommand DismissNoticeCommand { get; private set; } = null!;

    public Func<Task>? PromptNewTask { get; set; }
    public Func<Task>? PromptAssign { get; set; }
    public Func<Task>? PromptAddPrerequisite { get; set; }
    public Func<Task>? PromptRemovePrerequisite { get; set; }

    public WorkspaceCommand NewTaskCommand { get; private set; } = null!;
    public WorkspaceCommand AssignCommand { get; private set; } = null!;
    public WorkspaceCommand AddPrerequisiteCommand { get; private set; } = null!;
    public WorkspaceCommand RemovePrerequisiteCommand { get; private set; } = null!;
    /// <summary>Parameter: the target column as "open", "in_progress", "done" or "cancelled".</summary>
    public WorkspaceCommand MoveTaskCommand { get; private set; } = null!;

    private static string ColumnOf(string status) => status switch { "in progress" => "In progress", "done" => "Done", "cancelled" => "Cancelled", _ => "Open" };

    private void InitEditing()
    {
        DismissNoticeCommand = new(_ => Notice != null, _ => { ClearNotice(); return Task.CompletedTask; });
        NewTaskCommand = new(_ => CanWrite && selected != null && PromptNewTask != null, _ => PromptNewTask!());
        AssignCommand = new(_ => CanEdit && PromptAssign != null, _ => PromptAssign!());
        AddPrerequisiteCommand = new(_ => CanEdit && PromptAddPrerequisite != null && PrerequisiteCandidates().Count > 0, _ => PromptAddPrerequisite!());
        RemovePrerequisiteCommand = new(_ => CanEdit && PromptRemovePrerequisite != null && selectedTask!.Dependencies.Count > 0, _ => PromptRemovePrerequisite!());
        MoveTaskCommand = new(p => CanEdit && p is string s && s is "open" or "in_progress" or "done" or "cancelled" && ColumnOf(selectedTask!.Status) != ColumnOf(((string)p).Replace('_', ' ')),
            p => MoveAsync((string)p!));
        commands = [.. commands, DismissNoticeCommand, NewTaskCommand, AssignCommand, AddPrerequisiteCommand, RemovePrerequisiteCommand, MoveTaskCommand];
    }

    private void RefreshColumns()
    {
        steady.Update([.. Tasks.Select(t => t.Id)]);
        var shown = ShownTasks.ToList();
        OpenTasks = shown.Where(t => ColumnOf(t.Status) == "Open").ToList();
        ActiveTasks = shown.Where(t => ColumnOf(t.Status) == "In progress").ToList();
        DoneTasks = shown.Where(t => ColumnOf(t.Status) == "Done").ToList();
        CancelledTasks = shown.Where(t => ColumnOf(t.Status) == "Cancelled").ToList();
        Notify(nameof(PendingNote)); Notify(nameof(HasPending)); Notify(nameof(FilterLine));
        foreach (var n in new[] { nameof(OpenTasks), nameof(ActiveTasks), nameof(DoneTasks), nameof(CancelledTasks),
                                  nameof(CancelledColumnWidth), nameof(OpenHeader), nameof(ActiveHeader), nameof(DoneHeader), nameof(CancelledHeader) })
            Notify(n);
    }

    private void ShowNotice(string message, string actionLabel, WorkspaceCommand action)
    {
        (Notice, NoticeActionLabel, NoticeCommand) = (message, actionLabel, action);
        Notify(nameof(Notice)); Notify(nameof(HasNotice)); Notify(nameof(NoticeActionLabel)); Notify(nameof(NoticeCommand)); Notify(nameof(ShowDismiss));
        RaiseCommands();
    }

    private void ClearNotice()
    {
        if (Notice == null) return;
        (Notice, NoticeActionLabel, NoticeCommand) = (null, null, null);
        Notify(nameof(Notice)); Notify(nameof(HasNotice)); Notify(nameof(NoticeActionLabel)); Notify(nameof(NoticeCommand)); Notify(nameof(ShowDismiss));
        RaiseCommands();
    }

    /// <summary>Sends one command, then reloads so the board shows what is stored. Returns the error, or null when it worked.</summary>
    private async Task<WorkerException?> Execute(Func<Task<CommandResult>> call, string workspace, bool selectResult = false)
    {
        try
        {
            var result = await call();
            // The request belonged to `workspace`. Reload only if that is still what this window shows.
            if (path == workspace)
            {
                if (selectResult) nextSelectId = result.Id;
                ClearNotice();
                await OpenAsync(workspace, workspaceName);
            }
            return null;
        }
        catch (WorkerException e) { return e; }
    }

    private async Task MoveAsync(string target)
    {
        var task = selectedTask!;
        string column = ColumnOf(task.Status);
        var local = MoveEligibility.Evaluate(task, target.Replace('_', ' '));
        if (!local.Allowed)
        {
            ShowNotice($"{local.Reason} “{task.Title}” stays in {column}.", "Go to blocker", GoToBlockerCommand);
            return;
        }
        string commandId = Guid.NewGuid().ToString("N"); // a retry of this same move reuses the ID, so it cannot apply twice
        string ws = path!;
        await Attempt();

        async Task Attempt()
        {
            if (path != ws)
            {
                // A retry from another workspace never writes: ask what happened to the original move, read-only.
                if (await SettleAway(ws, commandId, uncertain: true) is { } message) ShowNotice(message, "Dismiss", DismissNoticeCommand);
                return;
            }
            var e = await Execute(() => workspaces!.MoveTaskAsync(ws, commandId, task.Id, target, task.Revision), ws);
            if (e == null) return;
            switch (e.Error.Code)
            {
                case "blocked":
                    ShowNotice($"{e.Message} “{task.Title}” stays in {column}.", "Go to blocker", GoToBlockerCommand);
                    break;
                case "conflict":
                    ShowNotice($"{e.Message} “{task.Title}” stays in {column} until you reload.", "Reload board", RefreshCommand);
                    break;
                case "outcome_unknown":
                    ShowNotice(e.Message, "Try again", new(_ => true, _ => Attempt()));
                    break;
                default:
                    ShowNotice($"{e.Message} “{task.Title}” stays in {column}.", "Dismiss", DismissNoticeCommand);
                    break;
            }
        }
    }

    public IReadOnlyList<(string Id, string Label)> PrerequisiteCandidates() =>
        selectedTask is not { } t ? [] : [.. Tasks.Where(o => o.Id != t.Id && t.Dependencies.All(d => d.Id != o.Id)).Select(o => (o.Id, $"{o.Title} ({o.Status})"))];

    public IReadOnlyList<(string Id, string Label)> CurrentPrerequisites() =>
        selectedTask is not { } t ? [] : [.. t.Dependencies.Select(d => (d.Id, $"{d.Title} ({d.StatusLabel})"))];

    // One logical submission = one command ID. Resubmitting identical input after an error reuses it, so a
    // save whose answer was lost cannot create a second task. A changed request first asks the worker what happened.
    private sealed record Pending(string Key, string CommandId, bool Uncertain, string Workspace);
    private Pending? pending;

    private static bool IsUncertain(string code) => code is "outcome_unknown" or "worker_exited" or "protocol_error";

    /// <summary>
    /// The user has left this workspace, and this window only owns the one it shows. Never write to the other one:
    /// settle an uncertain earlier write with the read-only status lookup, and otherwise send nothing. Null means it is settled.
    /// </summary>
    private async Task<string?> SettleAway(string workspace, string? commandId, bool uncertain)
    {
        bool applied = false;
        if (uncertain && commandId != null)
        {
            try { applied = await workspaces!.CommandStatusAsync(workspace, commandId) != null; }
            catch (WorkerException e) { return $"{e.Message} The earlier change may not have been recorded, so nothing was sent."; }
        }
        if (applied) { ClearNotice(); return null; }
        return "That workspace is no longer open in this window, so nothing was sent. Reopen it, check the board, then make the change again.";
    }

    private async Task<string?> Submit(string key, string workspace, Func<string, Task<CommandResult>> call, bool selectResult = false)
    {
        if (pending is { } old && old.Key != key)
        {
            if (old.Uncertain)
            {
                try
                {
                    // Applied: reload so the board shows it before the changed request goes out.
                    if (await workspaces!.CommandStatusAsync(old.Workspace, old.CommandId) != null && path == old.Workspace) await OpenAsync(old.Workspace, workspaceName);
                }
                catch (WorkerException e) { return $"{e.Message} Your earlier change may not have been recorded yet, so this one was not sent."; }
            }
            pending = null;
        }
        if (path != workspace)
        {
            bool uncertain = pending is { Uncertain: true } p && p.Key == key;
            string? commandId = pending?.CommandId;
            pending = null;
            return await SettleAway(workspace, commandId, uncertain);
        }
        string id = pending?.Key == key ? pending.CommandId : Guid.NewGuid().ToString("N");
        pending = new(key, id, false, workspace);
        var error = await Execute(() => call(id), workspace, selectResult);
        if (error == null) { pending = null; return null; }
        pending = IsUncertain(error.Error.Code) ? pending with { Uncertain = true } : null;
        return error.Message;
    }

    /// <summary>Dialog entry points: each returns the message to show in the dialog, or null when the change was saved.</summary>
    public Task<string?> CreateTaskAsync(string title, string outcome)
    {
        string ws = path!;
        string project = selected!.Id;
        return Submit($"create|{ws}|{project}|{title.Trim()}|{outcome.Trim()}", ws, id => workspaces!.CreateTaskAsync(ws, id, project, title, outcome), selectResult: true);
    }

    public Task<string?> AssignAsync(string owner)
    {
        string ws = path!;
        var t = selectedTask!;
        return Submit($"assign|{ws}|{t.Id}|{t.Revision}|{owner.Trim()}", ws, id => workspaces!.UpdateTaskAsync(ws, id, t.Id, t.Revision, owner: owner));
    }

    public Task<string?> AddPrerequisiteAsync(string requiresId)
    {
        string ws = path!;
        var t = selectedTask!;
        return Submit($"add|{ws}|{t.Id}|{t.Revision}|{requiresId}", ws, id => workspaces!.AddDependencyAsync(ws, id, t.Id, requiresId, t.Revision));
    }

    public Task<string?> RemovePrerequisiteAsync(string requiresId)
    {
        string ws = path!;
        var t = selectedTask!;
        return Submit($"remove|{ws}|{t.Id}|{t.Revision}|{requiresId}", ws, id => workspaces!.RemoveDependencyAsync(ws, id, t.Id, requiresId, t.Revision));
    }
}
