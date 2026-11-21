using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalMind.AgentHub.Core;

/// <summary>
/// Runs inside the worker. Who is calling comes from the credential, never from the request body: the owner's credential is the app's,
/// and a fixture role gets a credential of its own that can run that role and, where the policy allows, decide. Writes need this process
/// to own the workspace.
/// </summary>
public sealed class WorkerHost(string profileId, string token, Func<string> newId, ICredentialVault? vaultOverride = null) : IDisposable
{
    private const string Owner = "owner";
    private byte[] ownerHash = Hash(token);
    private readonly List<(byte[] Hash, string Identity)> roleTokens = [];
    private readonly Dictionary<string, WorkspaceLock> locks = new(StringComparer.OrdinalIgnoreCase);

    public WorkerInfo Info => new("LocalMind.AgentHub.Worker", Protocol.Version, profileId);

    private static byte[] Hash(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));

    private string? Caller(string? presented)
    {
        if (presented is null) return null;
        var h = Hash(presented);
        if (CryptographicOperations.FixedTimeEquals(ownerHash, h)) return Owner;
        foreach (var (roleHash, identity) in roleTokens)
            if (CryptographicOperations.FixedTimeEquals(roleHash, h)) return identity;
        return null;
    }

    private static string FolderOf(string workspace) => Path.GetDirectoryName(ProjectStore.DatabaseIn(Path.GetFullPath(workspace)))!;

    /// <summary>Holds the workspace for this process or says who does. Every write goes through here.</summary>
    private void RequireOwnership(string workspace)
    {
        var o = Acquire(workspace);
        if (!o.Held)
            throw new StoreException("workspace_owned", $"This workspace is open for editing in another Agent Hub window (process {o.OwnerProcessId}).",
                "Close that window, then try again. This window can still read the board.");
    }

    private OwnershipResult Acquire(string workspace)
    {
        string folder = FolderOf(workspace);
        if (locks.TryGetValue(folder, out var held) && held.Held) return new(true, Environment.ProcessId);
        var attempt = WorkspaceLock.TryAcquire(folder);
        if (attempt.Held) locks[folder] = attempt;
        return new(attempt.Held, attempt.OwnerProcessId);
    }

    public WorkerResponse Handle(WorkerRequest request, CancellationToken ct = default)
    {
        if (request.Version != Protocol.Version)
            return Fail(request, new("incompatible_version", $"The app speaks protocol {request.Version}; the worker speaks {Protocol.Version}.",
                "Update LocalMind Agent Hub so the app and its worker come from the same build, then restart it."));
        string? who = Caller(request.Token);
        if (who is null)
            return Fail(request, new("unauthorized", request.Token is null ? "The request carried no credentials." : "The request credentials were not accepted.",
                "Restart Agent Hub so it can start a new worker with fresh credentials."));
        try
        {
            if (who != Owner && request.Op is not ("identify" or "commandStatus" or "runRole" or "decideOutput" or "fixtureSay"))
                throw new StoreException("not_authorized", $"{who} cannot do “{request.Op}”.", "Only the workspace owner can.");
            object result = request.Op switch
            {
                "identify" => Info,
                "loadWorkspace" => Load(Arg<FolderArgs>(request).Folder),
                "createWorkspace" => Created(ProjectStore.Create(Arg<CreateArgs>(request).Folder, Arg<CreateArgs>(request).Name, newId)),
                "importLegacy" => ProjectStore.Import(Arg<ImportArgs>(request).Source, Arg<ImportArgs>(request).Folder, ct, newId),
                "rotate" => Rotate(Arg<RotateArgs>(request).NewToken),
                "issueRoleToken" => IssueRoleToken(Arg<RoleTokenArgs>(request).Role),
                "acquireWorkspace" => Acquire(Arg<WorkspaceArgs>(request).Workspace),
                "releaseWorkspace" => Release(Arg<WorkspaceArgs>(request).Workspace),
                "commandStatus" => TaskStore.CommandStatus(Arg<StatusArgs>(request).Workspace, Arg<StatusArgs>(request).CommandId) ?? (object)new { },
                "createTask" => Write(Arg<CreateTaskArgs>(request).Workspace, () => TaskStore.CreateTask(Arg<CreateTaskArgs>(request).Workspace, Arg<CreateTaskArgs>(request).CommandId, Arg<CreateTaskArgs>(request).ProjectId, Arg<CreateTaskArgs>(request).Title, Arg<CreateTaskArgs>(request).Outcome, newId)),
                "updateTask" => Write(Arg<UpdateTaskArgs>(request).Workspace, () => TaskStore.UpdateTask(Arg<UpdateTaskArgs>(request).Workspace, Arg<UpdateTaskArgs>(request).CommandId, Arg<UpdateTaskArgs>(request).TaskId, Arg<UpdateTaskArgs>(request).ExpectedRevision, Arg<UpdateTaskArgs>(request).Title, Arg<UpdateTaskArgs>(request).Outcome, Arg<UpdateTaskArgs>(request).Owner, Arg<UpdateTaskArgs>(request).ReviewRequired)),
                "addDependency" => Write(Arg<DependencyArgs>(request).Workspace, () => TaskStore.AddDependency(Arg<DependencyArgs>(request).Workspace, Arg<DependencyArgs>(request).CommandId, Arg<DependencyArgs>(request).TaskId, Arg<DependencyArgs>(request).RequiresId, Arg<DependencyArgs>(request).ExpectedRevision)),
                "removeDependency" => Write(Arg<DependencyArgs>(request).Workspace, () => TaskStore.RemoveDependency(Arg<DependencyArgs>(request).Workspace, Arg<DependencyArgs>(request).CommandId, Arg<DependencyArgs>(request).TaskId, Arg<DependencyArgs>(request).RequiresId, Arg<DependencyArgs>(request).ExpectedRevision)),
                "moveTask" => Write(Arg<MoveArgs>(request).Workspace, () => TaskStore.MoveTask(Arg<MoveArgs>(request).Workspace, Arg<MoveArgs>(request).CommandId, Arg<MoveArgs>(request).TaskId, Arg<MoveArgs>(request).ToState, Arg<MoveArgs>(request).ExpectedRevision)),
                "setPolicy" => Write(Arg<PolicyArgs>(request).Workspace, () => TaskStore.SetPolicy(Arg<PolicyArgs>(request).Workspace, Arg<PolicyArgs>(request).CommandId, Arg<PolicyArgs>(request).Name, Arg<PolicyArgs>(request).ExpectedRevision, who)),
                "decideOutput" => Write(Arg<DecideArgs>(request).Workspace, () => TaskStore.DecideOutput(Arg<DecideArgs>(request).Workspace, Arg<DecideArgs>(request).CommandId, Arg<DecideArgs>(request).TaskId, Arg<DecideArgs>(request).OutputId, Arg<DecideArgs>(request).ContentRevision, Arg<DecideArgs>(request).PolicyRevision, Arg<DecideArgs>(request).Decision, Arg<DecideArgs>(request).Comment, who, newId)),
                "runRole" => Write(Arg<RoleArgs>(request).Workspace, () =>
                {
                    var a = Arg<RoleArgs>(request);
                    if (who != Owner && !who.StartsWith(a.Role))
                        throw new StoreException("not_authorized", $"{who} cannot run the {a.Role} role.", "Each fixture role runs only as itself.");
                    return TaskStore.RunRole(a.Workspace, a.CommandId, a.TaskId, a.Role, a.Failure, newId);
                }),
                "fixtureSay" => Write(Arg<SayArgs>(request).Workspace, () =>
                {
                    var a = Arg<SayArgs>(request);
                    if (who != Owner && !who.StartsWith("builder"))
                        throw new StoreException("not_authorized", $"{who} cannot speak for the builder fixture.", "Only the builder fixture or the owner can.");
                    return TaskStore.FixtureSay(a.Workspace, a.CommandId, a.TaskId, a.Kind, a.Text, newId);
                }),
                "answerQuestion" => Write(Arg<AnswerArgs>(request).Workspace, () => TaskStore.AnswerQuestion(Arg<AnswerArgs>(request).Workspace, Arg<AnswerArgs>(request).CommandId, Arg<AnswerArgs>(request).QuestionId, Arg<AnswerArgs>(request).Text, who)),
                "addConnection" => Write(Arg<AddConnectionArgs>(request).Workspace, () => { var a = Arg<AddConnectionArgs>(request); return Team.AddConnection(a.Workspace, a.CommandId, a.Provider, a.Endpoint, a.Label, newId); }),
                "removeConnection" => Write(Arg<ConnectionArgs>(request).Workspace, () => { var a = Arg<ConnectionArgs>(request); DropSecret(a.Workspace, a.ConnectionId); return Team.RemoveConnection(a.Workspace, a.CommandId, a.ConnectionId); }),
                "probeConnection" => Probe(Arg<ConnectionArgs>(request)),
                "setCredential" => Write(Arg<CredentialArgs>(request).Workspace, () => Credential(Arg<CredentialArgs>(request))),
                "assignRole" => Write(Arg<AssignArgs>(request).Workspace, () => { var a = Arg<AssignArgs>(request); return Team.SetAssignment(a.Workspace, a.CommandId, a.ProjectId, a.Role, a.ConnectionId, a.ModelId); }),
                "setProjectSettings" => Write(Arg<SettingsArgs>(request).Workspace, () => { var a = Arg<SettingsArgs>(request); return Team.SetProjectSettings(a.Workspace, a.CommandId, a.Settings); }),
                "grantConsent" => Write(Arg<ConsentArgs>(request).Workspace, () => { var a = Arg<ConsentArgs>(request); return Team.GrantConsent(a.Workspace, a.CommandId, a.ProjectId, a.ConnectionId, a.ModelId, a.Files, newId); }),
                "revokeConsent" => Write(Arg<RevokeArgs>(request).Workspace, () => { var a = Arg<RevokeArgs>(request); return Team.RevokeConsent(a.Workspace, a.CommandId, a.ConsentId); }),
                "setPrice" => Write(Arg<PriceArgs>(request).Workspace, () => { var a = Arg<PriceArgs>(request); return Team.SetPrice(a.Workspace, a.CommandId, a.ConnectionId, a.ModelId, a.Currency, a.InputPerMTok, a.OutputPerMTok, a.Source, a.Effective, newId); }),
                "addTaskFile" => Write(Arg<FileArgs>(request).Workspace, () => { var a = Arg<FileArgs>(request); return Team.AddTaskFile(a.Workspace, a.CommandId, a.TaskId, a.Name, a.Content); }),
                "previewDispatch" => Engine.Preview(Arg<PreviewArgs>(request).Workspace, Arg<PreviewArgs>(request).TaskId, Arg<PreviewArgs>(request).Role, Arg<PreviewArgs>(request).Instruction),
                "dispatch" => Write(Arg<DispatchArgs>(request).Workspace, () => { var a = Arg<DispatchArgs>(request); return Engine.Dispatch(a.Workspace, a.CommandId, a.TaskId, a.Role, new(a.AcceptPriceRisk, a.AcceptRepeatCost, Binding: a.Binding ?? throw NoPreview())); }),
                "reconcileAttempt" => Write(Arg<ReconcileArgs>(request).Workspace, () => { var a = Arg<ReconcileArgs>(request); return Engine.Reconcile(a.Workspace, a.CommandId, a.AttemptId, a.Amount); }),
                "cancelAttempt" => Write(Arg<AttemptArgs>(request).Workspace, () => { var a = Arg<AttemptArgs>(request); return Engine.Cancel(a.Workspace, a.CommandId, a.AttemptId); }),
                "pauseAttempt" => Write(Arg<AttemptArgs>(request).Workspace, () => { var a = Arg<AttemptArgs>(request); return Engine.Pause(a.Workspace, a.CommandId, a.AttemptId); }),
                "resumeAttempt" => Write(Arg<AttemptArgs>(request).Workspace, () => { var a = Arg<AttemptArgs>(request); return Engine.Resume(a.Workspace, a.CommandId, a.AttemptId, a.AcceptRepeatCost); }),
                "smokeTest" => Write(Arg<SmokeArgs>(request).Workspace, () => { var a = Arg<SmokeArgs>(request); return Engine.SmokeTest(a.Workspace, a.CommandId, a.ConnectionId, a.ModelId, a.Prompt); }),
                "previewInstruction" => Engine.PreviewInstruction(Arg<InstructionPreviewArgs>(request).Workspace, Arg<InstructionPreviewArgs>(request).TaskId, Arg<InstructionPreviewArgs>(request).Roles, Arg<InstructionPreviewArgs>(request).Text, ReadImages(Arg<InstructionPreviewArgs>(request).Images), Arg<InstructionPreviewArgs>(request).ImageConfirms),
                "sendInstruction" => Write(Arg<InstructionArgs>(request).Workspace, () =>
                {
                    var a = Arg<InstructionArgs>(request);
                    var (ins, attempts) = Engine.SendInstruction(a.Workspace, a.CommandId, a.TaskId, a.Roles, a.Text, a.Bindings ?? throw NoPreview(), ReadImages(a.Images), a.ImageConfirms);
                    return new InstructionResult(ins, [.. attempts.Select(x => new InstructionAttempt(x.Role, x.Attempt, x.Error))]);
                }),
                "proposePlan" => Write(Arg<PlanProposeArgs>(request).Workspace, () => { var a = Arg<PlanProposeArgs>(request); return Engine.ProposePlan(a.Workspace, a.CommandId, a.ProjectId, a.Brief, a.Tasks, a.Edges); }),
                "editPlan" => Write(Arg<PlanEditArgs>(request).Workspace, () => { var a = Arg<PlanEditArgs>(request); return Plans.Edit(a.Workspace, a.CommandId, a.PlanId, a.Version, a.Tasks, a.Edges); }),
                "approvePlan" => Write(Arg<PlanDecideArgs>(request).Workspace, () => { var a = Arg<PlanDecideArgs>(request); return Plans.Approve(a.Workspace, a.CommandId, a.PlanId, a.Version, newId); }),
                "dismissPlan" => Write(Arg<PlanDecideArgs>(request).Workspace, () => { var a = Arg<PlanDecideArgs>(request); return Plans.Dismiss(a.Workspace, a.CommandId, a.PlanId, a.Version); }),
                "startPlan" => Write(Arg<PlanStartArgs>(request).Workspace, () => { var a = Arg<PlanStartArgs>(request); return Engine.StartPlan(a.Workspace, a.CommandId, a.PlanId, a.Bindings); }),
                "readPlans" => Plans.ForProject(Arg<ProjectArgs>(request).Workspace, Arg<ProjectArgs>(request).ProjectId),
                "planStatus" => Plans.Status(Arg<PlanArgs>(request).Workspace, Arg<PlanArgs>(request).PlanId),
                "planEstimate" => Engine.Estimate(Arg<PlanArgs>(request).Workspace, Arg<PlanArgs>(request).PlanId),
                "planActuals" => Plans.Actuals(Arg<PlanArgs>(request).Workspace, Arg<PlanArgs>(request).PlanId),
                "planHandoffs" => Plans.Handoffs(Arg<ProjectArgs>(request).Workspace, Arg<ProjectArgs>(request).ProjectId, Arg<ProjectArgs>(request).After),
                "addDocument" => Write(Arg<DocAddArgs>(request).Workspace, () => { var a = Arg<DocAddArgs>(request); return Documents.Add(a.Workspace, a.CommandId, a.ProjectId, a.Name, a.Text, newId); }),
                "addDocumentImage" => Write(Arg<DocImageArgs>(request).Workspace, () => { var a = Arg<DocImageArgs>(request); return Documents.AddImage(a.Workspace, a.CommandId, a.ProjectId, ReadImages([a.Image])[0], newId); }),
                "editDocument" => Write(Arg<DocEditArgs>(request).Workspace, () => { var a = Arg<DocEditArgs>(request); return Documents.Edit(a.Workspace, a.CommandId, a.DocId, a.Text); }),
                "removeDocument" => Write(Arg<DocIdArgs>(request).Workspace, () => { var a = Arg<DocIdArgs>(request); return Documents.Remove(a.Workspace, a.CommandId, a.DocId); }),
                "forgetSource" => Write(Arg<DocIdArgs>(request).Workspace, () => { var a = Arg<DocIdArgs>(request); var (r, rep) = Documents.Forget(a.Workspace, a.CommandId, a.DocId); return new ForgetResult(r, rep); }),
                "askDocuments" => Write(Arg<AskArgs>(request).Workspace, () => { var a = Arg<AskArgs>(request); return Engine.AskDocuments(a.Workspace, a.CommandId, a.ProjectId, a.Question, a.Role); }),
                "readDocuments" => Documents.List(Arg<ProjectArgs>(request).Workspace, Arg<ProjectArgs>(request).ProjectId),
                "readAnswers" => Documents.Answers(Arg<ProjectArgs>(request).Workspace, Arg<ProjectArgs>(request).ProjectId),
                "openPassage" => Documents.Open(Arg<PassageArgs>(request).Workspace, Arg<PassageArgs>(request).DocId, Arg<PassageArgs>(request).Revision, Arg<PassageArgs>(request).Start, Arg<PassageArgs>(request).Length),
                "resolveAttempt" => Write(Arg<ResolveArgs>(request).Workspace, () => { var a = Arg<ResolveArgs>(request); return Engine.ResolveAttempt(a.Workspace, a.CommandId, a.AttemptId, a.Choice, a.Amount); }),
                "lifecycle" => Write(Arg<LifecycleArgs>(request).Workspace, () => { var a = Arg<LifecycleArgs>(request); return a.Kind == "sleep" ? new WakeReport(Engine.OnSleep(a.Workspace), []) : Engine.OnWake(a.Workspace); }),
                "shutdown" => Write(Arg<ShutdownArgs>(request).Workspace, () => { var a = Arg<ShutdownArgs>(request); return Engine.Shutdown(a.Workspace, a.Policy, a.MaxMs); }),
                "replayTimeline" => Replay.Timeline(Arg<ReplayArgs>(request).Workspace, Arg<ReplayArgs>(request).ProjectId),
                "replayState" => Replay.StateAt(Arg<ReplayArgs>(request).Workspace, Arg<ReplayArgs>(request).ProjectId, Arg<ReplayArgs>(request).Seq),
                "runDiagnostics" => RunDiagnostics.ToText(RunDiagnostics.Build(Arg<RunDiagArgs>(request).Workspace, Arg<RunDiagArgs>(request).AttemptId, Arg<RunDiagArgs>(request).IncludeContent)),
                "exportRunDiagnostics" => new { path = RunDiagnostics.Export(Arg<RunDiagArgs>(request).Workspace, Arg<RunDiagArgs>(request).AttemptId, Arg<RunDiagArgs>(request).IncludeContent, Arg<RunDiagArgs>(request).Target ?? throw Bad()) },
                "exportWorkspace" => Portability.Export(Arg<PackageArgs>(request).Workspace, Arg<PackageArgs>(request).Target),
                "restoreWorkspace" => Portability.Restore(Arg<RestoreArgs>(request).Package, Arg<RestoreArgs>(request).Target, newId),
                "eraseWorkspace" => Write(Arg<WorkspaceArgs>(request).Workspace, () => EraseWorkspace(Arg<WorkspaceArgs>(request).Workspace)),
                "recoverRuns" => Write(Arg<WorkspaceArgs>(request).Workspace, () => new { recovered = Engine.Recover(Arg<WorkspaceArgs>(request).Workspace) }),
                "exportDiagnostics" => Diagnostics(Arg<DiagnosticsArgs>(request)),
                _ => throw new StoreException("unknown_operation", $"The worker does not know “{request.Op}”.", "Update LocalMind Agent Hub so the app and worker match."),
            };
            return new(Protocol.Version, request.RequestId, true, JsonSerializer.SerializeToElement(result, Protocol.Json), null);
        }
        catch (StoreException e) { return Fail(request, e.Error); }
        catch (OperationCanceledException) { return Fail(request, new("cancelled", "The operation was cancelled.", "Run it again when you are ready; nothing was changed.")); }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Fail(request, new("store_error", "The workspace could not be read or written.", "Check the folder is writable and the file is not open elsewhere, then try again."));
        }
    }

    // --- Models, keys and runs ---------------------------------------------------------------------------------
    private RunEngine? engine;
    private RunEngine Engine => engine ??= new RunEngine(vault, newId);
    private readonly ICredentialVault vault = vaultOverride ?? Vaults.ForPlatform();

    /// <summary>The vault reference for one connection's key, unique to this profile.</summary>
    private string SecretRef(string connectionId) => $"LocalMind.AgentHub/{profileId}/{connectionId}";

    private object Probe(ConnectionArgs a)
    {
        var models = Engine.Probe(a.Workspace, a.ConnectionId, CancellationToken.None).GetAwaiter().GetResult();
        return new { models = models.Count };
    }

    /// <summary>The key goes into the vault and never into the store, the events or any reply; the store records only where it is and its status.</summary>
    private object Credential(CredentialArgs a)
    {
        string reference = SecretRef(a.ConnectionId);
        string status;
        try
        {
            if (string.IsNullOrEmpty(a.Secret)) { vault.Delete(reference); status = "deleted"; }
            else
            {
                if (a.Secret.Length > 512 || a.Secret.Any(char.IsWhiteSpace)) throw new StoreException("invalid_key", "That does not look like an API key.", "Paste the key again, without spaces.");
                bool had = vault.Read(reference) != null;
                vault.Write(reference, a.Secret);
                status = had ? "rotated" : "stored";
            }
        }
        catch (VaultUnavailableException e) { throw new StoreException("vault_unavailable", e.Message, "Unlock the credential store, then try again."); }
        return Team.RecordCredential(a.Workspace, a.CommandId, a.ConnectionId, status, status == "deleted" ? null : reference);
    }

    private void DropSecret(string workspace, string connectionId)
    {
        try { vault.Delete(SecretRef(connectionId)); } catch (VaultUnavailableException) { }
    }

    /// <summary>A diagnostics file for support: settings, connections and attempts, never a key.</summary>
    private object Diagnostics(DiagnosticsArgs a)
    {
        var db = ProjectStore.DatabaseIn(a.Workspace);
        string project = Reader.Rows(Reader.Open(a.Workspace), "SELECT id FROM projects LIMIT 1")[0][0] as string ?? "";
        var view = Team.Read(a.Workspace, project);
        var dump = new { exported = DateTime.UtcNow, schema = ProjectStore.CurrentSchema, connections = view.Connections, assignments = view.Assignments, settings = view.Settings, budget = view.Budget,
                         attempts = RunEngine.ForTask(a.Workspace, "%").Count };
        File.WriteAllText(a.Target, JsonSerializer.Serialize(dump, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true }));
        return new { path = a.Target };
    }

    /// <summary>Removes the workspace's files and its stored keys. Refused while an attempt is running, so nothing is cut off mid-request.</summary>
    private object EraseWorkspace(string workspace)
    {
        if (Engine.ActiveCount > 0) throw new StoreException("runs_active", "A run is still in progress.", "Stop it or wait for it to finish, then erase the workspace.");
        string project;
        using (var db = Reader.Open(workspace)) project = Reader.Rows(db, "SELECT id FROM projects LIMIT 1")[0][0] as string ?? "";
        var connections = Team.Read(workspace, project).Connections;
        foreach (var c in connections) DropSecret(workspace, c.Id);
        Release(workspace);
        return new EraseResult(Portability.Erase(workspace));
    }

    private object Write(string workspace, Func<object> run)
    {
        RequireOwnership(workspace);
        return run();
    }

    // An open that has to migrate the store writes to it, so it needs ownership too.
    private object Load(string folder)
    {
        string db = ProjectStore.DatabaseIn(folder);
        if (File.Exists(db) && ProjectStore.NeedsMigration(db)) RequireOwnership(folder);
        return ProjectStore.Load(folder);
    }

    private object Created(WorkspaceInfo info)
    {
        Acquire(info.DatabasePath); // the window that creates a workspace owns it
        return info;
    }

    private OwnershipResult Release(string workspace)
    {
        string folder = FolderOf(workspace);
        if (locks.Remove(folder, out var held)) held.Dispose();
        return new(false, 0);
    }

    private object IssueRoleToken(string role)
    {
        if (role is not ("builder" or "reviewer")) throw new StoreException("unknown_role", $"There is no “{role}” fixture.", "Use builder or reviewer.");
        string t = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        roleTokens.Add((Hash(t), $"{role} (fixture)"));
        return new TokenResult(t);
    }

    private object Rotate(string next)
    {
        if (string.IsNullOrEmpty(next)) throw new StoreException("bad_request", "A new credential is required.", "Try rotating again.");
        ownerHash = Hash(next);
        roleTokens.Clear(); // role credentials were issued under the old owner credential
        return new { rotated = true };
    }

    private static T Arg<T>(WorkerRequest r) =>
        r.Args is { } a ? a.Deserialize<T>(Protocol.Json) ?? throw Bad() : throw Bad();

    /// <summary>The worker decodes and checks every image itself; the window's word about type and size is never trusted.</summary>
    private static IReadOnlyList<ImageData> ReadImages(ImageArg[]? args)
    {
        if (args is not { Length: > 0 }) return [];
        if (args.Length > Images.MaxImages) throw new StoreException("too_many_images", $"At most {Images.MaxImages} images can go in one message.", "Remove one.");
        try { return [.. args.Select(a => Images.Read(a.Name, Convert.FromBase64String(a.Base64)))]; }
        catch (FormatException) { throw new StoreException("unreadable_image", "An attached image could not be read.", "Attach it again."); }
    }

    private static StoreException NoPreview() => new("preview_required", "Sending needs the preview it was shown.", "Preview what would be sent, then send from that preview.");

    private static StoreException Bad() => new("bad_request", "The request arguments were missing or malformed.", "Update LocalMind Agent Hub so the app and worker match.");

    private static WorkerResponse Fail(WorkerRequest r, WorkerError e) => new(Protocol.Version, r.RequestId, false, null, e);

    public void Dispose()
    {
        foreach (var l in locks.Values) l.Dispose();
        locks.Clear();
    }
}

/// <summary>A running worker the client can talk to. The in-process fake and the real process both implement it.</summary>
public interface IWorker : IAsyncDisposable
{
    string Token { get; }
    string ProfileId { get; }
    Task<WorkerResponse> ExchangeAsync(WorkerRequest request, CancellationToken ct);
    void SetToken(string token);
    Task RestartAsync();
}

/// <summary>Typed client. Assigns request IDs and drops answers that a newer request superseded.</summary>
public sealed class WorkerClient(IWorker worker)
{
    private long counter, latestLoad;
    public IWorker Worker => worker;
    /// <summary>Test seam for sending bad credentials or an old protocol version.</summary>
    public string? TokenOverride { get; set; }
    public int? VersionOverride { get; set; }
    public bool OmitToken { get; set; }

    public async Task<WorkerResponse> CallAsync(string op, object? args = null, CancellationToken ct = default)
    {
        string id = "r" + Interlocked.Increment(ref counter);
        var request = new WorkerRequest(VersionOverride ?? Protocol.Version, id, op, OmitToken ? null : TokenOverride ?? worker.Token,
            args is null ? null : JsonSerializer.SerializeToElement(args, Protocol.Json));
        var response = await worker.ExchangeAsync(request, ct);
        if (response.RequestId != id)
            throw new WorkerException(new("protocol_error", "The worker answered a different request.", "Restart Agent Hub to start a fresh worker."));
        return response;
    }

    private async Task<T> CallAsync<T>(string op, object? args, CancellationToken ct)
    {
        var r = await CallAsync(op, args, ct);
        if (!r.Ok) throw new WorkerException(r.Error!);
        return r.Result!.Value.Deserialize<T>(Protocol.Json)!;
    }

    public async Task<WorkerInfo> IdentifyAsync(CancellationToken ct = default)
    {
        var info = await CallAsync<WorkerInfo>("identify", null, ct);
        if (info.Protocol != Protocol.Version || info.ProfileId != worker.ProfileId)
            throw new WorkerException(new("incompatible_version", $"The worker speaks protocol {info.Protocol}.",
                "Update LocalMind Agent Hub so the app and its worker come from the same build, then restart it."));
        return info;
    }

    /// <summary>Returns null when a later load request replaced this one, so the caller must not apply the result.</summary>
    public async Task<WorkspaceInfo?> LoadWorkspaceAsync(string folder, CancellationToken ct = default)
    {
        long mine = Interlocked.Increment(ref latestLoad);
        var info = await CallAsync<WorkspaceInfo>("loadWorkspace", new FolderArgs(folder), ct);
        return Interlocked.Read(ref latestLoad) == mine ? info : null;
    }

    public Task<WorkspaceInfo> CreateWorkspaceAsync(string folder, string name, CancellationToken ct = default) =>
        CallAsync<WorkspaceInfo>("createWorkspace", new CreateArgs(folder, name), ct);

    public Task<WorkspaceInfo> ImportLegacyAsync(string source, string folder, CancellationToken ct = default) =>
        CallAsync<WorkspaceInfo>("importLegacy", new ImportArgs(source, folder), ct);

    public Task<CommandResult> CreateTaskAsync(string workspace, string commandId, string projectId, string title, string? outcome, CancellationToken ct = default) =>
        CallAsync<CommandResult>("createTask", new CreateTaskArgs(workspace, commandId, projectId, title, outcome), ct);

    public Task<CommandResult> UpdateTaskAsync(string workspace, string commandId, string taskId, int expectedRevision, string? title = null, string? outcome = null, string? owner = null, bool? reviewRequired = null, CancellationToken ct = default) =>
        CallAsync<CommandResult>("updateTask", new UpdateTaskArgs(workspace, commandId, taskId, expectedRevision, title, outcome, owner, reviewRequired), ct);

    public Task<CommandResult> AddDependencyAsync(string workspace, string commandId, string taskId, string requiresId, int expectedRevision, CancellationToken ct = default) =>
        CallAsync<CommandResult>("addDependency", new DependencyArgs(workspace, commandId, taskId, requiresId, expectedRevision), ct);

    public Task<CommandResult> RemoveDependencyAsync(string workspace, string commandId, string taskId, string requiresId, int expectedRevision, CancellationToken ct = default) =>
        CallAsync<CommandResult>("removeDependency", new DependencyArgs(workspace, commandId, taskId, requiresId, expectedRevision), ct);

    public Task<CommandResult> MoveTaskAsync(string workspace, string commandId, string taskId, string toState, int expectedRevision, CancellationToken ct = default) =>
        CallAsync<CommandResult>("moveTask", new MoveArgs(workspace, commandId, taskId, toState, expectedRevision), ct);

    public Task<CommandResult> SetPolicyAsync(string workspace, string commandId, string name, int expectedRevision, CancellationToken ct = default) =>
        CallAsync<CommandResult>("setPolicy", new PolicyArgs(workspace, commandId, name, expectedRevision), ct);

    /// <summary>Owner only: a credential that acts as one fixture role.</summary>
    public async Task<string> IssueRoleTokenAsync(string role, CancellationToken ct = default) =>
        (await CallAsync<TokenResult>("issueRoleToken", new RoleTokenArgs(role), ct)).Token;

    /// <summary>Takes the workspace for this worker, or reports which process has it.</summary>
    public Task<OwnershipResult> AcquireWorkspaceAsync(string workspace, CancellationToken ct = default) =>
        CallAsync<OwnershipResult>("acquireWorkspace", new WorkspaceArgs(workspace), ct);

    public Task<OwnershipResult> ReleaseWorkspaceAsync(string workspace, CancellationToken ct = default) =>
        CallAsync<OwnershipResult>("releaseWorkspace", new WorkspaceArgs(workspace), ct);

    public Task<CommandResult> DecideOutputAsync(string workspace, string submissionId, string taskId, string outputId, int contentRevision, int policyRevision,
                                                 string decision, string? comment, CancellationToken ct = default) =>
        CallAsync<CommandResult>("decideOutput", new DecideArgs(workspace, submissionId, taskId, outputId, contentRevision, policyRevision, decision, comment), ct);

    public Task<CommandResult> RunRoleAsync(string workspace, string commandId, string taskId, string role, string? failure = null, CancellationToken ct = default) =>
        CallAsync<CommandResult>("runRole", new RoleArgs(workspace, commandId, taskId, role, failure), ct);

    public Task<CommandResult> FixtureSayAsync(string workspace, string commandId, string taskId, string kind, string text, CancellationToken ct = default) =>
        CallAsync<CommandResult>("fixtureSay", new SayArgs(workspace, commandId, taskId, kind, text), ct);

    public Task<CommandResult> AnswerQuestionAsync(string workspace, string commandId, string questionId, string text, CancellationToken ct = default) =>
        CallAsync<CommandResult>("answerQuestion", new AnswerArgs(workspace, commandId, questionId, text), ct);

    public Task<CommandResult> AddConnectionAsync(string ws, string cmd, string provider, string endpoint, string label, CancellationToken ct = default) =>
        CallAsync<CommandResult>("addConnection", new AddConnectionArgs(ws, cmd, provider, endpoint, label), ct);
    public Task<CommandResult> RemoveConnectionAsync(string ws, string cmd, string connectionId, CancellationToken ct = default) =>
        CallAsync<CommandResult>("removeConnection", new ConnectionArgs(ws, cmd, connectionId), ct);
    public Task<JsonElement> ProbeConnectionAsync(string ws, string connectionId, CancellationToken ct = default) =>
        CallAsync<JsonElement>("probeConnection", new ConnectionArgs(ws, "", connectionId), ct);
    /// <summary>Hands a key to the worker once. Null or empty deletes it.</summary>
    public Task<CommandResult> SetCredentialAsync(string ws, string cmd, string connectionId, string? secret, CancellationToken ct = default) =>
        CallAsync<CommandResult>("setCredential", new CredentialArgs(ws, cmd, connectionId, secret), ct);
    public Task<CommandResult> AssignRoleAsync(string ws, string cmd, string project, string role, string connectionId, string model, CancellationToken ct = default) =>
        CallAsync<CommandResult>("assignRole", new AssignArgs(ws, cmd, project, role, connectionId, model), ct);
    public Task<CommandResult> SetProjectSettingsAsync(string ws, string cmd, ProjectSettings settings, CancellationToken ct = default) =>
        CallAsync<CommandResult>("setProjectSettings", new SettingsArgs(ws, cmd, settings), ct);
    public Task<CommandResult> GrantConsentAsync(string ws, string cmd, string project, string connectionId, string model, string[] files, CancellationToken ct = default) =>
        CallAsync<CommandResult>("grantConsent", new ConsentArgs(ws, cmd, project, connectionId, model, files), ct);
    public Task<CommandResult> RevokeConsentAsync(string ws, string cmd, string consentId, CancellationToken ct = default) =>
        CallAsync<CommandResult>("revokeConsent", new RevokeArgs(ws, cmd, consentId), ct);
    public Task<CommandResult> SetPriceAsync(string ws, string cmd, string connectionId, string model, string currency, double input, double output, string source, DateTime effective, CancellationToken ct = default) =>
        CallAsync<CommandResult>("setPrice", new PriceArgs(ws, cmd, connectionId, model, currency, input, output, source, effective), ct);
    public Task<CommandResult> AddTaskFileAsync(string ws, string cmd, string taskId, string name, string content, CancellationToken ct = default) =>
        CallAsync<CommandResult>("addTaskFile", new FileArgs(ws, cmd, taskId, name, content), ct);
    public Task<DispatchPreview> PreviewDispatchAsync(string ws, string taskId, string role, string? instruction = null, CancellationToken ct = default) =>
        CallAsync<DispatchPreview>("previewDispatch", new PreviewArgs(ws, taskId, role, instruction), ct);
    /// <summary>Sends exactly the preview whose binding is given. Without one, this previews first and confirms what it just saw: for scripted callers; the window always passes the binding the owner was shown.</summary>
    public async Task<CommandResult> DispatchAsync(string ws, string cmd, string taskId, string role, bool acceptPriceRisk = false, bool acceptRepeatCost = false, string? binding = null, CancellationToken ct = default) =>
        await CallAsync<CommandResult>("dispatch", new DispatchArgs(ws, cmd, taskId, role, acceptPriceRisk, acceptRepeatCost, binding ?? (await PreviewDispatchAsync(ws, taskId, role, null, ct)).Binding), ct);
    public Task<CommandResult> ReconcileAttemptAsync(string ws, string cmd, string attemptId, double amount, CancellationToken ct = default) =>
        CallAsync<CommandResult>("reconcileAttempt", new ReconcileArgs(ws, cmd, attemptId, amount), ct);
    public Task<CommandResult> CancelAttemptAsync(string ws, string cmd, string attemptId, CancellationToken ct = default) =>
        CallAsync<CommandResult>("cancelAttempt", new AttemptArgs(ws, cmd, attemptId, false), ct);
    public Task<CommandResult> PauseAttemptAsync(string ws, string cmd, string attemptId, CancellationToken ct = default) =>
        CallAsync<CommandResult>("pauseAttempt", new AttemptArgs(ws, cmd, attemptId, false), ct);
    public Task<CommandResult> ResumeAttemptAsync(string ws, string cmd, string attemptId, bool acceptRepeatCost = false, CancellationToken ct = default) =>
        CallAsync<CommandResult>("resumeAttempt", new AttemptArgs(ws, cmd, attemptId, acceptRepeatCost), ct);
    public Task<CommandResult> SmokeTestAsync(string ws, string cmd, string connectionId, string model, string prompt, CancellationToken ct = default) =>
        CallAsync<CommandResult>("smokeTest", new SmokeArgs(ws, cmd, connectionId, model, prompt), ct);
    public Task<List<DispatchPreview>> PreviewInstructionAsync(string ws, string taskId, string[] roles, string text, ImageArg[]? images = null, Dictionary<string, string>? imageConfirms = null, CancellationToken ct = default) =>
        CallAsync<List<DispatchPreview>>("previewInstruction", new InstructionPreviewArgs(ws, taskId, roles, text, images, imageConfirms), ct);
    public async Task<InstructionResult> SendInstructionAsync(string ws, string cmd, string taskId, string[] roles, string text, Dictionary<string, string>? bindings = null, ImageArg[]? images = null, Dictionary<string, string>? imageConfirms = null, CancellationToken ct = default) =>
        await CallAsync<InstructionResult>("sendInstruction", new InstructionArgs(ws, cmd, taskId, roles, text, bindings ?? (await PreviewInstructionAsync(ws, taskId, roles, text, images, imageConfirms, ct)).ToDictionary(p => p.Role, p => p.Binding), images, imageConfirms), ct);
    public Task<CommandResult> ProposePlanAsync(string ws, string cmd, string projectId, string brief, PlanTask[]? tasks = null, PlanEdge[]? edges = null, CancellationToken ct = default) =>
        CallAsync<CommandResult>("proposePlan", new PlanProposeArgs(ws, cmd, projectId, brief, tasks, edges), ct);
    public Task<CommandResult> EditPlanAsync(string ws, string cmd, string planId, int version, PlanTask[] tasks, PlanEdge[] edges, CancellationToken ct = default) =>
        CallAsync<CommandResult>("editPlan", new PlanEditArgs(ws, cmd, planId, version, tasks, edges), ct);
    public Task<CommandResult> ApprovePlanAsync(string ws, string cmd, string planId, int version, CancellationToken ct = default) =>
        CallAsync<CommandResult>("approvePlan", new PlanDecideArgs(ws, cmd, planId, version), ct);
    public Task<CommandResult> DismissPlanAsync(string ws, string cmd, string planId, int version, CancellationToken ct = default) =>
        CallAsync<CommandResult>("dismissPlan", new PlanDecideArgs(ws, cmd, planId, version), ct);
    public Task<PlanStart> StartPlanAsync(string ws, string cmd, string planId, Dictionary<string, string> bindings, CancellationToken ct = default) =>
        CallAsync<PlanStart>("startPlan", new PlanStartArgs(ws, cmd, planId, bindings), ct);
    public Task<List<PlanView>> ReadPlansAsync(string ws, string projectId, CancellationToken ct = default) =>
        CallAsync<List<PlanView>>("readPlans", new ProjectArgs(ws, projectId), ct);
    public Task<PlanStatus> PlanStatusAsync(string ws, string planId, CancellationToken ct = default) =>
        CallAsync<PlanStatus>("planStatus", new PlanArgs(ws, planId), ct);
    public Task<PlanEstimate> PlanEstimateAsync(string ws, string planId, CancellationToken ct = default) =>
        CallAsync<PlanEstimate>("planEstimate", new PlanArgs(ws, planId), ct);
    public Task<List<AttemptActual>> PlanActualsAsync(string ws, string planId, CancellationToken ct = default) =>
        CallAsync<List<AttemptActual>>("planActuals", new PlanArgs(ws, planId), ct);
    public Task<List<HandoffEvent>> PlanHandoffsAsync(string ws, string projectId, long after, CancellationToken ct = default) =>
        CallAsync<List<HandoffEvent>>("planHandoffs", new ProjectArgs(ws, projectId, after), ct);
    public Task<CommandResult> AddDocumentAsync(string ws, string cmd, string projectId, string name, string text, CancellationToken ct = default) => CallAsync<CommandResult>("addDocument", new DocAddArgs(ws, cmd, projectId, name, text), ct);
    public Task<CommandResult> AddDocumentImageAsync(string ws, string cmd, string projectId, ImageArg image, CancellationToken ct = default) => CallAsync<CommandResult>("addDocumentImage", new DocImageArgs(ws, cmd, projectId, image), ct);
    public Task<CommandResult> EditDocumentAsync(string ws, string cmd, string docId, string text, CancellationToken ct = default) => CallAsync<CommandResult>("editDocument", new DocEditArgs(ws, cmd, docId, text), ct);
    public Task<CommandResult> RemoveDocumentAsync(string ws, string cmd, string docId, CancellationToken ct = default) => CallAsync<CommandResult>("removeDocument", new DocIdArgs(ws, cmd, docId), ct);
    public Task<ForgetResult> ForgetSourceAsync(string ws, string cmd, string docId, CancellationToken ct = default) => CallAsync<ForgetResult>("forgetSource", new DocIdArgs(ws, cmd, docId), ct);
    public Task<CommandResult> AskDocumentsAsync(string ws, string cmd, string projectId, string question, string role, CancellationToken ct = default) => CallAsync<CommandResult>("askDocuments", new AskArgs(ws, cmd, projectId, question, role), ct);
    public Task<List<DocumentInfo>> ReadDocumentsAsync(string ws, string projectId, CancellationToken ct = default) => CallAsync<List<DocumentInfo>>("readDocuments", new ProjectArgs(ws, projectId), ct);
    public Task<List<DocAnswer>> ReadAnswersAsync(string ws, string projectId, CancellationToken ct = default) => CallAsync<List<DocAnswer>>("readAnswers", new ProjectArgs(ws, projectId), ct);
    public Task<PassageView> OpenPassageAsync(string ws, string docId, int revision, int start, int length, CancellationToken ct = default) => CallAsync<PassageView>("openPassage", new PassageArgs(ws, docId, revision, start, length), ct);
    public Task<CommandResult> ResolveAttemptAsync(string ws, string cmd, string attemptId, string choice, double? amount = null, CancellationToken ct = default) => CallAsync<CommandResult>("resolveAttempt", new ResolveArgs(ws, cmd, attemptId, choice, amount), ct);
    public Task<WakeReport> LifecycleAsync(string ws, string kind, CancellationToken ct = default) => CallAsync<WakeReport>("lifecycle", new LifecycleArgs(ws, kind), ct);
    public Task<ShutdownReport> ShutdownAsync(string ws, string policy, int maxMs, CancellationToken ct = default) => CallAsync<ShutdownReport>("shutdown", new ShutdownArgs(ws, policy, maxMs), ct);
    public Task<List<ReplayEvent>> ReplayTimelineAsync(string ws, string projectId, CancellationToken ct = default) => CallAsync<List<ReplayEvent>>("replayTimeline", new ReplayArgs(ws, projectId, 0), ct);
    public Task<ReplayState> ReplayStateAsync(string ws, string projectId, long seq, CancellationToken ct = default) => CallAsync<ReplayState>("replayState", new ReplayArgs(ws, projectId, seq), ct);
    public Task<string> RunDiagnosticsAsync(string ws, string? attemptId, bool includeContent, CancellationToken ct = default) => CallAsync<string>("runDiagnostics", new RunDiagArgs(ws, attemptId, includeContent, null), ct);
    public Task<JsonElement> ExportRunDiagnosticsAsync(string ws, string? attemptId, bool includeContent, string target, CancellationToken ct = default) => CallAsync<JsonElement>("exportRunDiagnostics", new RunDiagArgs(ws, attemptId, includeContent, target), ct);
    public Task<ExportManifest> ExportWorkspaceAsync(string ws, string target, CancellationToken ct = default) => CallAsync<ExportManifest>("exportWorkspace", new PackageArgs(ws, target), ct);
    public Task<RestoreResult> RestoreWorkspaceAsync(string package, string target, CancellationToken ct = default) => CallAsync<RestoreResult>("restoreWorkspace", new RestoreArgs(package, target), ct);
    public Task<EraseResult> EraseWorkspaceAsync(string ws, CancellationToken ct = default) => CallAsync<EraseResult>("eraseWorkspace", new WorkspaceArgs(ws), ct);
    public Task<JsonElement> RecoverRunsAsync(string ws, CancellationToken ct = default) =>
        CallAsync<JsonElement>("recoverRuns", new WorkspaceArgs(ws), ct);
    public Task<JsonElement> ExportDiagnosticsAsync(string ws, string target, CancellationToken ct = default) =>
        CallAsync<JsonElement>("exportDiagnostics", new DiagnosticsArgs(ws, target), ct);

    /// <summary>What a command ID did, or null when it was never applied.</summary>
    public async Task<CommandResult?> CommandStatusAsync(string workspace, string commandId, CancellationToken ct = default)
    {
        var r = await CallAsync("commandStatus", new StatusArgs(workspace, commandId), ct);
        if (!r.Ok) throw new WorkerException(r.Error!);
        var result = r.Result!.Value.Deserialize<CommandResult>(Protocol.Json);
        return result is { Id: not null } ? result : null;
    }

    public async Task RotateAsync(CancellationToken ct = default)
    {
        string next = NewToken();
        await CallAsync<JsonElement>("rotate", new RotateArgs(next), ct);
        worker.SetToken(next);
    }

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

/// <summary>Deterministic stand-in: runs the real command handler in-process with sequential IDs.</summary>
public sealed class FakeWorker : IWorker
{
    private WorkerHost host;
    private int ids;
    public string Token { get; private set; }
    public string ProfileId { get; }
    /// <summary>Lets a test hold a request open to force overlapping calls.</summary>
    public Func<WorkerRequest, Task>? BeforeHandle { get; set; }

    private readonly string prefix;
    /// <summary>The OS vault stand-in. It outlives a worker restart, as the real one does.</summary>
    public ICredentialVault Vault { get; }

    public FakeWorker(string profileId = "fake-profile", ICredentialVault? vault = null)
    {
        ProfileId = profileId;
        Token = "fake-token-1";
        // Sequential, and unique across instances so two in-process windows never mint the same ID.
        prefix = "id" + Guid.NewGuid().ToString("N")[..4] + "-";
        Vault = vault ?? new MemoryVault();
        host = new(profileId, Token, () => prefix + Interlocked.Increment(ref ids), Vault);
    }

    /// <summary>Lets a test lose the answer after the command has been applied (throw WorkerException) to model a crash.</summary>
    public Func<WorkerRequest, Task>? AfterHandle { get; set; }

    public async Task<WorkerResponse> ExchangeAsync(WorkerRequest request, CancellationToken ct)
    {
        if (BeforeHandle is { } hold) await hold(request);
        var response = host.Handle(request, ct);
        if (AfterHandle is { } lose) await lose(request);
        return response;
    }

    public void SetToken(string token) => Token = token;
    public Task RestartAsync()
    {
        Token = "fake-token-" + (ids + 2);
        host.Dispose(); // a restarted worker has lost whatever workspaces it owned
        host = new(ProfileId, Token, () => prefix + Interlocked.Increment(ref ids), Vault);
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() { host.Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>Real worker child process. The credential travels over its stdin, never its command line or environment.</summary>
public sealed class ProcessWorker : IWorker
{
    private readonly string exe;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    public string Token { get; private set; } = "";
    public string ProfileId { get; }

    public ProcessWorker(string workerExecutable, string profileId)
    {
        exe = workerExecutable;
        ProfileId = profileId;
    }

    public static string? Locate()
    {
        foreach (var name in new[] { "LocalMind.AgentHub.Worker.exe", "LocalMind.AgentHub.Worker" })
            if (Path.Combine(AppContext.BaseDirectory, name) is var p && File.Exists(p)) return p;
        return null;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        Stop();
        Token = WorkerClient.NewToken();
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
        };
        process = Process.Start(psi) ?? throw Unavailable();
        _ = process.StandardError.ReadToEndAsync(); // drained, never logged
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { profile = ProfileId, token = Token }));
        await process.StandardInput.FlushAsync(ct);
        var hello = await ReadAsync(ct);
        if (hello.RequestId != "handshake" || !hello.Ok || hello.Result!.Value.Deserialize<WorkerInfo>(Protocol.Json) is not { } info
            || info.Protocol != Protocol.Version || info.ProfileId != ProfileId)
            throw new WorkerException(new("incompatible_version", "The worker did not complete its handshake.",
                "Update LocalMind Agent Hub so the app and its worker come from the same build, then restart it."));
    }

    private static WorkerException Unavailable() => new(new("worker_unavailable", "The worker process could not start.", "Reinstall or rebuild Agent Hub, then restart it."));

    private async Task<WorkerResponse> ReadAsync(CancellationToken ct)
    {
        string? line;
        try { line = await process!.StandardOutput.ReadLineAsync(ct); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { line = null; }
        if (line is null) throw new WorkerException(new("worker_exited", "The worker stopped unexpectedly.", "Restart Agent Hub; nothing was changed by the failed request."));
        return JsonSerializer.Deserialize<WorkerResponse>(line, Protocol.Json) ?? throw Unavailable();
    }

    /// <summary>How many request lines this object has written to worker processes. Lets tests prove nothing was replayed.</summary>
    public int RequestsSent { get; private set; }
    public int ProcessId => process?.Id ?? 0;
    /// <summary>Test seam: runs after a request is written and before its answer is read.</summary>
    public Func<Process, Task>? AfterSend { get; set; }
    /// <summary>Test seam: ends the current worker process.</summary>
    public void Kill() { try { process?.Kill(true); process?.WaitForExit(2000); } catch (InvalidOperationException) { } }

    public async Task<WorkerResponse> ExchangeAsync(WorkerRequest request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                // A worker that is already gone has seen nothing, so starting a fresh one and sending is safe for any operation.
                if (process is null || process.HasExited) request = await RestartForAsync(request, ct);
                try
                {
                    await process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Protocol.Json));
                    await process.StandardInput.FlushAsync(ct);
                }
                catch (IOException) when (attempt == 0)
                {
                    Stop(); // the line never reached the worker
                    continue;
                }
                RequestsSent++;
                try
                {
                    if (AfterSend is { } hook) await hook(process);
                    return await ReadAsync(ct);
                }
                catch (WorkerException e) when (e.Error.Code == "worker_exited")
                {
                    Stop();
                    if (Protocol.IsWrite(request.Op))
                        throw new WorkerException(new("outcome_unknown", $"The worker stopped while handling “{request.Op}”, so it may or may not have been applied.",
                            "Check the workspace, then repeat the action. Repeating is safe: it carries the same command ID, so it cannot apply twice."));
                    if (attempt >= 1) throw;
                    // A read can be repeated once on a fresh worker.
                }
            }
        }
        finally { gate.Release(); }
    }

    private async Task<WorkerRequest> RestartForAsync(WorkerRequest request, CancellationToken ct)
    {
        string old = Token;
        await StartAsync(ct);
        return request.Token == old ? request with { Token = Token } : request;
    }

    public void SetToken(string token) => Token = token;
    public Task RestartAsync() => StartAsync();

    private void Stop()
    {
        if (process is null) return;
        try { process.StandardInput.Close(); if (!process.WaitForExit(2000)) process.Kill(true); } catch (InvalidOperationException) { }
        process.Dispose();
        process = null;
    }

    public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }
}
