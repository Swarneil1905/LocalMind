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

    public async Task<CommandResult> UpdateTaskAsync(string workspace, string commandId, string taskId, int revision, string? title = null, string? outcome = null, string? owner = null, bool? reviewRequired = null) =>
        await (await connect()).UpdateTaskAsync(workspace, commandId, taskId, revision, title, outcome, owner, reviewRequired);

    public async Task<CommandResult> AddDependencyAsync(string workspace, string commandId, string taskId, string requiresId, int revision) =>
        await (await connect()).AddDependencyAsync(workspace, commandId, taskId, requiresId, revision);

    public async Task<CommandResult> RemoveDependencyAsync(string workspace, string commandId, string taskId, string requiresId, int revision) =>
        await (await connect()).RemoveDependencyAsync(workspace, commandId, taskId, requiresId, revision);

    public async Task<CommandResult> MoveTaskAsync(string workspace, string commandId, string taskId, string toState, int revision) =>
        await (await connect()).MoveTaskAsync(workspace, commandId, taskId, toState, revision);

    public async Task<CommandResult?> CommandStatusAsync(string workspace, string commandId) =>
        await (await connect()).CommandStatusAsync(workspace, commandId);

    public async Task<CommandResult> RunRoleAsync(string workspace, string commandId, string taskId, string role) =>
        await (await connect()).RunRoleAsync(workspace, commandId, taskId, role);

    public async Task<CommandResult> DecideOutputAsync(string workspace, string submissionId, string taskId, string outputId, int contentRevision, int policyRevision,
                                                       string decision, string? comment) =>
        await (await connect()).DecideOutputAsync(workspace, submissionId, taskId, outputId, contentRevision, policyRevision, decision, comment);

    public async Task<CommandResult> SetPolicyAsync(string workspace, string commandId, string name, int expectedRevision) =>
        await (await connect()).SetPolicyAsync(workspace, commandId, name, expectedRevision);

    public async Task<CommandResult> FixtureSayAsync(string workspace, string commandId, string taskId, string kind, string text) =>
        await (await connect()).FixtureSayAsync(workspace, commandId, taskId, kind, text);

    public async Task<CommandResult> AnswerQuestionAsync(string workspace, string commandId, string questionId, string text) =>
        await (await connect()).AnswerQuestionAsync(workspace, commandId, questionId, text);

    public async Task<OwnershipResult> AcquireAsync(string workspace) => await (await connect()).AcquireWorkspaceAsync(workspace);

    public async Task<OwnershipResult> ReleaseAsync(string workspace) => await (await connect()).ReleaseWorkspaceAsync(workspace);

    // Models, keys and runs. Every call goes through the worker; the app never holds a key after handing it over.
    public async Task<WorkerClient> Client() => await connect();
}
