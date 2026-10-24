import { useEffect, useRef, useState } from "react";
import { Mic, Square } from "lucide-react";
import type { RecordingAdapter } from "../lib/syntheticRecorder";
import "./voice-preview.css";

export function VoiceRecorder({ recorder }: { recorder: RecordingAdapter }) {
  const [permission, setPermission] = useState(false);
  const [device, setDevice] = useState(recorder.devices[0]?.id ?? "");
  const [phase, setPhase] = useState<"idle" | "permission" | "recording" | "processing" | "editable">("idle");
  const [transcript, setTranscript] = useState("");
  const [error, setError] = useState("");
  const [seconds, setSeconds] = useState(0);
  const active = useRef(false);
  const epoch = useRef(0);
  const pending = useRef<AbortController | null>(null);
  const holding = useRef(false);

  function cancel() {
    epoch.current++; active.current = false; holding.current = false;
    pending.current?.abort(); pending.current = null; recorder.cancel();
    setPhase(transcript ? "editable" : "idle"); setSeconds(0); setError("");
  }
  useEffect(() => {
    const generation = epoch;
    const blur = () => {
      epoch.current++; active.current = false; holding.current = false;
      pending.current?.abort(); pending.current = null; recorder.cancel();
      setPhase(current => current === "recording" || current === "processing" || current === "permission" ? "idle" : current);
    };
    window.addEventListener("blur", blur);
    return () => { generation.current++; active.current = false; pending.current?.abort(); recorder.cancel(); window.removeEventListener("blur", blur); };
  }, [recorder]);

  async function finish() {
    if (!active.current) return;
    active.current = false; holding.current = false; setPhase("processing");
    const version = ++epoch.current;
    const controller = new AbortController(); pending.current = controller;
    try {
      const text = await recorder.finish(controller.signal);
      if (version === epoch.current) { setTranscript(text); setPhase("editable"); }
    } catch (cause) {
      if (version === epoch.current) { setError(String(cause)); setPhase("idle"); }
    } finally { if (version === epoch.current) pending.current = null; }
  }
  const finishRef = useRef(finish);
  useEffect(() => { finishRef.current = finish; });
  useEffect(() => {
    if (phase !== "recording") return;
    const started = Date.now();
    const timer = setInterval(() => {
      const elapsed = Math.floor((Date.now() - started) / 1000);
      setSeconds(elapsed);
      if (elapsed >= 30) void finishRef.current();
    }, 250);
    return () => clearInterval(timer);
  }, [phase]);

  function start() {
    if (!permission || !device || active.current || pending.current || phase === "permission") return;
    try { recorder.start(device); active.current = true; setSeconds(0); setError(""); setPhase("recording"); }
    catch (cause) { setError(String(cause)); }
  }
  const busy = phase === "recording" || phase === "processing" || phase === "permission";
  const cannotStart = !permission || !device || phase === "processing" || phase === "permission";
  return <section className="voice-card" aria-label="Simulated recording controls">
    <label className="voice-field">Input device<select className="page-input" value={device} disabled={busy} onChange={event => setDevice(event.target.value)}>
      {!recorder.devices.length && <option value="">No input device</option>}
      {recorder.devices.map(input => <option value={input.id} key={input.id}>{input.label}</option>)}
    </select></label>
    <p>Selected input: {recorder.devices.find(input => input.id === device)?.label ?? "None"}</p>
    {!permission && <button className="page-button" disabled={busy} onClick={async () => {
      const version = ++epoch.current; setPhase("permission"); setError("");
      try { await recorder.requestPermission(); if (version === epoch.current) { setPermission(true); setPhase("idle"); } }
      catch (cause) { if (version === epoch.current) { setError(String(cause)); setPhase("idle"); } }
    }}>Allow simulated microphone</button>}
    {error && <p role="alert">{error}</p>}
    <div className="voice-status" role="status" aria-live="polite">
      {phase === "recording" ? <><span className="recording-dot" />Simulated recording · {seconds}s / 30s</> :
        phase === "processing" ? "Preparing sample transcript…" : phase === "permission" ? "Checking simulated permission…" :
          phase === "editable" ? "Sample transcript ready to edit" : "Ready. No audio is being captured."}
    </div>
    <div className="voice-actions">
      <button className="page-button primary" disabled={cannotStart} onClick={() => active.current ? void finish() : start()}>
        {phase === "recording" ? <><Square size={16} />Stop sample recording</> : <><Mic size={16} />Start sample recording</>}
      </button>
      <button className="page-button" disabled={cannotStart} onPointerDown={event => {
        event.preventDefault(); if (active.current) return;
        holding.current = true; event.currentTarget.setPointerCapture?.(event.pointerId); start();
      }} onPointerUp={() => { if (holding.current) void finish(); }} onPointerCancel={cancel}
        onKeyDown={event => { if ((event.key === " " || event.key === "Enter") && !event.repeat && !active.current) { event.preventDefault(); holding.current = true; start(); } }}
        onKeyUp={event => { if ((event.key === " " || event.key === "Enter") && holding.current) { event.preventDefault(); void finish(); } }}>Hold to talk</button>
      <button className="page-button" disabled={!busy} onClick={cancel}>Cancel recording</button>
    </div>
    <label className="voice-field">Editable sample transcript<textarea className="page-input" rows={5} value={transcript} disabled={busy} onChange={event => setTranscript(event.target.value)} placeholder="A sample transcript appears after you stop." /></label>
    <p className="page-footnote">Cancelling discards the new recording and keeps your previous draft. Nothing is sent automatically. This draft is cleared when you leave Voice.</p>
  </section>;
}
