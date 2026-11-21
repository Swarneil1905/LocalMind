using System.Text.Json;

namespace LocalMind.AgentHub.Core;

public static class Protocol
{
    public const int Version = 1;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>Reads can be repeated after a worker dies; anything else might have been applied already.</summary>
    public static bool IsWrite(string op) => op is not ("identify" or "loadWorkspace" or "commandStatus" or "previewDispatch" or "previewInstruction" or "probeConnection" or "readPlans" or "planStatus" or "planEstimate" or "planActuals" or "planHandoffs" or "readDocuments" or "readAnswers" or "openPassage" or "replayTimeline" or "replayState" or "runDiagnostics");
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
public sealed record DispatchArgs(string Workspace, string CommandId, string TaskId, string Role, bool AcceptPriceRisk, bool AcceptRepeatCost, string? Binding = null);
public sealed record AttemptArgs(string Workspace, string CommandId, string AttemptId, bool AcceptRepeatCost);
public sealed record SmokeArgs(string Workspace, string CommandId, string ConnectionId, string ModelId, string Prompt);
public sealed record ImageArg(string Name, string Base64);
public sealed record InstructionArgs(string Workspace, string CommandId, string TaskId, string[] Roles, string Text, Dictionary<string, string>? Bindings = null, ImageArg[]? Images = null, Dictionary<string, string>? ImageConfirms = null);
public sealed record InstructionPreviewArgs(string Workspace, string TaskId, string[] Roles, string Text, ImageArg[]? Images = null, Dictionary<string, string>? ImageConfirms = null);
public sealed record ReconcileArgs(string Workspace, string CommandId, string AttemptId, double Amount);
public sealed record PlanProposeArgs(string Workspace, string CommandId, string ProjectId, string Brief, PlanTask[]? Tasks = null, PlanEdge[]? Edges = null);
public sealed record PlanEditArgs(string Workspace, string CommandId, string PlanId, int Version, PlanTask[] Tasks, PlanEdge[] Edges);
public sealed record PlanDecideArgs(string Workspace, string CommandId, string PlanId, int Version);
public sealed record PlanArgs(string Workspace, string PlanId);
public sealed record PlanStartArgs(string Workspace, string CommandId, string PlanId, Dictionary<string, string> Bindings);
public sealed record ProjectArgs(string Workspace, string ProjectId, long After = 0);
public sealed record DocAddArgs(string Workspace, string CommandId, string ProjectId, string Name, string Text);
public sealed record DocImageArgs(string Workspace, string CommandId, string ProjectId, ImageArg Image);
public sealed record DocEditArgs(string Workspace, string CommandId, string DocId, string Text);
public sealed record DocIdArgs(string Workspace, string CommandId, string DocId);
public sealed record AskArgs(string Workspace, string CommandId, string ProjectId, string Question, string Role);
public sealed record PassageArgs(string Workspace, string DocId, int Revision, int Start, int Length);
public sealed record ForgetResult(CommandResult Result, ForgetReport Report);
public sealed record ResolveArgs(string Workspace, string CommandId, string AttemptId, string Choice, double? Amount);
public sealed record LifecycleArgs(string Workspace, string Kind);
public sealed record ShutdownArgs(string Workspace, string Policy, int MaxMs);
public sealed record RunDiagArgs(string Workspace, string? AttemptId, bool IncludeContent, string? Target);
public sealed record ReplayArgs(string Workspace, string ProjectId, long Seq);
public sealed record PackageArgs(string Workspace, string Target);
public sealed record RestoreArgs(string Package, string Target);
public sealed record EraseResult(ErasureReport Report);
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
