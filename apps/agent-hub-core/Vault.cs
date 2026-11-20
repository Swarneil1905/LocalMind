using System.Runtime.InteropServices;
using System.Text;

namespace LocalMind.AgentHub.Core;

/// <summary>Thrown when the operating system's credential store cannot be used: missing, locked or refusing.</summary>
public sealed class VaultUnavailableException(string message) : Exception(message);

/// <summary>
/// Provider secrets live here and nowhere else. Only the worker holds an instance; the database keeps a reference and a status.
/// </summary>
public interface ICredentialVault
{
    void Write(string reference, string secret);
    string? Read(string reference);
    void Delete(string reference);
}

public static class Vaults
{
    /// <summary>The vault for this platform. macOS and Linux are not implemented yet and report themselves as unavailable.</summary>
    public static ICredentialVault ForPlatform() => OperatingSystem.IsWindows() ? new WindowsVault() : new UnavailableVault();
}

internal sealed class UnavailableVault : ICredentialVault
{
    private static VaultUnavailableException Fail() => new("This platform's credential store is not supported by Agent Hub yet, so keys cannot be stored.");
    public void Write(string reference, string secret) => throw Fail();
    public string? Read(string reference) => throw Fail();
    public void Delete(string reference) => throw Fail();
}

/// <summary>Windows Credential Manager, generic credentials, scoped to the signed-in user.</summary>
internal sealed class WindowsVault : ICredentialVault
{
    private const int Generic = 1, LocalMachinePersist = 2, NotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags, Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")] private static extern bool CredWrite(ref Credential c, int flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")] private static extern bool CredRead(string target, int type, int flags, out IntPtr c);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")] private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);

    public void Write(string reference, string secret)
    {
        byte[] blob = Encoding.Unicode.GetBytes(secret);
        var handle = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, handle, blob.Length);
            var c = new Credential { Type = Generic, TargetName = reference, CredentialBlob = handle, CredentialBlobSize = blob.Length, Persist = LocalMachinePersist, UserName = "LocalMind Agent Hub" };
            if (!CredWrite(ref c, 0)) throw new VaultUnavailableException($"Windows Credential Manager refused to store the key (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Array.Clear(blob);
            Marshal.FreeHGlobal(handle);
        }
    }

    public string? Read(string reference)
    {
        if (!CredRead(reference, Generic, 0, out var ptr))
        {
            int error = Marshal.GetLastWin32Error();
            return error == NotFound ? null : throw new VaultUnavailableException($"Windows Credential Manager could not be read (error {error}).");
        }
        try
        {
            var c = Marshal.PtrToStructure<Credential>(ptr);
            return c.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
        }
        finally { CredFree(ptr); }
    }

    public void Delete(string reference)
    {
        if (!CredDelete(reference, Generic, 0) && Marshal.GetLastWin32Error() is var e && e != NotFound)
            throw new VaultUnavailableException($"Windows Credential Manager could not delete the key (error {e}).");
    }
}

/// <summary>For checks: an in-memory vault that can be locked.</summary>
public sealed class MemoryVault : ICredentialVault
{
    private readonly Dictionary<string, string> items = [];
    public bool Locked { get; set; }
    private void Gate() { if (Locked) throw new VaultUnavailableException("The credential store is locked. Unlock it, then try again."); }
    public void Write(string reference, string secret) { Gate(); items[reference] = secret; }
    public string? Read(string reference) { Gate(); return items.GetValueOrDefault(reference); }
    public void Delete(string reference) { Gate(); items.Remove(reference); }
    public int Count => items.Count;
}
