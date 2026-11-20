namespace LocalMind.AgentHub.Core;

/// <summary>
/// One Agent Hub window owns writes to a workspace. The owner holds an exclusive lock on a small file beside the database; the OS
/// releases it if the process dies, so a crash never leaves a stale owner. A window that cannot take it opens read-only and can name the owner.
/// </summary>
public sealed class WorkspaceLock : IDisposable
{
    private FileStream? stream;
    public bool Held => stream != null;
    public int OwnerProcessId { get; }
    private WorkspaceLock(FileStream? stream, int owner) => (this.stream, OwnerProcessId) = (stream, owner);

    public static WorkspaceLock TryAcquire(string folderOrDatabase)
    {
        string folder = File.Exists(folderOrDatabase) ? Path.GetDirectoryName(Path.GetFullPath(folderOrDatabase))! : Path.GetFullPath(folderOrDatabase);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, ".agenthub.lock");
        try
        {
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            fs.SetLength(0);
            fs.Write(System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId.ToString()));
            fs.Flush();
            return new(fs, Environment.ProcessId);
        }
        catch (IOException)
        {
            try
            {
                using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(read);
                return new(null, int.TryParse(reader.ReadToEnd().Trim(), out int pid) ? pid : 0);
            }
            catch (IOException) { return new(null, 0); }
        }
    }

    public void Dispose()
    {
        stream?.Dispose();
        stream = null;
    }
}
