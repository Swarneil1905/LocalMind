using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub.Core;

public sealed record ManifestFile(string Path, string Sha256, long Size);
public sealed record ExportManifest(int Format, int Schema, string WorkspaceId, string CreatedAt, int Tasks, int Decisions, int Outputs, IReadOnlyList<ManifestFile> Files, string Excluded);
public sealed record RestoreResult(string DatabasePath, string WorkspaceId, int Tasks, int Decisions, int Outputs, string Note);
public sealed record ErasureReport(IReadOnlyList<string> Removed, IReadOnlyList<string> ExternalCopies);

/// <summary>Export, restore and erase. A package is plain files with a manifest of hashes; nothing is activated until every hash and the database itself have been checked.</summary>
public static class Portability
{
    public const int Format = 1;
    private static StoreException Bad(string code, string message, string recovery) => new(code, message, recovery);

    private static (int Tasks, int Decisions, int Outputs) Counts(string db)
    {
        using var c = ProjectStore.OpenRead(db);
        long N(string t) => TaskStore.Scalar<long>(c, $"SELECT count(*) FROM {t}");
        return ((int)N("tasks"), (int)N("decisions"), (int)N("outputs"));
    }

    private static string Sha(string file) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    private static void Backup(string source, string target)
    {
        using var src = ProjectStore.OpenRead(source);
        using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        dst.Open();
        src.BackupDatabase(dst);
    }

    /// <summary>Test seam: runs right after the database snapshot is taken, so a check can make an ordinary write land between the snapshot and the rest of the export.</summary>
    public static Action? AfterSnapshot { get; set; }

    /// <summary>
    /// Writes a package into an empty folder. The database is copied first with SQLite's backup API; everything else is derived from that snapshot, never from the live
    /// workspace: the event log is read from the snapshot and only the output files the snapshot names are copied. Credential references are removed (a key never lives in the
    /// database; the OS store holds it). The manifest names every file and its hash.
    /// </summary>
    public static ExportManifest Export(string workspace, string target)
    {
        string source = ProjectStore.DatabaseIn(workspace);
        target = Path.GetFullPath(target);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw Bad("destination_not_empty", "That folder is not empty.", "Choose an empty folder for the export.");
        Directory.CreateDirectory(target);
        string db = Path.Combine(target, ProjectStore.FileName);
        Backup(source, db);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
        {
            c.Open();
            TaskStore.Exec(c, "UPDATE connections SET credential_ref=NULL, credential_status='none', credential_changed_at=NULL");
            TaskStore.Exec(c, "VACUUM");
        }
        AfterSnapshot?.Invoke();
        File.WriteAllText(Path.Combine(target, "events.jsonl"), Replay.ExportEvents(db));
        string folder = Path.GetFullPath(Reader.Dir(workspace));
        List<string> blobPaths;
        using (var c = ProjectStore.OpenRead(db)) blobPaths = [.. Reader.Rows(c, "SELECT DISTINCT blob_path FROM outputs WHERE blob_path IS NOT NULL").Select(r => Reader.S(r[0]))];
        foreach (var rel in blobPaths)
        {
            string from = Path.GetFullPath(Path.Combine(folder, rel));
            if (!from.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Bad("bad_blob_path", "An output names a file outside the workspace.", "Nothing was exported.");
            if (!File.Exists(from)) throw Bad("missing_output_file", $"An output's file ({rel}) is missing from the workspace, so an exact export is not possible.", "Restore the file, or export after removing that output.");
            string to = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to);
        }
        var files = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "manifest.json")
            .Select(f => new ManifestFile(Path.GetRelativePath(target, f).Replace('\\', '/'), Sha(f), new FileInfo(f).Length)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var (t, d, o) = Counts(db);
        string id;
        using (var c = ProjectStore.OpenRead(db)) id = TaskStore.Scalar<string>(c, "SELECT id FROM workspace LIMIT 1") ?? "";
        var manifest = new ExportManifest(Format, ProjectStore.CurrentSchema, id, DateTime.UtcNow.ToString("O"), t, d, o, files, "API keys and credential references are not included; enter keys again after restoring.");
        File.WriteAllText(Path.Combine(target, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true }));
        return manifest;
    }

    /// <summary>Test seam: runs after validation and before the restored workspace is put in place.</summary>
    public static Action? BeforeActivation { get; set; }
    /// <summary>Test seam: runs after each file is copied into the staging folder, with the number copied so far.</summary>
    public static Action<int>? AfterStagedCopy { get; set; }

    /// <summary>
    /// Restores exactly what the manifest lists and nothing else. The package's files must equal the manifest's inventory (the database and the event log included), every one must match its
    /// hash, and the staged copies (the bytes that will actually be activated) are hashed again before anything changes. The workspace appears in the target only by one rename of the
    /// finished staging folder, so an interruption leaves the target exactly as it was. The restored workspace gets a new workspace ID, and older schemas are upgraded first.
    /// </summary>
    public static RestoreResult Restore(string package, string targetFolder, Func<string> newId)
    {
        package = Path.GetFullPath(package);
        targetFolder = Path.GetFullPath(targetFolder).TrimEnd('\\', '/');
        string manifestPath = Path.Combine(package, "manifest.json");
        if (!File.Exists(manifestPath)) throw Bad("no_manifest", "That folder is not an Agent Hub export: it has no manifest.json.", "Choose the export folder itself.");
        ExportManifest m;
        try { m = JsonSerializer.Deserialize<ExportManifest>(File.ReadAllText(manifestPath), Protocol.Json) ?? throw new JsonException(); }
        catch (JsonException) { throw Bad("bad_manifest", "The manifest could not be read.", "Export again."); }
        if (m.Format != Format) throw Bad("newer_export", $"This export uses format {m.Format}; this build reads format {Format}.", "Update LocalMind Agent Hub.");
        if (m.Schema > ProjectStore.CurrentSchema) throw Bad("newer_store", $"This export uses schema {m.Schema}; this build understands {ProjectStore.CurrentSchema}.", "Update LocalMind Agent Hub, then restore again.");
        var listed = m.Files.Select(f => f.Path).ToList();
        if (listed.Distinct(StringComparer.Ordinal).Count() != listed.Count || !listed.Contains(ProjectStore.FileName) || !listed.Contains("events.jsonl"))
            throw Bad("bad_manifest", "The manifest must list the database and the event log once each.", "Export again.");
        var present = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(package, f).Replace('\\', '/')).Where(f => f != "manifest.json").ToHashSet(StringComparer.Ordinal);
        if (present.Except(listed).FirstOrDefault() is { } extra) throw Bad("unlisted_file", $"{extra} is in the package but not in its manifest, so nothing was restored.", "Remove it, or export again.");
        foreach (var f in m.Files)
        {
            string full = Path.GetFullPath(Path.Combine(package, f.Path));
            if (!full.StartsWith(package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Bad("bad_manifest", "The manifest names a file outside the package.", "Export again.");
            if (!File.Exists(full)) throw Bad("missing_artifact", $"{f.Path} is missing from the export.", "Use the complete export folder.");
            if (Sha(full) != f.Sha256) throw Bad("hash_mismatch", $"{f.Path} does not match its recorded hash, so nothing was restored.", "The export was changed or damaged. Export again from the source workspace.");
        }
        if (Directory.Exists(targetFolder) && Directory.EnumerateFileSystemEntries(targetFolder).Any()) throw Bad("destination_not_empty", "That folder already holds files.", "Choose an empty folder.");
        string stage = Path.Combine(Path.GetDirectoryName(targetFolder) ?? targetFolder, ".restoring-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(stage);
        bool activated = false;
        try
        {
            int copied = 0;
            foreach (var f in m.Files)
            {
                string to = Path.Combine(stage, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(Path.Combine(package, f.Path), to);
                AfterStagedCopy?.Invoke(++copied);
            }
            foreach (var f in m.Files) // the staged bytes are what will be activated, so they are what is verified
                if (Sha(Path.Combine(stage, f.Path)) != f.Sha256) throw Bad("hash_mismatch", $"{f.Path} changed while it was being copied, so nothing was restored.", "Try again.");
            string stagedDb = Path.Combine(stage, ProjectStore.FileName);
            using (var c = ProjectStore.OpenRead(stagedDb))
                if (TaskStore.Scalar<string>(c, "PRAGMA integrity_check") != "ok") throw Bad("damaged_database", "The database in the export failed SQLite's integrity check.", "Export again from the source workspace.");
            var (t, d, o) = Counts(stagedDb);
            if (t != m.Tasks || d != m.Decisions || o != m.Outputs) throw Bad("count_mismatch", "The database does not hold the tasks, decisions and outputs the manifest recorded.", "Export again.");
            if (m.Schema < ProjectStore.CurrentSchema) Migration.Upgrade(stagedDb);
            string id = newId();
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = stagedDb, Pooling = false }.ToString()))
            {
                c.Open();
                TaskStore.Exec(c, "UPDATE workspace SET id=$i", ("$i", id));
                TaskStore.Exec(c, "UPDATE connections SET credential_ref=NULL, credential_status='none'");
            }
            BeforeActivation?.Invoke();
            if (Directory.Exists(targetFolder)) Directory.Delete(targetFolder); // empty, checked above
            Directory.Move(stage, targetFolder);
            activated = true;
            return new(Path.Combine(targetFolder, ProjectStore.FileName), id, t, d, o, "Restored under a new workspace ID. API keys were not part of the export: enter them again in Connections.");
        }
        finally { if (!activated) try { Directory.Delete(stage, true); } catch (IOException) { } }
    }

    /// <summary>
    /// Deletes the workspace's database, its rollback copy, stored output files and lock file, overwriting the database first. Overwriting is best effort on modern disks.
    /// The report says what this cannot reach.
    /// </summary>
    public static ErasureReport Erase(string workspace)
    {
        string folder = Reader.Dir(workspace), db = ProjectStore.DatabaseIn(workspace);
        List<string> cloud = [];
        if (File.Exists(db))
            using (var c = ProjectStore.OpenRead(db))
                cloud = [.. Reader.Rows(c, "SELECT DISTINCT cn.label, cn.endpoint FROM attempts a JOIN connections cn ON cn.id=a.connection_id WHERE cn.is_cloud=1 AND a.sent_at IS NOT NULL").Select(r => $"{Reader.S(r[0])} ({Reader.S(r[1])})")];
        List<string> removed = [];
        void Wipe(string file)
        {
            if (!File.Exists(file)) return;
            try { using (var fs = new FileStream(file, FileMode.Open, FileAccess.Write)) { var zeros = new byte[64 * 1024]; for (long left = fs.Length; left > 0; left -= zeros.Length) fs.Write(zeros, 0, (int)Math.Min(zeros.Length, left)); fs.Flush(true); } }
            catch (IOException) { }
            File.Delete(file);
            removed.Add(Path.GetFileName(file));
        }
        foreach (var name in new[] { ProjectStore.FileName, ProjectStore.FileName + "-journal", ProjectStore.FileName + ".old", ProjectStore.FileName + ".migrating", "agenthub.migration.json" }) Wipe(Path.Combine(folder, name));
        string blobs = Path.Combine(folder, "blobs");
        if (Directory.Exists(blobs)) { foreach (var f in Directory.EnumerateFiles(blobs)) Wipe(f); Directory.Delete(blobs, true); }
        List<string> external =
        [
            "Exports and backups you made of this workspace are separate files; erasing does not reach them. Delete them yourself.",
            "Windows file history, the Recycle Bin and any disk or cloud backup may hold earlier copies of the files that were removed.",
            "Your local model server (for example Ollama) may keep its own request logs.",
        ];
        if (cloud.Count > 0) external.Insert(0, "Cloud services that received packets keep what they received under their own terms: " + string.Join("; ", cloud) + ".");
        return new(removed, external);
    }
}
