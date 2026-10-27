import { Mic, Square, Volume2 } from "lucide-react";
import type { useVoice } from "../hooks/useVoice";
import "./voice.css";

export function VoiceControls({ voice, disabled }: { voice: ReturnType<typeof useVoice>; disabled: boolean }) {
  const active = voice.phase !== "idle";
  return <div className="voice-controls">
    <button className="voice-toggle" type="button" aria-label="Voice controls" aria-expanded={voice.open}
      onClick={() => voice.setOpen(!voice.open)}><Mic size={17} aria-hidden="true" /><span>Voice</span></button>
    <span className="voice-phase" role="status" aria-live="polite">
      {voice.phase === "permission" ? "Requesting microphone…" : voice.phase === "listening" ? "Listening" :
        voice.phase === "transcribing" ? "Transcribing on this device…" : voice.phase === "review" ? "Review your transcript" :
          voice.phase === "responding" ? "Preparing your reply…" : voice.phase === "speaking" ? "Speaking" : "Microphone off"}
    </span>
    {voice.phase === "listening" && <span className="voice-phase" aria-hidden="true">{voice.seconds}s / 30s</span>}
    {active && <button type="button" className="voice-toggle" onClick={() => voice.cancel()} aria-label="Interrupt voice turn"><Square size={14} />Stop voice</button>}
    {voice.open && <section className="voice-panel" aria-label="Local voice controls">
      <div className="voice-fields">
        <label>Microphone<select value={voice.preferences.device} disabled={active} onChange={event => voice.updatePreferences({ device: event.target.value })}>
          <option value="">System default (selected at start)</option>
          {voice.preferences.device && !voice.devices.some(item => item.deviceId === voice.preferences.device) && <option value={voice.preferences.device}>Saved microphone unavailable</option>}
          {voice.devices.filter(item => item.deviceId).map((item, i) => <option key={item.deviceId} value={item.deviceId}>{item.label || `Microphone ${i + 1}`}</option>)}
        </select></label>
        <label>Reply voice<select value={voice.preferences.voice} disabled={active} onChange={event => voice.updatePreferences({ voice: event.target.value })}>
          <option value="">Choose an installed voice</option>
          {voice.preferences.voice && !voice.status?.voices.some(item => item.id === voice.preferences.voice) && <option value={voice.preferences.voice}>Saved voice unavailable</option>}
          {voice.status?.voices.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
        </select></label>
      </div>
      <label className="voice-read"><input type="checkbox" checked={voice.preferences.readReplies} disabled={active}
        onChange={event => voice.updatePreferences({ readReplies: event.target.checked })} /><Volume2 size={16} />Speak replies to voice messages</label>
      <div className="voice-buttons">
        <button type="button" className="page-button primary" disabled={disabled || (active && voice.phase !== "listening" && voice.phase !== "speaking" && voice.phase !== "responding")}
          onClick={() => voice.phase === "listening" ? void voice.finish() : void voice.start()}>
          {voice.phase === "listening" ? "Finish recording" : voice.phase === "speaking" || voice.phase === "responding" ? "Interrupt and record" : "Start recording"}
        </button>
        <button type="button" className="page-button" disabled={disabled || (active && voice.phase !== "permission" && voice.phase !== "listening")}
          onPointerDown={event => { event.preventDefault(); event.currentTarget.setPointerCapture?.(event.pointerId); void voice.start(); }}
          onPointerUp={() => { if (voice.phase === "listening") void voice.finish(); else voice.cancel(); }} onPointerCancel={() => voice.cancel()}
          onKeyDown={event => { if ((event.key === " " || event.key === "Enter") && !event.repeat) { event.preventDefault(); void voice.start(); } }}
          onKeyUp={event => { if (event.key === " " || event.key === "Enter") { event.preventDefault(); if (voice.phase === "listening") void voice.finish(); else voice.cancel(); } }}>Hold to talk</button>
      </div>
      {voice.phase === "review" && <div className="voice-review">
        <label>Review transcript<textarea rows={3} value={voice.transcript} onChange={event => voice.setTranscript(event.target.value)} /></label>
        <div className="voice-buttons"><button type="button" className="page-button primary" disabled={!voice.transcript.trim()} onClick={voice.useTranscript}>Add to message</button>
          <button type="button" className="page-button" onClick={() => voice.cancel()}>Discard recording</button></div>
      </div>}
      <p>Audio stays on this device. Review the transcript before sending. Existing draft text is kept.</p>
      <p className="voice-help">Ctrl/⌘ + Shift + V starts or finishes recording. Escape stops voice. Setup is in Voice in the sidebar.</p>
    </section>}
    {voice.error && <p className="voice-error" role="alert">{voice.error}</p>}
  </div>;
}
