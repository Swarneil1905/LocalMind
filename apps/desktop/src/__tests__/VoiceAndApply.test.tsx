import { afterEach, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { BuddyPage } from "../components/BuddyPage";
import { VoiceRecorder } from "../components/VoiceRecorder";
import { syntheticRecorder, type RecordingAdapter } from "../lib/syntheticRecorder";
import { RepairWriteControls } from "../components/RepairWriteControls";

afterEach(() => { cleanup(); vi.useRealTimers(); });

function adapter(): RecordingAdapter {
  return { devices: [{ id: "test", label: "Test input" }], requestPermission: vi.fn().mockResolvedValue(undefined),
    start: vi.fn(), finish: vi.fn().mockResolvedValue("Sample words"), cancel: vi.fn() };
}
async function allow() {
  fireEvent.click(screen.getByRole("button", { name: "Allow simulated microphone" }));
  await waitFor(() => expect(screen.queryByRole("button", { name: "Allow simulated microphone" })).toBeNull());
}

it("never activates a recorder on mount, rerender, or navigation", () => {
  const recorder = adapter();
  const view = render(<VoiceRecorder recorder={recorder} />);
  view.rerender(<VoiceRecorder recorder={recorder} />);
  expect(recorder.requestPermission).not.toHaveBeenCalled();
  expect(recorder.start).not.toHaveBeenCalled();
  view.unmount();
  expect(recorder.cancel).toHaveBeenCalled();
  render(<VoiceRecorder recorder={recorder} />);
  expect(recorder.start).not.toHaveBeenCalled();
});

it("requires explicit permission and uses the selected device", async () => {
  const recorder = adapter();
  recorder.devices.push({ id: "headset", label: "Headset" });
  render(<VoiceRecorder recorder={recorder} />);
  expect((screen.getByRole("button", { name: "Start sample recording" }) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.change(screen.getByLabelText("Input device"), { target: { value: "headset" } });
  await allow();
  expect(recorder.start).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Start sample recording" }));
  expect(recorder.start).toHaveBeenCalledWith("headset");
  expect(screen.getByRole("status").textContent).toContain("Simulated recording");
  fireEvent.click(screen.getByRole("button", { name: "Stop sample recording" }));
  await screen.findByDisplayValue("Sample words");
  fireEvent.change(screen.getByLabelText("Editable sample transcript"), { target: { value: "My corrected draft" } });
  fireEvent.click(screen.getByRole("button", { name: "Start sample recording" }));
  fireEvent.click(screen.getByRole("button", { name: "Cancel recording" }));
  expect(screen.getByDisplayValue("My corrected draft")).not.toBeNull();
});

it("cancels a pending transcript without replacing the previous draft", async () => {
  const recorder = adapter();
  let resolve!: (text: string) => void;
  recorder.finish = vi.fn(() => new Promise<string>(done => { resolve = done; }));
  render(<VoiceRecorder recorder={recorder} />);
  fireEvent.change(screen.getByLabelText("Editable sample transcript"), { target: { value: "Keep me" } });
  await allow();
  fireEvent.click(screen.getByRole("button", { name: "Start sample recording" }));
  fireEvent.click(screen.getByRole("button", { name: "Stop sample recording" }));
  expect(screen.getByRole("status").textContent).toContain("Preparing");
  fireEvent.click(screen.getByRole("button", { name: "Cancel recording" }));
  await act(async () => resolve("Late transcript"));
  expect(screen.getByDisplayValue("Keep me")).not.toBeNull();
});

it("handles push-to-talk release and losing focus", async () => {
  const recorder = adapter();
  render(<VoiceRecorder recorder={recorder} />);
  await allow();
  const hold = screen.getByRole("button", { name: "Hold to talk" });
  fireEvent.keyDown(hold, { key: " " });
  expect(recorder.start).toHaveBeenCalledOnce();
  fireEvent.keyDown(hold, { key: " ", repeat: true });
  fireEvent.keyUp(hold, { key: " " });
  await screen.findByDisplayValue("Sample words");
  expect(recorder.finish).toHaveBeenCalledOnce();
  fireEvent.pointerDown(hold, { pointerId: 1 });
  fireEvent.blur(window);
  expect(recorder.cancel).toHaveBeenCalled();
  expect(screen.getByRole("status").textContent).toContain("Ready");
});

it("exposes denied and missing-device states without recording", async () => {
  render(<BuddyPage />);
  fireEvent.change(screen.getByLabelText("Simulation scenario"), { target: { value: "denied" } });
  fireEvent.click(screen.getByRole("button", { name: "Allow simulated microphone" }));
  expect((await screen.findByRole("alert")).textContent).toContain("denied");
  fireEvent.change(screen.getByLabelText("Simulation scenario"), { target: { value: "missing" } });
  fireEvent.click(screen.getByRole("button", { name: "Allow simulated microphone" }));
  expect((await screen.findByRole("alert")).textContent).toContain("No simulated input");
  expect((screen.getByRole("button", { name: "Start sample recording" }) as HTMLButtonElement).disabled).toBe(true);
});

it("bounds recordings and aborts synthetic processing", async () => {
  const recorder = adapter();
  render(<VoiceRecorder recorder={recorder} />);
  await allow();
  vi.useFakeTimers();
  fireEvent.click(screen.getByRole("button", { name: "Start sample recording" }));
  await act(async () => vi.advanceTimersByTime(30_000));
  expect(recorder.finish).toHaveBeenCalledOnce();
  const synthetic = syntheticRecorder();
  synthetic.start(synthetic.devices[0].id);
  const controller = new AbortController();
  const finished = synthetic.finish(controller.signal);
  controller.abort();
  await expect(finished).rejects.toThrow("Cancelled");
});

const base = { operationId: "operation", itemId: "item", target: "draft.md", kind: "file", accepted: true, busy: false };
it("does not apply an accepted preview until a separate confirmation", async () => {
  const run = vi.fn().mockResolvedValue(true);
  render(<RepairWriteControls {...base} capabilities={{ files: true, tasks: true, reason: "Available" }} run={run} />);
  expect(run).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Apply this change…" }));
  expect(run).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Cancel write" }));
  expect(run).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Apply this change…" }));
  fireEvent.click(screen.getByRole("button", { name: "Confirm apply" }));
  await waitFor(() => expect(run).toHaveBeenCalledWith("apply", { operation_id: "operation", item_id: "item", confirm: true }));
});

it("shows disabled capability and receipt recovery without offering duplicate apply", async () => {
  const run = vi.fn().mockResolvedValue(true);
  const view = render(<RepairWriteControls {...base} capabilities={{ files: false, tasks: false, reason: "Pilot disabled" }} run={run} />);
  expect(screen.queryByRole("button", { name: /Apply/ })).toBeNull();
  expect(screen.getByText("Pilot disabled")).not.toBeNull();
  view.rerender(<RepairWriteControls {...base} capabilities={{ files: true, tasks: true, reason: "Available" }} run={run}
    receipt={{ item_id: "item", state: "applied", error: null, updated_at: "now" }} />);
  expect(screen.queryByRole("button", { name: /Apply/ })).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Check recovery" }));
  expect(run).toHaveBeenCalledWith("recover", { operation_id: "operation", item_id: "item", confirm: true });
  fireEvent.click(screen.getByRole("button", { name: "Restore original…" }));
  fireEvent.click(screen.getByRole("button", { name: "Confirm restore" }));
  await waitFor(() => expect(run).toHaveBeenCalledWith("restore", { operation_id: "operation", item_id: "item", confirm: true }));
});
