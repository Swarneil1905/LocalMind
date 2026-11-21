using System.Text.Json;

namespace LocalMind.AgentHub;

/// <summary>A review comment written but not sent. It belongs to one task and is anchored to the exact output and revision it was written about.</summary>
public sealed record ReviewDraft(string TaskId, string OutputId, int Revision, string Text);

public sealed record AttachmentRef(string Name, string Hash, string Mime, int Width, int Height);

/// <summary>A message being written for one project: its text, who it is for, and the images waiting to go with it (by reference; the bytes are kept beside the draft).</summary>
public sealed record ComposerDraft(string ProjectId, string? TaskId, string Recipient, string Text, List<AttachmentRef> Attachments, bool RecipientMissing = false);

/// <summary>What this window has typed but not sent, for one workspace: unsent replies by question ID, unsent review comments by task ID, and the message being composed per project.</summary>
public sealed class DraftFile
{
    public Dictionary<string, string> Replies { get; set; } = [];
    public Dictionary<string, ReviewDraft> Reviews { get; set; } = [];
    public Dictionary<string, ComposerDraft> Composer { get; set; } = [];
}

/// <summary>
/// Local, per-user drafts. They are not workspace data: another window or another person never sees them, and the worker never writes them.
/// The file is keyed by the workspace's own ID, so a draft stays with its workspace and never follows the user into another one.
/// Failures to read or write are swallowed: losing a draft is better than failing the window.
/// </summary>
public sealed class DraftStore(string folder)
{
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalMind", "AgentHub", "drafts");

    private string FileFor(string workspaceKey) => Path.Combine(folder, new string([.. workspaceKey.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_')]) + ".json");

    public DraftFile Load(string workspaceKey)
    {
        try { return File.Exists(FileFor(workspaceKey)) ? JsonSerializer.Deserialize<DraftFile>(File.ReadAllText(FileFor(workspaceKey))) ?? new() : new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    private string BlobFolder(string workspaceKey) => Path.Combine(folder, new string([.. workspaceKey.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_')]) + ".blobs");

    /// <summary>An attachment's bytes, filed by hash under its own workspace.</summary>
    public void SaveBlob(string workspaceKey, string hash, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(BlobFolder(workspaceKey));
            string target = Path.Combine(BlobFolder(workspaceKey), hash + ".bin");
            if (!File.Exists(target)) File.WriteAllBytes(target, bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public byte[]? LoadBlob(string workspaceKey, string hash)
    {
        try { string p = Path.Combine(BlobFolder(workspaceKey), hash + ".bin"); return File.Exists(p) ? File.ReadAllBytes(p) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Save(string workspaceKey, DraftFile file)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string target = FileFor(workspaceKey), temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file));
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
