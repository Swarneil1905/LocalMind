using System.Text.Json;

namespace LocalMind.AgentHub.Core;

public static class Protocol
{
    public const int Version = 1;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>Reads can be repeated after a worker dies; anything else might have been applied already.</summary>
    public static bool IsWrite(string op) => op is not ("identify" or "loadWorkspace" or "commandStatus");
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
public sealed record UpdateTaskArgs(string Workspace, string CommandId, string TaskId, int ExpectedRevision, string? Title, string? Outcome, string? Owner);
public sealed record DependencyArgs(string Workspace, string CommandId, string TaskId, string RequiresId, int ExpectedRevision);
public sealed record MoveArgs(string Workspace, string CommandId, string TaskId, string ToState, int ExpectedRevision);
public sealed record StatusArgs(string Workspace, string CommandId);
public sealed record RoleArgs(string Workspace, string CommandId, string TaskId, string Role, string? Failure);

public sealed class WorkerException(WorkerError error) : Exception($"{error.Message} {error.Recovery}")
{
    public WorkerError Error { get; } = error;
}

internal sealed class StoreException(string code, string message, string recovery) : Exception(message)
{
    public WorkerError Error { get; } = new(code, message, recovery);
}
