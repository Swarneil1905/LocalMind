import { useEffect, useState } from "react";
import { ArrowRight } from "lucide-react";
import { voiceRequest, type VoiceStatus } from "../lib/voice";
import "./supporting-pages.css";
import "./voice.css";
import "./voice-preview.css";

export function BuddyPage({
  onNavigateToChat,
}: {
  onNavigateToChat?: () => void;
}) {
  const [status, setStatus] = useState<VoiceStatus | null>(null);
  const [error, setError] = useState("");
  const [poll, setPoll] = useState(0);
  const [starting, setStarting] = useState(false);
  useEffect(() => {
    let live = true;
    void voiceRequest<VoiceStatus>("status").then(result => { if (live) setStatus(result); })
      .catch(cause => { if (live) setError(String(cause)); });
    return () => { live = false; };
  }, [poll]);
  useEffect(() => {
    if (status?.install.phase !== "downloading") return;
    const timer = setInterval(() => setPoll(value => value + 1), 1000);
    return () => clearInterval(timer);
  }, [status?.install.phase]);
  const downloading = starting || status?.install.phase === "downloading";
  return (
    <div className="workspace-page">
      <div className="workspace-page-inner">
        <header className="page-heading">
          <div>
            <h1>Voice</h1>
            <p>Speak a draft. Review it. Hear a reply.</p>
          </div>
          <span className="page-badge">Local voice · Windows pilot</span>
        </header>
        <section className="voice-setup">
          {status?.disabled_reason && <p className="page-notice" role="status">{status.disabled_reason}</p>}
          <div className="voice-card">
            <h2>Local transcription</h2>
            <p>Whisper turns English recordings into editable text on your CPU. Windows stock voices read replies without a cloud speech service.</p>
            <p>No microphone is activated from this page. Recordings are limited to 30 seconds; temporary audio is removed after processing or cancellation.</p>
            <p role="status">{!status ? "Checking local speech…" : status.disabled_reason ? "Text chat is available while voice checks are pending." : status.stt_ready ? "Transcription is installed and ready." : !status.supported ? "Voice setup is currently available on Windows x64. Text chat works on every supported platform." : "Install the speech runtime and English model to begin (about 157 MB)."}</p>
            {status?.supported && !status.stt_ready && <button className="page-button primary" disabled={downloading} onClick={async () => {
              setStarting(true); setError("");
              try { await voiceRequest("install"); setPoll(value => value + 1); }
              catch (cause) { setError(String(cause)); }
              finally { setStarting(false); }
            }}>{downloading ? "Installing speech…" : "Download local speech"}</button>}
            {downloading && <><progress aria-label="Speech download progress" max={status?.install.total || 1} value={status?.install.downloaded || 0} /><p>{Math.round((status?.install.downloaded || 0) / 1_000_000)} MB downloaded</p></>}
            {(error || status?.install.error) && <p role="alert">{error || status?.install.error}<button className="page-button" onClick={() => { setError(""); setPoll(value => value + 1); }}>Refresh status</button></p>}
          </div>
          <div className="voice-card"><h2>You control each turn</h2>
            <ol><li>Open chat and choose Voice below the composer.</li><li>Choose your microphone and an installed reply voice.</li><li>Start recording, or hold the talk button. Finish to transcribe.</li><li>Edit the transcript, add it to your message, and send when ready.</li></ol>
            <p>Ctrl/⌘ + Shift + V starts or finishes recording. Escape interrupts playback and generation. Leaving the window releases the microphone. Spoken replies always remain available as text.</p>
            <p>Voice preferences stay on this device. No wake word, background listening, or voice cloning.</p>
            <p>{status?.voices.length ? `Installed voices: ${status.voices.map(item => item.name).join(", ")}.` : status?.speech_error || "Installed voices will appear here when the local service is ready."}</p>
            {status?.speech_error && <button className="page-button" onClick={() => setPoll(value => value + 1)}>Retry voice discovery</button>}
          </div>
          {onNavigateToChat && (
            <button className="page-button primary" onClick={onNavigateToChat}>
              Open chat <ArrowRight size={15} />
            </button>
          )}
        </section>
      </div>
    </div>
  );
}
