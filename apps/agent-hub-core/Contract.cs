using System.Text.Json;

namespace LocalMind.AgentHub.Core;

public static class Protocol
{
    public const int Version = 1;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>Reads can be repeated after a worker dies; anything else might have been applied already.</summary>
    public static bool IsWrite(string op) => op is not ("identify" or "loadWorkspace" or "commandStatus" or "previewDispatch" or "previewInstruction" or "probeConnection");
}

/// <summary>Typed failure. Recovery always names something the user can do.</summary>
public sealed record WorkerError(string Code, string Message, string Recovery);

public sealed record WorkerRequest(int Version, string RequestId, string Op, string? Token, JsonElement? Args);
public sealed record WorkerResponse(int Version, string RequestId, bool Ok, JsonElement? Result, WorkerError? Error);

public sealed record WorkerInfo(string Name, int Protocol, string ProfileId);
public sealed record WorkspaceInfo(string DatabasePath, string Name, string ProjectId, int SchemaVersion, bool Migrated);

public sealed record CreateArgs(string Folder, string Name);
public sealed record FolderArgs(string Folder);
public sealed record ImportArgs(string Source, string Folder);
public sealed record RotateArgs(string NewToken);
public sealed record CreateTaskArgs(string Workspace, string CommandId, string ProjectId, string Title, string? Outcome);
public sealed record UpdateTaskArgs(string Workspace, string CommandId, string TaskId, int ExpectedRevision, string? Title, string? Outcome, string? Owner, bool? ReviewRequired = null);
public sealed record DependencyArgs(string Workspace, string CommandId, string TaskId, string RequiresId, int ExpectedRevision);
public sealed record MoveArgs(string Workspace, string CommandId, string TaskId, string ToState, int ExpectedRevision);
public sealed record PolicyArgs(string Workspace, string CommandId, string Name, int ExpectedRevision);
public sealed record RoleTokenArgs(string Role);
public sealed record WorkspaceArgs(string Workspace);
public sealed record TokenResult(string Token);
public sealed record OwnershipResult(bool Held, int OwnerProcessId);
public sealed record DecideArgs(string Workspace, string CommandId, string TaskId, string OutputId, int ContentRevision, int PolicyRevision, string Decision, string? Comment);
public sealed record StatusArgs(string Workspace, string CommandId);
public sealed record SayArgs(string Workspace, string CommandId, string TaskId, string Kind, string Text);
public sealed record AnswerArgs(string Workspace, string CommandId, string QuestionId, string Text);
public sealed record AddConnectionArgs(string Workspace, string CommandId, string Provider, string Endpoint, string Label);
public sealed record ConnectionArgs(string Workspace, string CommandId, string ConnectionId);
public sealed record CredentialArgs(string Workspace, string CommandId, string ConnectionId, string? Secret);
public sealed record AssignArgs(string Workspace, string CommandId, string ProjectId, string Role, string ConnectionId, string ModelId);
public sealed record SettingsArgs(string Workspace, string CommandId, ProjectSettings Settings);
public sealed record ConsentArgs(string Workspace, string CommandId, string ProjectId, string ConnectionId, string ModelId, string[] Files);
public sealed record RevokeArgs(string Workspace, string CommandId, string ConsentId);
public sealed record PriceArgs(string Workspace, string CommandId, string ConnectionId, string ModelId, string Currency, double InputPerMTok, double OutputPerMTok, string Source, DateTime Effective);
public sealed record FileArgs(string Workspace, string CommandId, string TaskId, string Name, string Content);
public sealed record PreviewArgs(string Workspace, string TaskId, string Role, string? Instruction);
public sealed record DispatchArgs(string Workspace, string CommandId, string TaskId, string Role, bool AcceptPriceRisk, bool AcceptRepeatCost);
public sealed record AttemptArgs(string Workspace, string CommandId, string AttemptId, bool AcceptRepeatCost);
public sealed record SmokeArgs(string Workspace, string CommandId, string ConnectionId, string ModelId, string Prompt);
public sealed record InstructionArgs(string Workspace, string CommandId, string TaskId, string[] Roles, string Text);
public sealed record InstructionPreviewArgs(string Workspace, string TaskId, string[] Roles, string Text);
public sealed record DiagnosticsArgs(string Workspace, string Target);
public sealed record InstructionResult(CommandResult Instruction, IReadOnlyList<InstructionAttempt> Attempts);
public sealed record InstructionAttempt(string Role, CommandResult? Attempt, string? Error);
public sealed record RoleArgs(string Workspace, string CommandId, string TaskId, string Role, string? Failure);

public sealed class WorkerException(WorkerError error) : Exception($"{error.Message} {error.Recovery}")
{
    public WorkerError Error { get; } = error;
}

public sealed class StoreException(string code, string message, string recovery) : Exception(message)
{
    public WorkerError Error { get; } = new(code, message, recovery);
}
