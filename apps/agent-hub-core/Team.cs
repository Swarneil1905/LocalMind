using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub.Core;

public sealed record ConnectionModel(string Id, ModelCapabilities Capabilities, bool Available, string DiscoveredAt);

public sealed record ConnectionInfo(string Id, string Provider, string Endpoint, string Label, bool IsCloud, string CredentialStatus, string? CredentialChangedAt,
                                    string? LastProbeAt, bool? LastProbeOk, string? LastError, IReadOnlyList<ConnectionModel> Models);

public sealed record Assignment(string Role, string ConnectionId, string ModelId);

public sealed record ProjectSettings(string ProjectId, bool LocalOnly, double? BudgetLimit, string Currency, int MaxSteps, int MaxSeconds, int MaxToolCalls, int MaxOutputTokens,
                                     IReadOnlyList<string>? OnlyProviders, bool AutoReview, int MaxConcurrency = 2);

public sealed record Consent(string Id, string ConnectionId, string ModelId, IReadOnlyList<string> Files, string GrantedAt);

public sealed record Price(string Id, string ConnectionId, string ModelId, string Currency, double InputPerMTok, double OutputPerMTok, string Source, string EffectiveAt);

/// <summary>Committed spend: settled amounts plus open reservations. Unknown usage is counted separately and never hidden.</summary>
public sealed record BudgetStatus(double? Limit, string Currency, double Spent, double Reserved, int UnknownUsage, int Unresolved = 0)
{
    public double? Remaining => Limit is { } l ? l - Spent - Reserved : null;
}

public sealed record TeamView(IReadOnlyList<ConnectionInfo> Connections, IReadOnlyList<Assignment> Assignments, ProjectSettings Settings,
                              IReadOnlyList<Consent> Consents, IReadOnlyList<Price> Prices, BudgetStatus Budget);

/// <summary>Connections, roles, limits, consent, prices and supplied files. Every change is a command with an event, like task changes.</summary>
public static class Team
{
    public static readonly string[] Roles = ["planner", "builder", "reviewer"];
    public const int StalePriceDays = 90;
    private static string Now => DateTime.UtcNow.ToString("O");

    private static StoreException Bad(string code, string message, string recovery) => new(code, message, recovery);

    internal static bool IsLoopback(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host is "localhost");

    // --- Connections ------------------------------------------------------------------------------------------
    public static CommandResult AddConnection(string workspace, string commandId, string provider, string endpoint, string label, Func<string> newId) =>
        TaskStore.Run(workspace, commandId, "connection_added", db =>
        {
            if (provider is not ("ollama" or "openrouter")) throw Bad("unknown_provider", $"“{provider}” is not a supported provider.", "Use Ollama or OpenRouter.");
            if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw Bad("invalid_endpoint", "The endpoint must be an http or https address.", "Enter an address such as http://localhost:11434.");
            if (provider == "openrouter" && uri.Scheme != "https" && !uri.IsLoopback)
                throw Bad("insecure_endpoint", "A gateway key would travel unencrypted to this address.", "Use an https address.");
            label = string.IsNullOrWhiteSpace(label) ? (provider == "ollama" ? "Ollama" : "OpenRouter") : label.Trim();
            // A gateway is a cloud destination even when tested locally; a local Ollama is not.
            bool cloud = provider == "openrouter" || !IsLoopback(endpoint);
            string id = newId();
            TaskStore.Exec(db, "INSERT INTO connections(id,provider,endpoint,label,is_cloud,created_at) VALUES($i,$p,$e,$l,$c,$at)",
                ("$i", id), ("$p", provider), ("$e", uri.ToString().TrimEnd('/')), ("$l", label), ("$c", cloud ? 1 : 0), ("$at", Now));
            return new(id, 0, "", null, null, $"{provider} connection “{label}”");
        });

    public static CommandResult RemoveConnection(string workspace, string commandId, string connectionId) =>
        TaskStore.Run(workspace, commandId, "connection_removed", db =>
        {
            if (TaskStore.Scalar<long>(db, "SELECT count(*) FROM assignments WHERE connection_id=$c", ("$c", connectionId)) > 0)
                throw Bad("in_use", "A role still uses this connection.", "Assign that role to another model first.");
            TaskStore.Exec(db, "DELETE FROM connection_models WHERE connection_id=$c", ("$c", connectionId));
            TaskStore.Exec(db, "DELETE FROM connections WHERE id=$c", ("$c", connectionId));
            return new(connectionId, 0, "", null, null, "connection removed");
        });

    /// <summary>Records what a probe found. Models no longer listed stay, marked unavailable, so a role assigned to one explains itself.</summary>
    public static CommandResult RecordProbe(string workspace, string connectionId, IReadOnlyList<DiscoveredModel>? models, string? error) =>
        TaskStore.Run(workspace, "probe-" + Guid.NewGuid().ToString("N"), "connection_probed", db =>
        {
            Connection(db, connectionId);
            TaskStore.Exec(db, "UPDATE connections SET last_probe_at=$at, last_probe_ok=$ok, last_error=$e WHERE id=$c",
                ("$at", Now), ("$ok", models != null ? 1 : 0), ("$e", error), ("$c", connectionId));
            if (models != null)
            {
                TaskStore.Exec(db, "UPDATE connection_models SET available=0 WHERE connection_id=$c", ("$c", connectionId));
                foreach (var m in models)
                    TaskStore.Exec(db, """
                        INSERT INTO connection_models(connection_id,model_id,capabilities,available,discovered_at) VALUES($c,$m,$k,1,$at)
                        ON CONFLICT(connection_id,model_id) DO UPDATE SET capabilities=$k, available=1, discovered_at=$at
                        """, ("$c", connectionId), ("$m", m.Id), ("$k", JsonSerializer.Serialize(m.Capabilities, Protocol.Json)), ("$at", Now));
            }
            return new(connectionId, 0, "", null, null, models != null ? $"{models.Count} models" : error);
        });

    /// <summary>Marks one model as gone when a dispatch finds it missing.</summary>
    internal static void MarkUnavailable(SqliteConnection db, string connectionId, string model) =>
        TaskStore.Exec(db, "UPDATE connection_models SET available=0 WHERE connection_id=$c AND model_id=$m", ("$c", connectionId), ("$m", model));

    /// <summary>The worker stores the secret in the vault first, then records only the reference and status here.</summary>
    public static CommandResult RecordCredential(string workspace, string commandId, string connectionId, string status, string? reference) =>
        TaskStore.Run(workspace, commandId, "credential_" + status, db =>
        {
            Connection(db, connectionId);
            TaskStore.Exec(db, "UPDATE connections SET credential_ref=$r, credential_status=$s, credential_changed_at=$at WHERE id=$c",
                ("$r", reference), ("$s", status), ("$at", Now), ("$c", connectionId));
            return new(connectionId, 0, "", null, null, "key " + status); // never the key
        });

    internal static (string Provider, string Endpoint, bool IsCloud, string? CredentialRef, string Label) Connection(SqliteConnection db, string id)
    {
        var r = Reader.Rows(db, "SELECT provider, endpoint, is_cloud, credential_ref, label FROM connections WHERE id=$i", ("$i", id)).FirstOrDefault()
            ?? throw Bad("not_found", "That connection no longer exists.", "Open Connections and choose another.");
        return (Reader.S(r[0]), Reader.S(r[1]), Reader.I(r[2]) != 0, r[3] as string, Reader.S(r[4]));
    }

    // --- Project settings, consent, prices, roles ---------------------------------------------------------------
    internal static ProjectSettings Settings(SqliteConnection db, string project)
    {
        var r = Reader.Rows(db, "SELECT local_only,budget_limit,currency,max_steps,max_seconds,max_tool_calls,max_output_tokens,only_providers,auto_review,max_concurrency FROM project_settings WHERE project_id=$p", ("$p", project)).FirstOrDefault();
        if (r == null) return new(project, true, null, "USD", 4, 180, 3, 600, null, true, 2);
        return new(project, Reader.I(r[0]) != 0, r[1] is null ? null : Convert.ToDouble(r[1]), Reader.S(r[2]), Reader.I(r[3]), Reader.I(r[4]), Reader.I(r[5]), Reader.I(r[6]),
            r[7] is string only ? JsonSerializer.Deserialize<string[]>(only) : null, Reader.I(r[8]) != 0, Reader.I(r[9]));
    }

    public static CommandResult SetProjectSettings(string workspace, string commandId, ProjectSettings s) =>
        TaskStore.Run(workspace, commandId, "project_settings_changed", db =>
        {
            if (TaskStore.Scalar<long>(db, "SELECT count(*) FROM projects WHERE id=$p", ("$p", s.ProjectId)) == 0) throw Bad("not_found", "That project no longer exists.", "Reload the workspace.");
            if (s.MaxSteps is < 1 or > 50 || s.MaxSeconds is < 5 or > 3600 || s.MaxToolCalls is < 0 or > 50 || s.MaxOutputTokens is < 16 or > 32768 || s.BudgetLimit is < 0 || s.MaxConcurrency is < 1 or > 8)
                throw Bad("invalid_limits", "A limit is out of range.", "Steps 1–50, time 5–3600 s, tool calls 0–50, output 16–32768 tokens, budget 0 or more.");
            TaskStore.Exec(db, """
                INSERT INTO project_settings(project_id,local_only,budget_limit,currency,max_steps,max_seconds,max_tool_calls,max_output_tokens,only_providers,auto_review,max_concurrency)
                VALUES($p,$l,$b,$c,$s,$t,$k,$o,$op,$a,$mc)
                ON CONFLICT(project_id) DO UPDATE SET local_only=$l,budget_limit=$b,currency=$c,max_steps=$s,max_seconds=$t,max_tool_calls=$k,max_output_tokens=$o,only_providers=$op,auto_review=$a,max_concurrency=$mc
                """, ("$p", s.ProjectId), ("$l", s.LocalOnly ? 1 : 0), ("$b", s.BudgetLimit), ("$c", s.Currency), ("$s", s.MaxSteps), ("$t", s.MaxSeconds), ("$k", s.MaxToolCalls),
                ("$o", s.MaxOutputTokens), ("$op", s.OnlyProviders is { Count: > 0 } o ? JsonSerializer.Serialize(o) : null), ("$a", s.AutoReview ? 1 : 0), ("$mc", s.MaxConcurrency));
            return new(s.ProjectId, 0, "", null, null, s.LocalOnly ? "local only" : "cloud allowed");
        });

    /// <summary>Consent binds one destination (connection and exact model) to a scope: the packet text and the named files, nothing else.</summary>
    public static CommandResult GrantConsent(string workspace, string commandId, string project, string connectionId, string model, IReadOnlyList<string> files, Func<string> newId) =>
        TaskStore.Run(workspace, commandId, "consent_granted", db =>
        {
            var c = Connection(db, connectionId);
            if (!c.IsCloud) throw Bad("not_needed", "This connection is on this computer; nothing leaves it.", "No consent is needed.");
            if (Settings(db, project).LocalOnly) throw Bad("local_only", "This project is local-only, so nothing may be sent to a cloud model.", "Turn off local-only for the project first.");
            string id = newId();
            TaskStore.Exec(db, "UPDATE consents SET revoked_at=$at WHERE project_id=$p AND connection_id=$c AND model_id=$m AND revoked_at IS NULL", ("$at", Now), ("$p", project), ("$c", connectionId), ("$m", model));
            TaskStore.Exec(db, "INSERT INTO consents VALUES($i,$p,$c,$m,$f,$at,NULL)", ("$i", id), ("$p", project), ("$c", connectionId), ("$m", model), ("$f", JsonSerializer.Serialize(files)), ("$at", Now));
            return new(id, 0, "", null, null, $"consent for {model} on {c.Label}");
        });

    public static CommandResult RevokeConsent(string workspace, string commandId, string consentId) =>
        TaskStore.Run(workspace, commandId, "consent_revoked", db =>
        {
            TaskStore.Exec(db, "UPDATE consents SET revoked_at=$at WHERE id=$i AND revoked_at IS NULL", ("$at", Now), ("$i", consentId));
            return new(consentId, 0, "", null, null, "consent revoked");
        });

    internal static Consent? ActiveConsent(SqliteConnection db, string project, string connectionId, string model) =>
        Reader.Rows(db, "SELECT id, files, granted_at FROM consents WHERE project_id=$p AND connection_id=$c AND model_id=$m AND revoked_at IS NULL ORDER BY granted_at DESC LIMIT 1",
            ("$p", project), ("$c", connectionId), ("$m", model)).Select(r => new Consent(Reader.S(r[0]), connectionId, model, JsonSerializer.Deserialize<string[]>(Reader.S(r[1])) ?? [], Reader.S(r[2]))).FirstOrDefault();

    public static CommandResult SetPrice(string workspace, string commandId, string connectionId, string model, string currency, double inputPerMTok, double outputPerMTok, string source, DateTime effective, Func<string> newId) =>
        TaskStore.Run(workspace, commandId, "price_recorded", db =>
        {
            Connection(db, connectionId);
            if (inputPerMTok < 0 || outputPerMTok < 0 || currency.Length != 3) throw Bad("invalid_price", "A price needs a three-letter currency and amounts of zero or more.", "Check the rates.");
            string id = newId();
            TaskStore.Exec(db, "INSERT INTO prices VALUES($i,$c,$m,$cur,$in,$out,$s,$e,$at)", ("$i", id), ("$c", connectionId), ("$m", model), ("$cur", currency.ToUpperInvariant()),
                ("$in", inputPerMTok), ("$out", outputPerMTok), ("$s", source), ("$e", effective.ToUniversalTime().ToString("O")), ("$at", Now));
            return new(id, 0, "", null, null, $"{model}: {inputPerMTok}/{outputPerMTok} {currency} per million tokens");
        });

    /// <summary>The price in effect now. Local Ollama models cost nothing and need no price row.</summary>
    internal static (Price? Price, string Status) PriceFor(SqliteConnection db, string connectionId, string model, bool cloud)
    {
        if (!cloud) return (new("local", connectionId, model, "USD", 0, 0, "local model", Now), "local");
        var r = Reader.Rows(db, "SELECT id,currency,input_per_mtok,output_per_mtok,source,effective_at FROM prices WHERE connection_id=$c AND model_id=$m AND effective_at<=$now ORDER BY effective_at DESC, recorded_at DESC LIMIT 1",
            ("$c", connectionId), ("$m", model), ("$now", Now)).FirstOrDefault();
        if (r == null) return (null, "unknown");
        var p = new Price(Reader.S(r[0]), connectionId, model, Reader.S(r[1]), Convert.ToDouble(r[2]), Convert.ToDouble(r[3]), Reader.S(r[4]), Reader.S(r[5]));
        return (p, DateTime.Parse(p.EffectiveAt, null, System.Globalization.DateTimeStyles.RoundtripKind) < DateTime.UtcNow.AddDays(-StalePriceDays) ? "stale" : "known");
    }

    public static CommandResult SetAssignment(string workspace, string commandId, string project, string role, string connectionId, string model) =>
        TaskStore.Run(workspace, commandId, "role_assigned", db =>
        {
            if (!Roles.Contains(role)) throw Bad("unknown_role", $"“{role}” is not a role.", "Use planner, builder or reviewer.");
            var c = Connection(db, connectionId);
            var m = Reader.Rows(db, "SELECT capabilities, available FROM connection_models WHERE connection_id=$c AND model_id=$m", ("$c", connectionId), ("$m", model)).FirstOrDefault()
                ?? throw Bad("model_unknown", $"{model} has not been discovered on {c.Label}.", "Probe the connection, then choose a listed model.");
            if (Reader.I(m[1]) == 0) throw Bad("model_unavailable", $"{model} was not listed by {c.Label} at the last probe.", "Probe again or choose another model.");
            var caps = JsonSerializer.Deserialize<ModelCapabilities>(Reader.S(m[0]), Protocol.Json) ?? ModelCapabilities.Unknown;
            if (caps.Text == false) throw Bad("incompatible", $"{model} does not produce text, which the {role} needs.", "Choose a text model.");
            if (c.IsCloud)
            {
                if (Settings(db, project).LocalOnly) throw Bad("local_only", "This project is local-only, so a role cannot use a cloud model.", "Turn off local-only for the project, or choose a local model.");
                if (ActiveConsent(db, project, connectionId, model) == null)
                    throw Bad("consent_required", $"The project has not agreed to send work to {model} on {c.Label}.", "Preview what would be sent and give consent first.");
            }
            TaskStore.Exec(db, "INSERT INTO assignments VALUES($p,$r,$c,$m,$at) ON CONFLICT(project_id,role) DO UPDATE SET connection_id=$c, model_id=$m, updated_at=$at",
                ("$p", project), ("$r", role), ("$c", connectionId), ("$m", model), ("$at", Now));
            return new(project, 0, "", null, null, $"{role}: {model} on {c.Label}");
        });

    public static CommandResult AddTaskFile(string workspace, string commandId, string taskId, string name, string content) =>
        TaskStore.Run(workspace, commandId, "file_supplied", db =>
        {
            var t = TaskStore.Task(db, taskId);
            name = Path.GetFileName(name.Trim());
            if (name.Length is 0 or > 120 || content.Length > 50_000) throw Bad("invalid_file", "A supplied file needs a name and at most 50,000 characters.", "Shorten it.");
            TaskStore.Exec(db, "INSERT INTO task_files VALUES($t,$n,$c) ON CONFLICT(task_id,name) DO UPDATE SET content=$c", ("$t", taskId), ("$n", name), ("$c", content));
            return new(taskId, t.Revision, taskId, null, null, $"supplied “{name}”");
        });

    // --- Reading ------------------------------------------------------------------------------------------------
    public static TeamView Read(string workspace, string project)
    {
        using var db = Reader.Open(workspace);
        var models = Reader.Rows(db, "SELECT connection_id, model_id, capabilities, available, discovered_at FROM connection_models ORDER BY model_id")
            .Select(r => (Conn: Reader.S(r[0]), Model: new ConnectionModel(Reader.S(r[1]), JsonSerializer.Deserialize<ModelCapabilities>(Reader.S(r[2]), Protocol.Json) ?? ModelCapabilities.Unknown, Reader.I(r[3]) != 0, Reader.S(r[4])))).ToList();
        var connections = Reader.Rows(db, "SELECT id,provider,endpoint,label,is_cloud,credential_status,credential_changed_at,last_probe_at,last_probe_ok,last_error FROM connections ORDER BY created_at")
            .Select(r => new ConnectionInfo(Reader.S(r[0]), Reader.S(r[1]), Reader.S(r[2]), Reader.S(r[3]), Reader.I(r[4]) != 0, Reader.S(r[5]), r[6] as string, r[7] as string,
                r[8] is null ? null : Reader.I(r[8]) != 0, r[9] as string, [.. models.Where(m => m.Conn == Reader.S(r[0])).Select(m => m.Model)])).ToList();
        var assignments = Reader.Rows(db, "SELECT role, connection_id, model_id FROM assignments WHERE project_id=$p", ("$p", project)).Select(r => new Assignment(Reader.S(r[0]), Reader.S(r[1]), Reader.S(r[2]))).ToList();
        var consents = Reader.Rows(db, "SELECT id, connection_id, model_id, files, granted_at FROM consents WHERE project_id=$p AND revoked_at IS NULL", ("$p", project))
            .Select(r => new Consent(Reader.S(r[0]), Reader.S(r[1]), Reader.S(r[2]), JsonSerializer.Deserialize<string[]>(Reader.S(r[3])) ?? [], Reader.S(r[4]))).ToList();
        var prices = Reader.Rows(db, "SELECT id,connection_id,model_id,currency,input_per_mtok,output_per_mtok,source,effective_at FROM prices ORDER BY effective_at DESC")
            .Select(r => new Price(Reader.S(r[0]), Reader.S(r[1]), Reader.S(r[2]), Reader.S(r[3]), Convert.ToDouble(r[4]), Convert.ToDouble(r[5]), Reader.S(r[6]), Reader.S(r[7]))).ToList();
        return new(connections, assignments, Settings(db, project), consents, prices, Budget(db, project));
    }

    internal static BudgetStatus Budget(SqliteConnection db, string project)
    {
        var s = Settings(db, project);
        double spent = Convert.ToDouble(TaskStore.Scalar<double?>(db, "SELECT COALESCE(sum(amount),0) FROM budget_ledger WHERE project_id=$p AND kind='settle' AND amount IS NOT NULL", ("$p", project)) ?? 0);
        // A reservation counts until it is released or settled at a known amount: an unknown charge keeps it, so the cap cannot be spent twice.
        double reserved = Convert.ToDouble(TaskStore.Scalar<double?>(db, """
            SELECT COALESCE(sum(r.amount),0) FROM budget_ledger r WHERE r.project_id=$p AND r.kind='reserve' AND r.amount IS NOT NULL
            AND NOT EXISTS (SELECT 1 FROM budget_ledger x WHERE x.attempt_id=r.attempt_id AND (x.kind='release' OR (x.kind='settle' AND x.amount IS NOT NULL)))
            """, ("$p", project)) ?? 0);
        int unknown = (int)(TaskStore.Scalar<long?>(db, "SELECT count(*) FROM budget_ledger WHERE project_id=$p AND kind='settle' AND amount IS NULL", ("$p", project)) ?? 0);
        // An unknown charge with nothing reserved (the price was unknown too) cannot be held against the cap; paid work is blocked until it is reconciled.
        int unresolved = (int)(TaskStore.Scalar<long?>(db, "SELECT count(*) FROM budget_ledger s WHERE s.project_id=$p AND s.kind='settle' AND s.amount IS NULL AND NOT EXISTS (SELECT 1 FROM budget_ledger r WHERE r.attempt_id=s.attempt_id AND r.kind='reserve' AND r.amount IS NOT NULL)", ("$p", project)) ?? 0);
        return new(s.BudgetLimit, s.Currency, spent, reserved, unknown, unresolved);
    }

    public static IReadOnlyList<(string Name, string Content)> Files(SqliteConnection db, string task) =>
        [.. Reader.Rows(db, "SELECT name, content FROM task_files WHERE task_id=$t ORDER BY name", ("$t", task)).Select(r => (Reader.S(r[0]), Reader.S(r[1])))];
}
