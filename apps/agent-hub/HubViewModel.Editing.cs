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
    public bool CanEdit => IsWorkspace && selectedTask != null;

    public string? Notice { get; private set; }
    public bool HasNotice => Notice != null;
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
        NewTaskCommand = new(_ => IsWorkspace && selected != null && PromptNewTask != null, _ => PromptNewTask!());
        AssignCommand = new(_ => CanEdit && PromptAssign != null, _ => PromptAssign!());
        AddPrerequisiteCommand = new(_ => CanEdit && PromptAddPrerequisite != null && PrerequisiteCandidates().Count > 0, _ => PromptAddPrerequisite!());
        RemovePrerequisiteCommand = new(_ => CanEdit && PromptRemovePrerequisite != null && selectedTask!.Dependencies.Count > 0, _ => PromptRemovePrerequisite!());
        MoveTaskCommand = new(p => CanEdit && p is string s && s is "open" or "in_progress" or "done" or "cancelled" && ColumnOf(selectedTask!.Status) != ColumnOf(((string)p).Replace('_', ' ')),
            p => MoveAsync((string)p!));
        commands = [.. commands, DismissNoticeCommand, NewTaskCommand, AssignCommand, AddPrerequisiteCommand, RemovePrerequisiteCommand, MoveTaskCommand];
    }

    private void RefreshColumns()
    {
        OpenTasks = Tasks.Where(t => ColumnOf(t.Status) == "Open").ToList();
        ActiveTasks = Tasks.Where(t => ColumnOf(t.Status) == "In progress").ToList();
        DoneTasks = Tasks.Where(t => ColumnOf(t.Status) == "Done").ToList();
        CancelledTasks = Tasks.Where(t => ColumnOf(t.Status) == "Cancelled").ToList();
        foreach (var n in new[] { nameof(OpenTasks), nameof(ActiveTasks), nameof(DoneTasks), nameof(CancelledTasks),
                                  nameof(CancelledColumnWidth), nameof(OpenHeader), nameof(ActiveHeader), nameof(DoneHeader), nameof(CancelledHeader) })
            Notify(n);
    }

    private void ShowNotice(string message, string actionLabel, WorkspaceCommand action)
    {
        (Notice, NoticeActionLabel, NoticeCommand) = (message, actionLabel, action);
        Notify(nameof(Notice)); Notify(nameof(HasNotice)); Notify(nameof(NoticeActionLabel)); Notify(nameof(NoticeCommand));
        RaiseCommands();
    }

    private void ClearNotice()
    {
        if (Notice == null) return;
        (Notice, NoticeActionLabel, NoticeCommand) = (null, null, null);
        Notify(nameof(Notice)); Notify(nameof(HasNotice)); Notify(nameof(NoticeActionLabel)); Notify(nameof(NoticeCommand));
        RaiseCommands();
    }

    /// <summary>Sends one command, then reloads so the board shows what is stored. Returns the error, or null when it worked.</summary>
    private async Task<WorkerException?> Execute(Func<Task<CommandResult>> call, bool selectResult = false)
    {
        try
        {
            var result = await call();
            if (selectResult) nextSelectId = result.Id;
            ClearNotice();
            await OpenAsync(path!, workspaceName);
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
        await Attempt();

        async Task Attempt()
        {
            var e = await Execute(() => workspaces!.MoveTaskAsync(path!, commandId, task.Id, target, task.Revision));
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
    private sealed record Pending(string Key, string CommandId, bool Uncertain);
    private Pending? pending;

    private static bool IsUncertain(string code) => code is "outcome_unknown" or "worker_exited" or "protocol_error";

    private async Task<string?> Submit(string key, Func<string, Task<CommandResult>> call, bool selectResult = false)
    {
        if (pending is { } old && old.Key != key)
        {
            if (old.Uncertain)
            {
                try
                {
                    // Applied: reload so the board shows it before the changed request goes out.
                    if (await workspaces!.CommandStatusAsync(path!, old.CommandId) != null) await OpenAsync(path!, workspaceName);
                }
                catch (WorkerException e) { return $"{e.Message} Your earlier change may not have been recorded yet, so this one was not sent."; }
            }
            pending = null;
        }
        string id = pending?.Key == key ? pending.CommandId : Guid.NewGuid().ToString("N");
        pending = new(key, id, false);
        var error = await Execute(() => call(id), selectResult);
        if (error == null) { pending = null; return null; }
        pending = IsUncertain(error.Error.Code) ? pending with { Uncertain = true } : null;
        return error.Message;
    }

    /// <summary>Dialog entry points: each returns the message to show in the dialog, or null when the change was saved.</summary>
    public Task<string?> CreateTaskAsync(string title, string outcome) =>
        Submit($"create|{path}|{selected?.Id}|{title.Trim()}|{outcome.Trim()}",
            id => workspaces!.CreateTaskAsync(path!, id, selected!.Id, title, outcome), selectResult: true);

    public Task<string?> AssignAsync(string owner)
    {
        var t = selectedTask!;
        return Submit($"assign|{path}|{t.Id}|{t.Revision}|{owner.Trim()}", id => workspaces!.UpdateTaskAsync(path!, id, t.Id, t.Revision, owner: owner));
    }

    public Task<string?> AddPrerequisiteAsync(string requiresId)
    {
        var t = selectedTask!;
        return Submit($"add|{path}|{t.Id}|{t.Revision}|{requiresId}", id => workspaces!.AddDependencyAsync(path!, id, t.Id, requiresId, t.Revision));
    }

    public Task<string?> RemovePrerequisiteAsync(string requiresId)
    {
        var t = selectedTask!;
        return Submit($"remove|{path}|{t.Id}|{t.Revision}|{requiresId}", id => workspaces!.RemoveDependencyAsync(path!, id, t.Id, requiresId, t.Revision));
    }
}
