import { useCallback, useEffect, useRef, useState } from "react";
import type { Message } from "../types";
import { LocalMicrophone, LocalSpeechPlayer, nextSpeechChunk, saveVoicePreferences, voicePreferences, voiceRequest,
  type Microphone, type SpeechPlayer, type VoiceStatus } from "../lib/voice";

export type VoicePhase = "idle" | "permission" | "listening" | "transcribing" | "review" | "responding" | "speaking";
interface Options {
  value: string;
  onChange(value: string): void;
  onSend(text: string, turnId?: string): Promise<boolean>;
  onStop(): void;
  messages: Message[];
  isStreaming: boolean;
  disabled: boolean;
  scope: string;
  microphone?: Microphone;
  player?: SpeechPlayer;
}
interface Turn {
  id: string; ready: boolean; sent: boolean; read: boolean; voice: string;
  consumed: number; queue: string[]; pumping: boolean; finished: boolean;
}

export function useVoice(options: Options) {
  const latest = useRef(options); latest.current = options;
  const microphone = useRef(options.microphone ?? new LocalMicrophone()).current;
  const player = useRef(options.player ?? new LocalSpeechPlayer()).current;
  const turn = useRef<Turn | null>(null);
  const [phase, setPhase] = useState<VoicePhase>("idle");
  const [open, setOpen] = useState(false);
  const [status, setStatus] = useState<VoiceStatus | null>(null);
  const [devices, setDevices] = useState<MediaDeviceInfo[]>([]);
  const [preferences, setPreferences] = useState(voicePreferences);
  const prefs = useRef(preferences); prefs.current = preferences;
  const [transcript, setTranscript] = useState("");
  const [error, setError] = useState("");
  const [seconds, setSeconds] = useState(0);
  const mounted = useRef(true);

  const cancel = useCallback((message = "") => {
    const old = turn.current; turn.current = null;
    microphone.cancel(); player.stop();
    if (old) {
      void voiceRequest("cancel", { turn_id: old.id }).catch(() => {});
      if (old.sent) latest.current.onStop();
    }
    if (mounted.current) { setPhase("idle"); setTranscript(""); setSeconds(0); setError(message); }
  }, [microphone, player]);

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; cancel(); };
  }, [cancel]);
  useEffect(() => { cancel(); }, [options.scope, cancel]);
  useEffect(() => {
    // Losing focus may mean the device is going to sleep. Never retain a mic handle.
    const blur = () => { if (turn.current) cancel("Voice stopped when the window lost focus. Your typed draft is safe."); };
    const hidden = () => { if (document.hidden) blur(); };
    // OS sleep does not reliably emit a webview blur event. On resume, expire
    // any turn whose event loop was suspended rather than resuming old audio.
    let lastTick = Date.now();
    const wakeGuard = setInterval(() => {
      const now = Date.now();
      if (turn.current && now - lastTick > 5000) cancel("Voice stopped while the app was suspended. Start recording again when ready.");
      lastTick = now;
    }, 500);
    const changed = () => {
      void microphone.devices().then(inputs => {
        if (!mounted.current) return;
        setDevices(inputs);
        if (turn.current && (!prefs.current.device || !inputs.some(input => input.deviceId === prefs.current.device))) {
          cancel("Your microphone changed. Select an input and start again.");
        }
      }).catch(() => cancel("Microphone devices are unavailable."));
    };
    window.addEventListener("blur", blur); window.addEventListener("pagehide", blur);
    document.addEventListener("visibilitychange", hidden);
    navigator.mediaDevices?.addEventListener("devicechange", changed);
    return () => {
      clearInterval(wakeGuard);
      window.removeEventListener("blur", blur); window.removeEventListener("pagehide", blur);
      document.removeEventListener("visibilitychange", hidden);
      navigator.mediaDevices?.removeEventListener("devicechange", changed);
    };
  }, [cancel, microphone]);

  const refresh = useCallback(async () => {
    try {
      const result = await voiceRequest<VoiceStatus>("status");
      if (mounted.current) setStatus(result);
      const inputs = await microphone.devices();
      if (mounted.current) setDevices(inputs);
    } catch (cause) { if (mounted.current) setError(String(cause)); }
  }, [microphone]);

  const start = useCallback(async () => {
    if (latest.current.disabled) return;
    const wasVoiceResponse = turn.current?.sent;
    cancel();
    if (latest.current.isStreaming && !wasVoiceResponse) latest.current.onStop();
    setOpen(true); setError("");
    const current: Turn = { id: crypto.randomUUID(), ready: false, sent: false, read: prefs.current.readReplies,
      voice: prefs.current.voice, consumed: 0, queue: [], pumping: false, finished: false };
    turn.current = current; setPhase("permission");
    try {
      const available = await voiceRequest<VoiceStatus>("status");
      if (turn.current !== current) return;
      setStatus(available);
      if (!available?.stt_ready) throw new Error("Install local transcription from Voice in the sidebar first.");
      const inputs = await microphone.devices();
      if (turn.current !== current) return;
      setDevices(inputs);
      if (prefs.current.device && !inputs.some(input => input.deviceId === prefs.current.device)) throw new Error("Your saved microphone is missing. Select an available input.");
      if (current.read && !available.voices.some(voice => voice.id === current.voice)) throw new Error("Select an installed voice, or turn off spoken replies.");
      await microphone.start(prefs.current.device, () => cancel("Microphone disconnected. Select an input and try again."));
      if (turn.current !== current) return;
      const activeInputs = await microphone.devices();
      if (turn.current !== current) return;
      setDevices(activeInputs); setSeconds(0); setPhase("listening");
    } catch (cause) {
      if (turn.current === current) cancel(`Could not start recording: ${String(cause)}`);
    }
  }, [cancel, microphone]);

  const finish = useCallback(async () => {
    const current = turn.current;
    if (!current || phase !== "listening") return;
    setPhase("transcribing");
    try {
      const audio = await microphone.finish();
      if (turn.current !== current) return;
      const result = await voiceRequest<{ turn_id: string; text: string }>("transcribe", { turn_id: current.id, audio });
      if (turn.current !== current || result.turn_id !== current.id) return;
      setTranscript(result.text); setPhase("review");
    } catch (cause) { if (turn.current === current) cancel(String(cause)); }
  }, [phase, microphone, cancel]);
  const finishRef = useRef(finish); finishRef.current = finish;
  useEffect(() => {
    if (phase !== "listening") return;
    const began = Date.now();
    const timer = setInterval(() => {
      const elapsed = Math.floor((Date.now() - began) / 1000); setSeconds(elapsed);
      if (elapsed >= 30) void finishRef.current();
    }, 200);
    return () => clearInterval(timer);
  }, [phase]);

  const pump = useCallback(async (current: Turn) => {
    if (current.pumping || turn.current !== current) return;
    current.pumping = true;
    try {
      while (current.queue.length && turn.current === current) {
        const text = current.queue.shift()!;
        const result = await voiceRequest<{ turn_id: string; audio: string }>("speak", { turn_id: current.id, voice: current.voice, text });
        if (turn.current !== current || result.turn_id !== current.id) return;
        setPhase("speaking");
        await player.play(result.audio);
      }
    } catch (cause) {
      if (turn.current === current) { current.read = false; current.queue = []; player.stop(); setError(`Speech stopped: ${String(cause)}. You can still read the reply.`); }
    } finally {
      current.pumping = false;
      if (turn.current === current) {
        setPhase(current.finished ? "idle" : "responding");
        if (current.finished) turn.current = null;
      }
    }
  }, [player]);

  useEffect(() => {
    const current = turn.current;
    if (!current?.sent) return;
    const answer = options.messages.find(message => message.id === current.id && message.role === "assistant");
    if (!answer) return;
    if (answer.cancelled || answer.error) { cancel("Response stopped. Any available text remains in the conversation."); return; }
    current.finished = !options.isStreaming;
    if (current.read) {
      for (;;) {
        const next = nextSpeechChunk(answer.content.slice(current.consumed), current.finished);
        if (!next) break;
        current.consumed += next.consumed;
        if (next.text) current.queue.push(next.text);
        if (current.queue.length >= 64) { current.read = false; setError("Spoken reply limit reached. The full answer remains in chat."); break; }
      }
      if (current.queue.length) void pump(current);
    }
    if (current.finished && !current.pumping && !current.queue.length) { turn.current = null; setPhase("idle"); }
  }, [options.messages, options.isStreaming, pump, cancel]);

  const send = async (text: string) => {
    const current = turn.current;
    if (current && !current.ready) return false;
    if (current) { current.sent = true; setPhase("responding"); }
    const sent = current ? await latest.current.onSend(text, current.id) : await latest.current.onSend(text);
    if (!sent && current && turn.current === current) { current.sent = false; setPhase("idle"); }
    return sent;
  };
  const useTranscript = () => {
    const current = turn.current;
    if (!current || !transcript.trim()) return;
    latest.current.onChange([latest.current.value.trimEnd(), transcript.trim()].filter(Boolean).join("\n"));
    current.ready = true; setTranscript(""); setPhase("idle"); setOpen(false);
  };
  const updatePreferences = (change: Partial<typeof preferences>) => {
    const value = { ...preferences, ...change }; setPreferences(value); saveVoicePreferences(value);
  };
  const action = useRef({ start, finish, cancel, phase }); action.current = { start, finish, cancel, phase };
  useEffect(() => {
    const key = (event: KeyboardEvent) => {
      if (event.isComposing) return;
      if ((event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === "v") {
        event.preventDefault();
        if (event.repeat) return;
        if (action.current.phase === "listening") void action.current.finish();
        else void action.current.start();
      } else if (event.key === "Escape" && turn.current) { event.preventDefault(); action.current.cancel(); }
    };
    window.addEventListener("keydown", key);
    return () => window.removeEventListener("keydown", key);
  }, []);
  const showControls = (value: boolean) => { setOpen(value); if (value) void refresh(); };
  return { phase, open, setOpen: showControls, status, devices, preferences, updatePreferences, transcript, setTranscript,
    error, seconds, start, finish, cancel, send, useTranscript,
    busy: phase === "permission" || phase === "listening" || phase === "transcribing" || phase === "review" };
}
