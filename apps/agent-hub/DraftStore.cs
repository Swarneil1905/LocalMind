using System.Text.Json;

namespace LocalMind.AgentHub;

/// <summary>A review comment written but not sent. It belongs to one task and is anchored to the exact output and revision it was written about.</summary>
public sealed record ReviewDraft(string TaskId, string OutputId, int Revision, string Text);

/// <summary>What this window has typed but not sent, for one workspace: unsent replies by question ID and unsent review comments by task ID.</summary>
public sealed class DraftFile
{
    public Dictionary<string, string> Replies { get; set; } = [];
    public Dictionary<string, ReviewDraft> Reviews { get; set; } = [];
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
