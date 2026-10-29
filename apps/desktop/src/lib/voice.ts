import { invoke } from "@tauri-apps/api/core";

export interface VoiceStatus {
  supported: boolean;
  disabled_reason?: string;
  stt_ready: boolean;
  engine: string;
  voices: { id: string; name: string; language: string }[];
  speech_error?: string | null;
  install: { phase: string; downloaded: number; total: number; error?: string | null };
}
export function voiceRequest<T>(operation: string, payload?: Record<string, unknown>): Promise<T> {
  return invoke("voice_request", { operation, payload: payload ?? null });
}

export interface VoicePreferences { device: string; voice: string; readReplies: boolean }
export function voicePreferences(): VoicePreferences {
  try {
    const value = JSON.parse(localStorage.getItem("localmind.voice.v1") || "{}");
    return { device: typeof value.device === "string" ? value.device : "",
      voice: typeof value.voice === "string" ? value.voice : "", readReplies: value.readReplies === true };
  } catch { return { device: "", voice: "", readReplies: false }; }
}
export function saveVoicePreferences(value: VoicePreferences) {
  try { localStorage.setItem("localmind.voice.v1", JSON.stringify(value)); } catch { /* Voice remains usable without persistence. */ }
}

/** Keep incomplete code/math delimiters until a later token, never speak them. */
export function speechText(raw: string): string {
  return raw.replace(/(```|~~~)[\s\S]*?(?:\1|$)/g, " ")
    .replace(/\$\$[\s\S]*?(?:\$\$|$)/g, " ")
    .replace(/\\\[[\s\S]*?(?:\\\]|$)/g, " ")
    .replace(/\\\([\s\S]*?(?:\\\)|$)/g, " ")
    .replace(/\$[^$\n]*(?:\$|$)/g, " ")
    .replace(/`[^`]*(?:`|$)/g, " ")
    .replace(/\[([^\]]+)\]\([^)]*\)/g, "$1")
    .replace(/\[(?:K?\d+)\]/g, "")
    .replace(/https?:\/\/\S+/g, "")
    .replace(/^[\s]*[#>*-]+\s*/gm, "")
    .replace(/[*_~]/g, "").replace(/\s+/g, " ").trim();
}

/** Consume raw offsets, so growing markdown never shifts already-spoken text. */
export function nextSpeechChunk(raw: string, final: boolean): { text: string; consumed: number } | null {
  let fence = "", math = "", inline = false;
  for (let i = 0; i < raw.length; i++) {
    if (fence && raw.startsWith(fence, i)) { fence = ""; i += 2; continue; }
    if (fence) continue;
    if (!math && !inline && (raw.startsWith("```", i) || raw.startsWith("~~~", i))) { fence = raw.slice(i, i + 3); i += 2; continue; }
    if (math && raw.startsWith(math, i)) { i += math.length - 1; math = ""; continue; }
    if (math) continue;
    if (raw[i] === "`") { inline = !inline; continue; }
    if (inline) continue;
    if (raw.startsWith("$$", i)) { math = "$$"; i++; continue; }
    if (raw.startsWith("\\[", i)) { math = "\\]"; i++; continue; }
    if (raw.startsWith("\\(", i)) { math = "\\)"; i++; continue; }
    if (raw[i] === "$") { math = "$"; continue; }
    const sentence = /[.!?\n]/.test(raw[i]) && i + 1 < raw.length && /\s/.test(raw[i + 1]);
    if (sentence || (i >= 400 && /\s/.test(raw[i]))) {
      return { text: speechText(raw.slice(0, i + 1)).slice(0, 600), consumed: i + 1 };
    }
  }
  return final && raw.length ? { text: speechText(raw).slice(0, 600), consumed: raw.length } : null;
}

export function pcmWave(samples: Float32Array, rate: number): string {
  const count = Math.min(480_000, Math.floor(samples.length * 16000 / rate));
  const bytes = new Uint8Array(44 + count * 2);
  const view = new DataView(bytes.buffer);
  const label = (offset: number, text: string) => [...text].forEach((char, i) => view.setUint8(offset + i, char.charCodeAt(0)));
  label(0, "RIFF"); view.setUint32(4, 36 + count * 2, true); label(8, "WAVE"); label(12, "fmt ");
  view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
  view.setUint32(24, 16000, true); view.setUint32(28, 32000, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true);
  label(36, "data"); view.setUint32(40, count * 2, true);
  for (let i = 0; i < count; i++) {
    const from = Math.floor(i * rate / 16000), to = Math.max(from + 1, Math.floor((i + 1) * rate / 16000));
    let sum = 0;
    for (let j = from; j < to; j++) sum += samples[j] || 0;
    view.setInt16(44 + i * 2, Math.round(Math.max(-1, Math.min(1, sum / (to - from))) * 32767), true);
  }
  let binary = "";
  for (let offset = 0; offset < bytes.length; offset += 8192) binary += String.fromCharCode(...bytes.subarray(offset, offset + 8192));
  return btoa(binary);
}

export interface Microphone {
  devices(): Promise<MediaDeviceInfo[]>;
  start(device: string, ended: () => void): Promise<void>;
  finish(): Promise<string>;
  cancel(): void;
}

export class LocalMicrophone implements Microphone {
  private stream?: MediaStream;
  private context?: AudioContext;
  private node?: AudioWorkletNode;
  private chunks: Float32Array[] = [];
  private count = 0;
  private epoch = 0;

  async devices() {
    if (!navigator.mediaDevices) throw new Error("Microphone access is unavailable in this window.");
    return (await navigator.mediaDevices.enumerateDevices()).filter(device => device.kind === "audioinput");
  }
  async start(device: string, ended: () => void) {
    this.cancel();
    const version = this.epoch;
    const stream = await navigator.mediaDevices.getUserMedia({ audio: {
      ...(device ? { deviceId: { exact: device } } : {}), channelCount: 1, echoCancellation: true, noiseSuppression: true,
    } });
    if (version !== this.epoch) { stream.getTracks().forEach(track => track.stop()); return; }
    this.stream = stream;
    stream.getAudioTracks().forEach(track => { track.onended = ended; });
    const context = new AudioContext();
    this.context = context;
    try {
      await context.audioWorklet.addModule(new URL("../audio/pcm-capture.js", import.meta.url).href);
      if (version !== this.epoch) return;
      const node = new AudioWorkletNode(context, "localmind-pcm");
      this.node = node;
      node.port.onmessage = event => {
        if (version !== this.epoch || this.count >= context.sampleRate * 30) return;
        const chunk = (event.data as Float32Array).slice(0, context.sampleRate * 30 - this.count);
        this.chunks.push(chunk); this.count += chunk.length;
      };
      const mute = context.createGain(); mute.gain.value = 0;
      context.createMediaStreamSource(stream).connect(node).connect(mute).connect(context.destination);
      await context.resume();
    } catch (error) { if (version === this.epoch) this.cancel(); throw error; }
  }
  async finish() {
    const rate = this.context?.sampleRate ?? 16000;
    const samples = new Float32Array(this.count);
    let offset = 0;
    for (const chunk of this.chunks) { samples.set(chunk, offset); offset += chunk.length; }
    this.cancel();
    return pcmWave(samples, rate);
  }
  cancel() {
    this.epoch++;
    this.stream?.getTracks().forEach(track => { track.onended = null; track.stop(); });
    this.stream = undefined;
    if (this.node) { this.node.port.onmessage = null; this.node.port.close(); this.node.disconnect(); }
    this.node = undefined;
    if (this.context) void this.context.close().catch(() => {});
    this.context = undefined; this.chunks = []; this.count = 0;
  }
}

export interface SpeechPlayer { play(audio: string): Promise<void>; stop(): void }
export class LocalSpeechPlayer implements SpeechPlayer {
  private element?: HTMLAudioElement;
  private url?: string;
  private finish?: () => void;
  play(encoded: string): Promise<void> {
    this.stop();
    const bytes = Uint8Array.from(atob(encoded), char => char.charCodeAt(0));
    this.url = URL.createObjectURL(new Blob([bytes], { type: "audio/wav" }));
    const audio = new Audio(this.url); this.element = audio;
    return new Promise((resolve, reject) => {
      this.finish = resolve;
      audio.onended = () => { this.stop(); resolve(); };
      const failed = (error: unknown) => {
        if (this.element !== audio) return;
        this.finish = undefined; this.stop(); reject(error);
      };
      audio.onerror = () => failed(new Error("Audio playback failed. Your text answer is still available."));
      void audio.play().catch(failed);
    });
  }
  stop() {
    if (this.element) { this.element.pause(); this.element.onended = null; this.element.onerror = null; this.element.removeAttribute("src"); this.element.load(); }
    this.element = undefined;
    if (this.url) URL.revokeObjectURL(this.url);
    this.url = undefined;
    const finish = this.finish; this.finish = undefined; finish?.();
  }
}
