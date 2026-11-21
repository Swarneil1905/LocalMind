using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub.Core;

public sealed record MigrationOutcome(string Chosen, string Detail, int Tasks, int Decisions);

/// <summary>Raised by a test at a named step to stand in for the process dying there. No cleanup runs, exactly as when a process is killed.</summary>
public sealed class MigrationInterrupted(string phase) : Exception($"Interrupted at {phase}.");

/// <summary>
/// Upgrades a workspace by copy: check it, copy it, upgrade the copy, verify the copy, then swap. A marker file records the phase, so a relaunch after an interruption at any step
/// opens a validated profile (the old one, or the new one when the swap had already gone far enough) with the same task and decision counts.
/// </summary>
public static class Migration
{
    /// <summary>Test seam: called at "preflight", "copy", "verify" and "swap" (between the two renames).</summary>
    public static Action<string>? InterruptAt { get; set; }

    private sealed record Marker(string Phase, int From, int Tasks, int Decisions, int Outputs);
    private static string MarkerPath(string folder) => Path.Combine(folder, "agenthub.migration.json");
    private static void Step(string phase) => InterruptAt?.Invoke(phase);

    private static (int Tasks, int Decisions, int Outputs, int Version, bool Ok) Inspect(string db)
    {
        using var c = ProjectStore.OpenRead(db);
        long N(string t) => TaskStore.Scalar<long>(c, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name=$n", ("$n", t)) == 0 ? 0 : TaskStore.Scalar<long>(c, $"SELECT count(*) FROM {t}");
        bool ok = TaskStore.Scalar<string>(c, "PRAGMA integrity_check") == "ok";
        return ((int)N("tasks"), (int)N("decisions"), (int)N("outputs"), ProjectStore.ReadVersion(c), ok);
    }

    public static bool UpgradeIfNeeded(string path)
    {
        int version;
        using (var db = ProjectStore.OpenRead(path)) version = ProjectStore.ReadVersion(db);
        if (version == ProjectStore.CurrentSchema) return false;
        Upgrade(path); // also refuses a store from a newer build, or a file that is not a workspace
        return true;
    }

    public static MigrationOutcome Upgrade(string path)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!, copy = path + ".migrating", old = path + ".old";
        // 1 · preflight: is this store healthy, and is there room for a second copy?
        var before = Inspect(path);
        if (before.Version < 0) throw new StoreException("not_a_workspace", "This file is not an Agent Hub or LocalMind project database.", "Choose a projects.db or agenthub.db file, or create a new workspace.");
        if (before.Version > ProjectStore.CurrentSchema)
            throw new StoreException("newer_store", $"This workspace uses schema {before.Version}; this build understands {ProjectStore.CurrentSchema}.", "Update LocalMind Agent Hub, then open the workspace again.");
        if (!before.Ok) throw new StoreException("damaged_database", "This workspace failed SQLite's integrity check, so it was not upgraded.", "Restore it from an export.");
        try
        {
            long size = new FileInfo(path).Length;
            if (new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace < size * 2 + 1_000_000)
                throw new StoreException("disk_full", "There is not enough free disk space to upgrade this workspace safely.", "Free some space, then open it again. Nothing was changed.");
        }
        catch (ArgumentException) { }
        File.WriteAllText(MarkerPath(folder), JsonSerializer.Serialize(new Marker("preflight", before.Version, before.Tasks, before.Decisions, before.Outputs)));
        Step("preflight");
        try
        {
            // 2 · copy, upgrade the copy, verify it
            File.WriteAllText(MarkerPath(folder), JsonSerializer.Serialize(new Marker("copy", before.Version, before.Tasks, before.Decisions, before.Outputs)));
            if (File.Exists(copy)) File.Delete(copy);
            using (var src = ProjectStore.OpenRead(path))
            using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                dst.Open();
                src.BackupDatabase(dst);
            }
            Step("copy");
            ProjectStore.Migrate(copy);
            var after = Inspect(copy);
            Step("verify");
            if (!after.Ok || after.Version != ProjectStore.CurrentSchema || after.Tasks != before.Tasks || after.Decisions != before.Decisions || after.Outputs != before.Outputs)
                throw new StoreException("migration_check_failed", "The upgraded copy did not match the original (task, decision or output counts differ), so it was discarded and the original is unchanged.", "Export the workspace and report this.");
            // 3 · swap
            File.WriteAllText(MarkerPath(folder), JsonSerializer.Serialize(new Marker("swap", before.Version, before.Tasks, before.Decisions, before.Outputs)));
            if (File.Exists(old)) File.Delete(old);
            File.Move(path, old);
            Step("swap");
            File.Move(copy, path);
            File.Delete(MarkerPath(folder));
            return new("new", $"Upgraded from schema {before.Version} to {ProjectStore.CurrentSchema}. The previous file is kept as {Path.GetFileName(old)}.", after.Tasks, after.Decisions);
        }
        catch (Exception e) when (e is not MigrationInterrupted)
        {
            try { if (File.Exists(copy)) File.Delete(copy); if (!File.Exists(path) && File.Exists(old)) File.Move(old, path); File.Delete(MarkerPath(folder)); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Called before a workspace is opened. After an interruption it leaves exactly one validated database in place: the old one when the copy was not finished,
    /// the new one when it was verified before the swap began, and it says which and why.
    /// </summary>
    public static MigrationOutcome Resolve(string folder)
    {
        string path = Path.Combine(folder, ProjectStore.FileName), copy = path + ".migrating", old = path + ".old", marker = MarkerPath(folder);
        if (!File.Exists(marker))
        {
            if (File.Exists(copy)) File.Delete(copy); // a copy without a marker was never part of a started upgrade
            return new("current", "No upgrade was interrupted.", 0, 0);
        }
        Marker m;
        try { m = JsonSerializer.Deserialize<Marker>(File.ReadAllText(marker)) ?? throw new JsonException(); }
        catch (JsonException) { File.Delete(marker); return new("current", "An unreadable upgrade marker was removed.", 0, 0); }
        bool Matches(string db, int version) { try { var i = Inspect(db); return i.Ok && i.Version == version && i.Tasks == m.Tasks && i.Decisions == m.Decisions && i.Outputs == m.Outputs; } catch (Exception e) when (e is SqliteException or IOException or StoreException) { return false; } }
        MigrationOutcome Done(string chosen, string detail) { File.Delete(marker); var i = Inspect(path); return new(chosen, detail, i.Tasks, i.Decisions); }
        if (m.Phase is "preflight" or "copy")
        {
            foreach (var f in new[] { copy, copy + "-journal" }) if (File.Exists(f)) File.Delete(f);
            return Done("old", $"The upgrade stopped during {m.Phase}. The original workspace is unchanged and was checked.");
        }
        // swap: three possible states
        if (File.Exists(path) && !File.Exists(old)) return Done("new", "The upgrade had finished swapping; the new workspace was checked.");
        if (File.Exists(path) && File.Exists(old) && !File.Exists(copy)) return Done("new", "The upgrade had finished swapping; the new workspace was checked.");
        if (!File.Exists(path) && File.Exists(copy) && Matches(copy, ProjectStore.CurrentSchema)) { File.Move(copy, path); return Done("new", "The upgrade stopped between the two renames. The verified new workspace was put in place."); }
        if (!File.Exists(path) && File.Exists(old)) { if (File.Exists(copy)) File.Delete(copy); File.Move(old, path); return Done("old", "The upgrade stopped between the two renames and the copy could not be verified. The original was put back."); }
        return Done("current", "The upgrade marker did not match any file state and was removed.");
    }
}
