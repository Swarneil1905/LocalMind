import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { invoke } from "@tauri-apps/api/core";
import { ResponsePreferences } from "../components/ResponsePreferences";
import { ResponseFeedback, FeedbackSettings } from "../components/ResponseFeedback";
import { VisualBoundary } from "../components/VisualBoundary";
import { VisualCard } from "../components/VisualCard";
import { MemoryPage } from "../components/MemoryPage";
import { conversationMessages } from "../lib/conversationMessages";
import fixture from "../../../../contracts/app-v1.json";

beforeEach(() => vi.clearAllMocks());
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it("decodes the backend fixture without losing images, cancelled text or failures", () => {
  expect(fixture.schema_version).toBe(1);
  const messages = conversationMessages(fixture.messages);
  expect(messages[0].attachments?.[0].filename).toBe("fixture.png");
  expect(messages[3]).toMatchObject({ cancelled: true, content: "Partial response" });
  expect(messages[5]).toMatchObject({ error: true, errorMessage: "Connection interrupted." });
  expect(() => conversationMessages([{ ...fixture.messages[0], cancelled: "false" }])).toThrow(/boolean/);
  expect(() => conversationMessages([{ ...fixture.messages[0], attachments: [{ id: 1 }] }])).toThrow(/attachment/);
});

it("saves only explicit preference choices and supports reset", async () => {
  vi.mocked(invoke).mockImplementation(async (_command, args) => {
    const request = args as { path: string; method: string; body: unknown };
    return request.path.endsWith("feedback") ? { records: [] } : request.method === "POST" ? request.body : fixture.preferences;
  });
  render(<ResponsePreferences />);
  fireEvent.change(await screen.findByLabelText("Answer length"), { target: { value: "brief" } });
  expect(vi.mocked(invoke).mock.calls.filter(([, args]) => (args as { method: string }).method === "POST")).toHaveLength(0);
  fireEvent.click(screen.getByText("Save preferences"));
  await screen.findByText("Preferences saved for your next message.");
  expect(invoke).toHaveBeenCalledWith("quality_request", { method: "POST", path: "/quality/preferences", body: { ...fixture.preferences, length: "brief" } });
  fireEvent.click(screen.getByText("Reset preferences"));
  await waitFor(() => expect((screen.getByLabelText("Answer length") as HTMLSelectElement).value).toBe("balanced"));
});

it("does not save feedback before consent and keeps failed notes for retry", async () => {
  vi.mocked(invoke).mockRejectedValueOnce("unavailable").mockResolvedValue({});
  render(<ResponseFeedback conversationId="chat" content="answer" />);
  expect(invoke).not.toHaveBeenCalled();
  fireEvent.click(screen.getByText("Give feedback"));
  const field = screen.getByLabelText("Correction or note (optional)");
  fireEvent.change(field, { target: { value: "Use the active topic." } });
  fireEvent.click(screen.getByText("Save feedback locally"));
  await screen.findByRole("alert");
  expect((field as HTMLTextAreaElement).value).toBe("Use the active topic.");
  fireEvent.click(screen.getByText("Save feedback locally"));
  await screen.findByText(/Saved locally/);
});

it("exports only selected feedback after preview and permits deletion", async () => {
  vi.mocked(invoke).mockImplementation(async (_command, args) => {
    const { path } = args as { path: string };
    if (path.endsWith("export")) return { records: [{ category: "incorrect", correction: "redacted" }] };
    if (path.endsWith("delete")) return { ok: true };
    return { records: [{ id: "one", category: "incorrect", correction: "note", updated_at: "today" }] };
  });
  render(<FeedbackSettings />);
  const checkbox = await screen.findByRole("checkbox");
  expect((checkbox as HTMLInputElement).checked).toBe(false);
  expect(screen.queryByText("Download reviewed export")).toBeNull();
  fireEvent.click(checkbox);
  fireEvent.click(screen.getByText("Preview selected export"));
  await screen.findByLabelText("Review and redact export");
  expect(invoke).toHaveBeenCalledWith("quality_request", { method: "POST", path: "/quality/feedback/export", body: { ids: ["one"] } });
  fireEvent.click(screen.getByText("Delete feedback"));
  await screen.findByText("No saved feedback.");
  expect(screen.queryByText("Download reviewed export")).toBeNull();
});

it("edits a memory with its source and expected old content", async () => {
  vi.mocked(invoke).mockResolvedValue({});
  const refresh = vi.fn();
  render(<MemoryPage memories={[fixture.memory]} links={[]} onDelete={vi.fn()} onDeleteLink={vi.fn()} onRefresh={refresh} />);
  fireEvent.click(screen.getByText("Edit memory"));
  fireEvent.change(screen.getByLabelText("Correct memory"), { target: { value: "Prefer Java examples." } });
  fireEvent.click(screen.getByText("Save memory"));
  await waitFor(() => expect(refresh).toHaveBeenCalledOnce());
  expect(invoke).toHaveBeenCalledWith("quality_request", { method: "POST", path: "/quality/memory/edit", body: { id: fixture.memory.id, content: "Prefer Java examples.", expected_content: fixture.memory.content } });
});

it("keeps the transcript after renderer crashes and allows only one retry", () => {
  vi.spyOn(console, "error").mockImplementation(() => {});
  function Broken(): never { throw new Error("bad graph"); }
  const close = vi.fn();
  render(<><p>Saved answer</p><VisualBoundary onClose={close}><Broken /></VisualBoundary><textarea aria-label="Message" /></>);
  expect(screen.getByText("Saved answer")).toBeTruthy();
  fireEvent.click(screen.getByText("Retry visual once"));
  expect(screen.queryByText("Retry visual once")).toBeNull();
  expect(screen.getByLabelText("Message")).toBeTruthy();
  fireEvent.click(screen.getByText("Close failed visual"));
  expect(close).toHaveBeenCalledOnce();
});

it("malformed visual source offers one repair draft without sending a model request", async () => {
  const handler = vi.fn();
  window.addEventListener("localmind-visual-followup", handler);
  try {
    render(<VisualCard source="{broken" content="answer" conversationId="chat" />);
    fireEvent.click(screen.getByText("Open visual workspace"));
    await screen.findByRole("alert");
    fireEvent.click(screen.getByText("Draft a repair request"));
    expect(handler).toHaveBeenCalledOnce();
    expect(invoke).not.toHaveBeenCalled();
    expect(screen.queryByText("Draft a repair request")).toBeNull();
    expect(screen.getByText("View source")).toBeTruthy();
  } finally { window.removeEventListener("localmind-visual-followup", handler); }
});
