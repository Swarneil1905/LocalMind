using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalMind.AgentHub.Core;

/// <summary>Runs inside the worker. Validates version and credentials before any command touches a store.</summary>
public sealed class WorkerHost(string profileId, string token, Func<string> newId)
{
    private byte[] tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public WorkerInfo Info => new("LocalMind.AgentHub.Worker", Protocol.Version, profileId);

    public WorkerResponse Handle(WorkerRequest request, CancellationToken ct = default)
    {
        if (request.Version != Protocol.Version)
            return Fail(request, new("incompatible_version", $"The app speaks protocol {request.Version}; the worker speaks {Protocol.Version}.",
                "Update LocalMind Agent Hub so the app and its worker come from the same build, then restart it."));
        if (request.Token is null || !CryptographicOperations.FixedTimeEquals(tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(request.Token))))
            return Fail(request, new("unauthorized", request.Token is null ? "The request carried no credentials." : "The request credentials were not accepted.",
                "Restart Agent Hub so it can start a new worker with fresh credentials."));
        try
        {
            object result = request.Op switch
            {
                "identify" => Info,
                "loadWorkspace" => ProjectStore.Load(Arg<FolderArgs>(request).Folder),
                "createWorkspace" => ProjectStore.Create(Arg<CreateArgs>(request).Folder, Arg<CreateArgs>(request).Name, newId),
                "importLegacy" => ProjectStore.Import(Arg<ImportArgs>(request).Source, Arg<ImportArgs>(request).Folder, ct, newId),
                "rotate" => Rotate(Arg<RotateArgs>(request).NewToken),
                "createTask" => TaskStore.CreateTask(Arg<CreateTaskArgs>(request).Workspace, Arg<CreateTaskArgs>(request).CommandId, Arg<CreateTaskArgs>(request).ProjectId, Arg<CreateTaskArgs>(request).Title, Arg<CreateTaskArgs>(request).Outcome, newId),
                "updateTask" => TaskStore.UpdateTask(Arg<UpdateTaskArgs>(request).Workspace, Arg<UpdateTaskArgs>(request).CommandId, Arg<UpdateTaskArgs>(request).TaskId, Arg<UpdateTaskArgs>(request).ExpectedRevision, Arg<UpdateTaskArgs>(request).Title, Arg<UpdateTaskArgs>(request).Outcome, Arg<UpdateTaskArgs>(request).Owner),
                "addDependency" => TaskStore.AddDependency(Arg<DependencyArgs>(request).Workspace, Arg<DependencyArgs>(request).CommandId, Arg<DependencyArgs>(request).TaskId, Arg<DependencyArgs>(request).RequiresId, Arg<DependencyArgs>(request).ExpectedRevision),
                "removeDependency" => TaskStore.RemoveDependency(Arg<DependencyArgs>(request).Workspace, Arg<DependencyArgs>(request).CommandId, Arg<DependencyArgs>(request).TaskId, Arg<DependencyArgs>(request).RequiresId, Arg<DependencyArgs>(request).ExpectedRevision),
                "moveTask" => TaskStore.MoveTask(Arg<MoveArgs>(request).Workspace, Arg<MoveArgs>(request).CommandId, Arg<MoveArgs>(request).TaskId, Arg<MoveArgs>(request).ToState, Arg<MoveArgs>(request).ExpectedRevision),
                "runRole" => TaskStore.RunRole(Arg<RoleArgs>(request).Workspace, Arg<RoleArgs>(request).CommandId, Arg<RoleArgs>(request).TaskId, Arg<RoleArgs>(request).Role, Arg<RoleArgs>(request).Failure, newId),
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

    private object Rotate(string next)
    {
        if (string.IsNullOrEmpty(next)) throw new StoreException("bad_request", "A new credential is required.", "Try rotating again.");
        tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(next));
        return new { rotated = true };
    }

    private static T Arg<T>(WorkerRequest r) =>
        r.Args is { } a ? a.Deserialize<T>(Protocol.Json) ?? throw Bad() : throw Bad();

    private static StoreException Bad() => new("bad_request", "The request arguments were missing or malformed.", "Update LocalMind Agent Hub so the app and worker match.");

    private static WorkerResponse Fail(WorkerRequest r, WorkerError e) => new(Protocol.Version, r.RequestId, false, null, e);
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

    public Task<CommandResult> UpdateTaskAsync(string workspace, string commandId, string taskId, int expectedRevision, string? title = null, string? outcome = null, string? owner = null, CancellationToken ct = default) =>
        CallAsync<CommandResult>("updateTask", new UpdateTaskArgs(workspace, commandId, taskId, expectedRevision, title, outcome, owner), ct);

    public Task<CommandResult> AddDependencyAsync(string workspace, string commandId, string taskId, string requiresId, int expectedRevision, CancellationToken ct = default) =>
        CallAsync<CommandResult>("addDependency", new DependencyArgs(workspace, commandId, taskId, requiresId, expectedRevision), ct);

    public Task<CommandResult> RemoveDependencyAsync(string workspace, string commandId, string taskId, string requiresId, int expectedRevision, CancellationToken ct = default) =>
        CallAsync<CommandResult>("removeDependency", new DependencyArgs(workspace, commandId, taskId, requiresId, expectedRevision), ct);

    public Task<CommandResult> MoveTaskAsync(string workspace, string commandId, string taskId, string toState, int expectedRevision, CancellationToken ct = default) =>
        CallAsync<CommandResult>("moveTask", new MoveArgs(workspace, commandId, taskId, toState, expectedRevision), ct);

    public Task<CommandResult> RunRoleAsync(string workspace, string commandId, string taskId, string role, string? failure = null, CancellationToken ct = default) =>
        CallAsync<CommandResult>("runRole", new RoleArgs(workspace, commandId, taskId, role, failure), ct);

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

    public FakeWorker(string profileId = "fake-profile")
    {
        ProfileId = profileId;
        Token = "fake-token-1";
        host = new(profileId, Token, () => "id" + Interlocked.Increment(ref ids));
    }

    public async Task<WorkerResponse> ExchangeAsync(WorkerRequest request, CancellationToken ct)
    {
        if (BeforeHandle is { } hold) await hold(request);
        return host.Handle(request, ct);
    }

    public void SetToken(string token) => Token = token;
    public Task RestartAsync()
    {
        Token = "fake-token-" + (ids + 2);
        host = new(ProfileId, Token, () => "id" + Interlocked.Increment(ref ids));
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
