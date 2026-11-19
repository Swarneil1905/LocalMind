using LocalMind.AgentHub.Core;

namespace LocalMind.AgentHub;

/// <summary>The only way the app changes workspace files: every call goes through the worker.</summary>
public sealed class WorkspaceService(Func<Task<WorkerClient>> connect)
{
    public WorkspaceService(WorkerClient client) : this(() => Task.FromResult(client)) { }

    public async Task<WorkspaceInfo> CreateAsync(string folder, string name) =>
        await (await connect()).CreateWorkspaceAsync(folder, name);

    /// <summary>Null when a newer open request replaced this one.</summary>
    public async Task<WorkspaceInfo?> OpenAsync(string folder) =>
        await (await connect()).LoadWorkspaceAsync(folder);

    public async Task<WorkspaceInfo> ImportAsync(string source, string folder) =>
        await (await connect()).ImportLegacyAsync(source, folder);

    public async Task<CommandResult> CreateTaskAsync(string workspace, string commandId, string projectId, string title, string? outcome) =>
        await (await connect()).CreateTaskAsync(workspace, commandId, projectId, title, outcome);

    public async Task<CommandResult> UpdateTaskAsync(string workspace, string commandId, string taskId, int revision, string? title = null, string? outcome = null, string? owner = null) =>
        await (await connect()).UpdateTaskAsync(workspace, commandId, taskId, revision, title, outcome, owner);

    public async Task<CommandResult> AddDependencyAsync(string workspace, string commandId, string taskId, string requiresId, int revision) =>
        await (await connect()).AddDependencyAsync(workspace, commandId, taskId, requiresId, revision);

    public async Task<CommandResult> RemoveDependencyAsync(string workspace, string commandId, string taskId, string requiresId, int revision) =>
        await (await connect()).RemoveDependencyAsync(workspace, commandId, taskId, requiresId, revision);

    public async Task<CommandResult> MoveTaskAsync(string workspace, string commandId, string taskId, string toState, int revision) =>
        await (await connect()).MoveTaskAsync(workspace, commandId, taskId, toState, revision);
}
