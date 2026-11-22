using System.Runtime.InteropServices;

namespace LocalMind.AgentHub;

public enum PowerSignal { Suspend, Resume }

/// <summary>Where sleep and wake notices come from. A platform adapter, or a script in a check.</summary>
public interface ILifecycleSignals : IDisposable
{
    event Action<PowerSignal>? Signal;
    /// <summary>False when this platform has no supported signal; <see cref="Status"/> says why.</summary>
    bool Available { get; }
    string Status { get; }
}

/// <summary>Windows: the operating system's suspend/resume notification (powrprof). Physical suspend through this path is not verified by the checks.</summary>
public sealed class WindowsPowerSignals : ILifecycleSignals
{
    private delegate uint Callback(IntPtr context, uint type, IntPtr setting);
    [StructLayout(LayoutKind.Sequential)] private struct Subscribe { public Callback Routine; public IntPtr Context; }
    [DllImport("powrprof.dll")] private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref Subscribe recipient, out IntPtr handle);
    [DllImport("powrprof.dll")] private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr handle);

    private const uint DeviceNotifyCallback = 2, Suspend = 0x4, ResumeAutomatic = 0x12, ResumeSuspend = 0x7;
    private readonly Callback routine; // kept alive for as long as the registration is
    private Subscribe subscription;
    private IntPtr handle;
    public event Action<PowerSignal>? Signal;
    public bool Available { get; }
    public string Status { get; }

    public WindowsPowerSignals()
    {
        routine = (_, type, _) =>
        {
            if (type == Suspend) Signal?.Invoke(PowerSignal.Suspend);
            else if (type is ResumeAutomatic or ResumeSuspend) Signal?.Invoke(PowerSignal.Resume);
            return 0;
        };
        subscription = new() { Routine = routine, Context = IntPtr.Zero };
        try
        {
            uint rc = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, ref subscription, out handle);
            Available = rc == 0;
            Status = Available ? "Listening for Windows suspend and resume." : $"Windows refused the registration (code {rc}).";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { Status = "The Windows power notification is not available here."; }
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero) { PowerUnregisterSuspendResumeNotification(handle); handle = IntPtr.Zero; }
    }
}

/// <summary>Other platforms: no supported signal is wired, and the window says so rather than pretending.</summary>
public sealed class UnavailableSignals(string status) : ILifecycleSignals
{
    public event Action<PowerSignal>? Signal { add { } remove { } }
    public bool Available => false;
    public string Status => status;
    public void Dispose() { }
}

/// <summary>For checks: raises the same signal the platform would, through the same adapter.</summary>
public sealed class ScriptedSignals : ILifecycleSignals
{
    public event Action<PowerSignal>? Signal;
    public bool Available => true;
    public string Status => "Scripted signals.";
    public int Subscribers => Signal?.GetInvocationList().Length ?? 0;
    public void Raise(PowerSignal s) => Signal?.Invoke(s);
    public void Dispose() { }
}

/// <summary>Connects a signal source to the window's sleep and wake handling, and lets go of it on shutdown.</summary>
public sealed class LifecycleAdapter : IDisposable
{
    private readonly HubViewModel hub;
    private readonly ILifecycleSignals signals;
    private readonly Action<Action> post;
    private bool disposed;
    public int Delivered { get; private set; }
    public Task Last { get; private set; } = Task.CompletedTask;
    public string Status => signals.Status;
    public bool Available => signals.Available;

    public LifecycleAdapter(HubViewModel hub, ILifecycleSignals signals, Action<Action>? post = null)
    {
        (this.hub, this.signals) = (hub, signals);
        this.post = post ?? (a => a());
        signals.Signal += OnSignal;
    }

    private void OnSignal(PowerSignal s)
    {
        if (disposed) return;
        post(() =>
        {
            if (disposed) return;
            Delivered++;
            Last = s == PowerSignal.Suspend ? hub.SleepAsync() : hub.WakeAsync();
        });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        signals.Signal -= OnSignal;
        signals.Dispose();
    }
}
