using LocalMind.AgentHub.Core;

namespace LocalMind.AgentHub;

/// <summary>AH56 · push-to-talk makes an editable draft in the message box, addressed to the recipient shown beside it. Sending is the usual Preview and Send.</summary>
public sealed partial class HubViewModel
{
    public IMicrophone Mic { get; set; } = LaunchOptions.VoiceDenied ? new DeniedMicrophone()
        : LaunchOptions.VoiceFixture is { } f && System.IO.File.Exists(f) ? new FixtureMicrophone(System.IO.File.ReadAllBytes(f)) : new NoMicrophone();
    /// <summary>No speech-to-text service ships in this build; a check or a launch option scripts one. Without it a recording is discarded and the owner is told.</summary>
    public ITranscriber? Transcriber { get; set; } = LaunchOptions.VoiceText is { } t ? new ScriptedTranscriber(t, LaunchOptions.VoiceDelay) : null;

    private CancellationTokenSource? voiceCancel; private TaskCompletionSource? voiceReleased; private Task<string?>? voiceRun;
    public bool VoiceListening => voiceRun != null;
    public string VoiceButtonLabel => VoiceListening ? "Stop and make a draft" : "Speak a message";
    public string VoiceStatus { get; private set; } = "";
    public WorkspaceCommand VoiceCommand { get; private set; } = null!;
    public WorkspaceCommand VoiceCancelCommand { get; private set; } = null!;

    private string VoiceOrigin() => $"{composerWorkspace}|{composerProject}|{selectedTask?.Id}|{ComposerRecipient}";

    private void VoiceChanged() { foreach (var n in new[] { nameof(VoiceListening), nameof(VoiceButtonLabel), nameof(VoiceStatus), nameof(ComposerNote) }) Notify(n); RaiseCommands(); }

    private WorkspaceCommand[] InitDay9()
    {
        VoiceCommand = new(_ => IsWorkspace && selectedTask != null && CanWrite, async _ =>
        {
            if (voiceRun != null) { voiceReleased!.TrySetResult(); await voiceRun; return; } // second press: release
            if (Mic.Access != MicAccess.Granted) { VoiceStatus = Mic.AccessNote; VoiceChanged(); return; }
            voiceCancel = new(); voiceReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancel = voiceCancel.Token; var released = voiceReleased.Task; string recipient = ComposerRecipient, origin = VoiceOrigin();
            voiceRun = RecordDraft(released, cancel);
            VoiceStatus = $"Listening. A draft for {recipient} appears when you stop. Nothing is sent."; VoiceChanged();
            var result = await voiceRun;
            voiceRun = null; voiceCancel?.Dispose(); voiceCancel = null;
            if (result != null && VoiceOrigin() != origin)
            {   // The owner moved to another task, workspace or recipient while this was transcribed: the words are not added there. They are shown here so nothing is lost.
                VoiceStatus = $"The draft was not added because the task, workspace or recipient changed while it was being made. It was meant for {recipient}. What was heard: “{result}”. Your current message is unchanged and nothing was sent.";
            }
            else if (result != null) { InstructionText = InstructionText.Trim().Length == 0 ? result : InstructionText.TrimEnd() + "\n" + result; Notify(nameof(InstructionText)); ComposerNote = $"Voice draft, not sent. It goes to {ComposerRecipient} only when you choose Preview or Send. Edit it first."; VoiceStatus = "Draft added to the message box."; }
            VoiceChanged();
        });
        VoiceCancelCommand = new(_ => VoiceListening, _ => { voiceCancel?.Cancel(); return Task.CompletedTask; });
        return [VoiceCommand, VoiceCancelCommand, .. InitVisuals()];
    }

    /// <summary>Returns the transcript, or null when nothing should change in the message box (cancelled, failed, nothing heard). Typed text is never touched here.</summary>
    private async Task<string?> RecordDraft(Task released, CancellationToken cancel)
    {
        try
        {
            var wav = await Mic.RecordUntilAsync(released, cancel);
            if (Transcriber == null) { VoiceStatus = "The recording was discarded: this build has no speech-to-text service set up, so it cannot make a draft. What you typed is unchanged."; return null; }
            string text = (await Transcriber.TranscribeAsync(wav, cancel)).Trim();
            if (text.Length == 0) { VoiceStatus = "No words were recognised, so no draft was made. What you typed is unchanged."; return null; }
            return text;
        }
        catch (OperationCanceledException) { VoiceStatus = "Voice recording cancelled. Nothing was kept, and what you typed is unchanged."; return null; }
        catch (Exception e) when (e is InvalidOperationException or IOException) { VoiceStatus = e.Message + " What you typed is unchanged."; return null; }
    }
}
