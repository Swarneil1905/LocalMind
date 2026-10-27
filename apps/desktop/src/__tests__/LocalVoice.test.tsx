import { act, cleanup, fireEvent, render, renderHook, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { invoke } from "@tauri-apps/api/core";
import { useVoice } from "../hooks/useVoice";
import { LocalMicrophone, LocalSpeechPlayer, nextSpeechChunk, pcmWave, speechText, type Microphone, type SpeechPlayer } from "../lib/voice";
import { BuddyPage } from "../components/BuddyPage";
import { VoiceControls } from "../components/VoiceControls";
import type { Message } from "../types";

const status = { supported: true, stt_ready: true, engine: "local", voices: [{ id: "stock", name: "Stock voice", language: "en-US" }], install: { phase: "ready", downloaded: 1, total: 1 } };
const device = { deviceId: "mic", kind: "audioinput", label: "Microphone" } as MediaDeviceInfo;
function fixture() {
  const microphone: Microphone = { devices: vi.fn().mockResolvedValue([device]), start: vi.fn().mockResolvedValue(undefined), finish: vi.fn().mockResolvedValue("wave"), cancel: vi.fn() };
  const player: SpeechPlayer = { play: vi.fn().mockResolvedValue(undefined), stop: vi.fn() };
  return { value: "Typed draft", onChange: vi.fn(), onSend: vi.fn().mockResolvedValue(true), onStop: vi.fn(), messages: [] as Message[],
    isStreaming: false, disabled: false, scope: "one", microphone, player };
}
beforeEach(() => {
  localStorage.clear(); vi.clearAllMocks();
  vi.mocked(invoke).mockImplementation(async (_command, args) => {
    const request = args as { operation: string; payload: { turn_id: string } };
    if (request.operation === "status") return status;
    if (request.operation === "transcribe") return { turn_id: request.payload.turn_id, text: "Dictated text" };
    if (request.operation === "speak") return { turn_id: request.payload.turn_id, audio: "audio" };
    return {};
  });
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

async function dictate(result: { current: ReturnType<typeof useVoice> }) {
  await act(() => result.current.start());
  expect(result.current.phase).toBe("listening");
  await act(() => result.current.finish());
  expect(result.current.phase).toBe("review");
}

it("does not open a microphone on mount or browsing voice setup", async () => {
  const options = fixture();
  const view = renderHook(() => useVoice(options));
  expect(options.microphone.start).not.toHaveBeenCalled();
  view.unmount();
  render(<BuddyPage />);
  await screen.findByText("Transcription is installed and ready.");
  expect(options.microphone.start).not.toHaveBeenCalled();
  expect(screen.queryByText("Simulation")).toBeNull();
});

it("retries voice discovery after a transient engine failure", async () => {
  vi.mocked(invoke).mockResolvedValueOnce({ ...status, voices: [], speech_error: "Speech unavailable" })
    .mockResolvedValueOnce(status);
  render(<BuddyPage />);
  fireEvent.click(await screen.findByRole("button", { name: "Retry voice discovery" }));
  await screen.findByText("Installed voices: Stock voice.");
  expect(screen.queryByRole("button", { name: "Retry voice discovery" })).toBeNull();
});

it("recovers from permission denial through keyboard controls with the draft intact", async () => {
  const options = fixture();
  vi.mocked(options.microphone.start).mockRejectedValueOnce(new Error("Permission denied"));
  function Controls() { const voice = useVoice(options); return <VoiceControls voice={voice} disabled={false} />; }
  render(<Controls />);
  fireEvent.keyDown(window, { key: "v", ctrlKey: true, shiftKey: true });
  await screen.findByRole("alert");
  expect(screen.getByRole("alert").textContent).toContain("Permission denied");
  fireEvent.keyDown(window, { key: "v", ctrlKey: true, shiftKey: true });
  await waitFor(() => expect(screen.getByRole("status").textContent).toBe("Listening"));
  fireEvent.keyDown(window, { key: "Escape" });
  expect(screen.getByRole("status").textContent).toBe("Microphone off");
  expect(options.onChange).not.toHaveBeenCalled();
  expect(options.onSend).not.toHaveBeenCalled();
});

it("requires review, keeps the typed draft and binds audio and chat to the same turn", async () => {
  const options = fixture();
  const { result } = renderHook(() => useVoice(options));
  await dictate(result);
  expect(options.onSend).not.toHaveBeenCalled(); expect(options.onChange).not.toHaveBeenCalled();
  act(() => result.current.setTranscript("Corrected words"));
  act(() => result.current.useTranscript());
  expect(options.onChange).toHaveBeenCalledWith("Typed draft\nCorrected words");
  await act(() => result.current.send("Typed draft\nCorrected words"));
  const call = vi.mocked(invoke).mock.calls.find(([, args]) => (args as { operation: string }).operation === "transcribe");
  const id = (call![1] as { payload: { turn_id: string } }).payload.turn_id;
  expect(options.onSend).toHaveBeenCalledWith("Typed draft\nCorrected words", id);
});

it("discarding a transcript never clears an unrelated typed message", async () => {
  const options = fixture(); const { result } = renderHook(() => useVoice(options));
  await dictate(result); act(() => result.current.cancel());
  expect(options.onChange).not.toHaveBeenCalled(); expect(options.onSend).not.toHaveBeenCalled();
  expect(result.current.transcript).toBe("");
});

it("permission denial, silence and missing saved inputs recover without draft loss", async () => {
  const options = fixture(); vi.mocked(options.microphone.start).mockRejectedValueOnce(new Error("Permission denied"));
  const { result } = renderHook(() => useVoice(options));
  await act(() => result.current.start()); expect(result.current.error).toContain("Permission denied");
  await act(() => result.current.start());
  vi.mocked(options.microphone.finish).mockRejectedValueOnce(new Error("No speech detected"));
  await act(() => result.current.finish()); expect(result.current.error).toContain("No speech");
  act(() => result.current.updatePreferences({ device: "unplugged" }));
  await act(() => result.current.start()); expect(result.current.error).toContain("missing");
  expect(options.onChange).not.toHaveBeenCalled();
});

it("rejects late transcription after cancellation or a conversation change", async () => {
  const options = fixture();
  let complete!: (value: unknown) => void;
  vi.mocked(invoke).mockImplementation(async (_command, args) => {
    const request = args as { operation: string };
    if (request.operation === "status") return status;
    if (request.operation === "transcribe") return new Promise(resolve => { complete = resolve; });
    return {};
  });
  const { result, rerender } = renderHook(props => useVoice(props), { initialProps: options });
  await act(() => result.current.start());
  let pending!: Promise<void>;
  act(() => { pending = result.current.finish(); });
  await waitFor(() => expect(complete).toBeDefined());
  rerender({ ...options, scope: "different-chat" });
  await act(async () => { complete({ turn_id: "old", text: "Old words" }); await pending; });
  expect(result.current.transcript).toBe(""); expect(options.onChange).not.toHaveBeenCalled();
});

it("does not restore listening after cancellation during input enumeration", async () => {
  const options = fixture();
  let complete!: (inputs: MediaDeviceInfo[]) => void;
  vi.mocked(options.microphone.devices).mockResolvedValueOnce([device])
    .mockImplementationOnce(() => new Promise(resolve => { complete = resolve; }));
  const { result } = renderHook(() => useVoice(options));
  let pending!: Promise<void>;
  act(() => { pending = result.current.start(); });
  await waitFor(() => expect(complete).toBeDefined());
  act(() => result.current.cancel());
  await act(async () => { complete([device]); await pending; });
  expect(result.current.phase).toBe("idle");
});

it("stops an existing typed response when recording is explicitly started", async () => {
  const options = { ...fixture(), isStreaming: true };
  const { result } = renderHook(() => useVoice(options));
  await act(() => result.current.start());
  expect(options.onStop).toHaveBeenCalledOnce();
  expect(result.current.phase).toBe("listening");
});

it("speaks completed sentences during generation once and interrupts without stale playback", async () => {
  const options = fixture();
  const { result, rerender } = renderHook(props => useVoice(props), { initialProps: options });
  act(() => result.current.updatePreferences({ voice: "stock", readReplies: true }));
  await dictate(result); act(() => result.current.useTranscript());
  await act(() => result.current.send("question"));
  const id = options.onSend.mock.calls[0][1];
  const first = { ...options, isStreaming: true, messages: [{ id, role: "assistant" as const, content: "First sentence. More" }] };
  rerender(first);
  await waitFor(() => expect(options.player.play).toHaveBeenCalledTimes(1));
  rerender({ ...first, messages: [{ ...first.messages[0], content: "First sentence. More words" }] });
  expect(options.player.play).toHaveBeenCalledTimes(1);
  act(() => result.current.cancel());
  expect(options.player.stop).toHaveBeenCalled(); expect(options.onStop).toHaveBeenCalledOnce();
  rerender({ ...first, isStreaming: false, messages: [{ ...first.messages[0], content: "First sentence. More words. Late output." }] });
  expect(options.player.play).toHaveBeenCalledTimes(1);
});

it("drops late synthesized audio after a new recording starts", async () => {
  const options = fixture();
  let finishAudio!: (value: unknown) => void;
  const original = vi.mocked(invoke).getMockImplementation()!;
  vi.mocked(invoke).mockImplementation((command, args) => {
    if ((args as { operation: string }).operation === "speak") return new Promise(resolve => { finishAudio = resolve; });
    return original(command, args);
  });
  const { result, rerender } = renderHook(props => useVoice(props), { initialProps: options });
  act(() => result.current.updatePreferences({ voice: "stock", readReplies: true }));
  await dictate(result); act(() => result.current.useTranscript()); await act(() => result.current.send("question"));
  const id = options.onSend.mock.calls[0][1];
  rerender({ ...options, isStreaming: true, messages: [{ id, role: "assistant", content: "Old sentence. Next" }] });
  await waitFor(() => expect(finishAudio).toBeDefined());
  await act(() => result.current.start());
  await act(async () => finishAudio({ turn_id: id, audio: "old-audio" }));
  expect(options.player.play).not.toHaveBeenCalled(); expect(result.current.phase).toBe("listening");
});

it("persists only preferences and releases capture on focus loss and unmount", async () => {
  const options = fixture(); const { result, unmount } = renderHook(() => useVoice(options));
  act(() => result.current.updatePreferences({ device: "mic", voice: "stock", readReplies: true }));
  expect(JSON.parse(localStorage.getItem("localmind.voice.v1")!)).toEqual({ device: "mic", voice: "stock", readReplies: true });
  await act(() => result.current.start()); act(() => window.dispatchEvent(new Event("blur")));
  expect(result.current.phase).toBe("idle"); expect(options.microphone.cancel).toHaveBeenCalled();
  unmount(); expect(options.player.stop).toHaveBeenCalled();
  expect(localStorage.getItem("localmind.voice.v1")).not.toContain("audio");
});

it("supports keyboard recording and Escape without a mouse", async () => {
  const options = fixture(); const { result } = renderHook(() => useVoice(options));
  fireEvent.keyDown(window, { key: "v", ctrlKey: true, shiftKey: true });
  await waitFor(() => expect(result.current.phase).toBe("listening"));
  fireEvent.keyDown(window, { key: "Escape" });
  expect(result.current.phase).toBe("idle");
});

it("expires capture after a sleep-sized clock gap without restarting it", async () => {
  vi.useFakeTimers();
  const options = fixture(); const { result } = renderHook(() => useVoice(options));
  await act(() => result.current.start());
  expect(result.current.phase).toBe("listening");
  vi.setSystemTime(Date.now() + 60_000);
  act(() => vi.advanceTimersByTime(500));
  expect(result.current.phase).toBe("idle");
  expect(result.current.error).toContain("suspended");
  expect(options.microphone.start).toHaveBeenCalledOnce();
  expect(options.player.stop).toHaveBeenCalled();
});

it("never speaks code, math or incomplete markdown and preserves sentence order", () => {
  const text = "Hello. ```js\nsecret();\n```\nThe result is $$x = 3.$$\nUseful explanation. ";
  const spoken: string[] = []; let rest = text;
  for (;;) { const next = nextSpeechChunk(rest, true); if (!next) break; spoken.push(next.text); rest = rest.slice(next.consumed); }
  expect(spoken.join(" ")).toContain("Useful explanation");
  expect(spoken.join(" ")).not.toMatch(/secret|x =/);
  expect(nextSpeechChunk("Pending sentence", false)).toBeNull();
  const formula = nextSpeechChunk("The formula \\(a. b + c\\) is omitted. Next", false);
  expect(formula?.text).toBe("The formula is omitted.");
  expect(speechText("Use `code` and $x^2$ with [a source](https://example.com). [K1]")).toBe("Use and with a source.");
});

it("encodes bounded mono PCM16 and drops a late microphone permission grant", async () => {
  const bytes = Uint8Array.from(atob(pcmWave(new Float32Array(48000).fill(.25), 48000)), char => char.charCodeAt(0));
  expect(bytes.length).toBe(32044);
  expect(new DataView(bytes.buffer).getUint32(24, true)).toBe(16000);
  let grant!: (stream: MediaStream) => void;
  const stop = vi.fn();
  vi.stubGlobal("navigator", { mediaDevices: { getUserMedia: vi.fn(() => new Promise(resolve => { grant = resolve; })) } });
  const mic = new LocalMicrophone(); const pending = mic.start("", vi.fn()); mic.cancel();
  grant({ getTracks: () => [{ stop }] } as unknown as MediaStream); await pending;
  expect(stop).toHaveBeenCalledOnce();
});

it("stops active playback synchronously and revokes its audio URL", async () => {
  const pause = vi.fn(), load = vi.fn();
  vi.stubGlobal("Audio", class { pause = pause; load = load; play = vi.fn().mockResolvedValue(undefined); removeAttribute = vi.fn(); });
  const revoke = vi.fn(); vi.stubGlobal("URL", { createObjectURL: () => "blob:owned", revokeObjectURL: revoke });
  const player = new LocalSpeechPlayer(); const playing = player.play(btoa("wave")); player.stop();
  expect(pause).toHaveBeenCalledOnce(); expect(revoke).toHaveBeenCalledWith("blob:owned");
  await playing;
});

it("does not let an old worklet failure close a newer microphone capture", async () => {
  let failOld!: (error: Error) => void;
  const oldTrack = { stop: vi.fn(), onended: null }, newTrack = { stop: vi.fn(), onended: null };
  const stream = (track: typeof oldTrack) => ({ getTracks: () => [track], getAudioTracks: () => [track] });
  vi.stubGlobal("navigator", { mediaDevices: { getUserMedia: vi.fn()
    .mockResolvedValueOnce(stream(oldTrack)).mockResolvedValueOnce(stream(newTrack)) } });
  let instances = 0;
  const connect = (destination: unknown) => destination;
  vi.stubGlobal("AudioContext", class {
    sampleRate = 48000; destination = {}; close = vi.fn().mockResolvedValue(undefined); resume = vi.fn().mockResolvedValue(undefined);
    audioWorklet = { addModule: () => ++instances === 1 ? new Promise((_resolve, reject) => { failOld = reject; }) : Promise.resolve() };
    createGain = () => ({ gain: { value: 1 }, connect });
    createMediaStreamSource = () => ({ connect });
  });
  vi.stubGlobal("AudioWorkletNode", class { port = { onmessage: null, close: vi.fn() }; connect = connect; disconnect = vi.fn(); });
  const mic = new LocalMicrophone();
  const oldStart = mic.start("", vi.fn());
  const oldFailure = expect(oldStart).rejects.toThrow("Old module failed");
  await waitFor(() => expect(failOld).toBeDefined());
  await mic.start("", vi.fn());
  failOld(new Error("Old module failed")); await oldFailure;
  expect(oldTrack.stop).toHaveBeenCalledOnce();
  expect(newTrack.stop).not.toHaveBeenCalled();
  mic.cancel(); expect(newTrack.stop).toHaveBeenCalledOnce();
});

it("reports blocked playback without hiding the text response", async () => {
  vi.stubGlobal("Audio", class {
    pause = vi.fn(); load = vi.fn(); removeAttribute = vi.fn();
    play = vi.fn().mockRejectedValue(new Error("Playback blocked"));
  });
  vi.stubGlobal("URL", { createObjectURL: vi.fn(() => "blob:test"), revokeObjectURL: vi.fn() });
  await expect(new LocalSpeechPlayer().play(btoa("wave"))).rejects.toThrow("Playback blocked");
  expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:test");
});
