import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import App from "../App";

const handlers = new Map<string, Set<(event: { payload: unknown }) => void>>();
let failCreate = false;
let miniActive = false;
let miniPinned = false;
let failWindow = false;
let failLoad = false;
let modelReady = true;
let pendingCreate: Promise<unknown> | null = null;
const conversation = {
  id: "saved",
  title: "Saved conversation",
  created_at: "2026-10-10",
  updated_at: "2026-10-10",
};
function emit(name: string, payload: unknown) {
  act(() => handlers.get(name)?.forEach((fn) => fn({ payload })));
}
const setup = async () => {
  render(<App />);
  await screen.findByText("Running locally");
  return screen.getByRole("textbox", {
    name: "Message LocalMind",
  }) as HTMLTextAreaElement;
};
beforeEach(() => {
  vi.clearAllMocks();
  handlers.clear();
  localStorage.clear();
  failCreate = false;
  miniActive = false;
  miniPinned = false;
  failWindow = false;
  failLoad = false;
  modelReady = true;
  pendingCreate = null;
  Object.defineProperty(window, "matchMedia", {
    writable: true,
    value: vi.fn(() => ({
      matches: false,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    })),
  });
  vi.mocked(listen).mockImplementation(async (name, handler) => {
    const fn = handler as (event: { payload: unknown }) => void;
    if (!handlers.has(name)) handlers.set(name, new Set());
    handlers.get(name)!.add(fn);
    return () => {
      handlers.get(name)?.delete(fn);
    };
  });
  vi.mocked(invoke).mockImplementation(async (command, args) => {
    if (command === "get_mini_state") return { active: miniActive, pinned: miniPinned };
    if (command === "set_mini_mode") {
      if (failWindow) throw new Error("Window resize failed");
      miniActive = (args as { compact: boolean }).compact;
      if (!miniActive) miniPinned = false;
      return { active: miniActive, pinned: miniPinned };
    }
    if (command === "pin_mini_window") {
      miniPinned = (args as { pinned: boolean }).pinned;
      return { active: miniActive, pinned: miniPinned };
    }
    if (command === "get_runtime_status")
      return { status: "ready", error: null };
    if (command === "list_ollama_models") return ["qwen3:8b"];
    if (command === "model_readiness") return { model: "qwen3:8b", preset: "balanced", ready: modelReady,
      state: modelReady ? "ready" : "needs_setup", message: modelReady ? "Ready" : "Selected model cannot chat" };
    if (command === "get_ollama_status")
      return { running: true, models: [{ name: "qwen3:8b" }], gpu: null };
    if (command === "list_conversations") return [conversation];
    if (command === "list_projects") return { projects: [] };
    if (command === "create_conversation") {
      if (failCreate) throw new Error("Cannot save");
      if (pendingCreate) return pendingCreate;
      return {
        ...conversation,
        id: "new",
        title: (args as { title: string }).title,
      };
    }
    if (command === "get_conversation_messages") {
      if (failLoad) throw new Error("Cannot load");
      return [
        { id: "old-user", conversation_id: "saved", role: "user", content: "Previous question" },
        { id: "old-answer", conversation_id: "saved", role: "assistant", content: "Previous answer" },
      ];
    }
    if (command.startsWith("list_") || command === "get_followup_questions")
      return [];
    return null;
  });
});
afterEach(cleanup);

describe("desktop chat workflow", () => {
  it("updates the header when web search is enabled or disabled", async () => {
    await setup();
    fireEvent.click(screen.getByRole("button", { name: "Tools" }));
    const web = screen.getByRole("button", { name: "Web" });
    if (web.getAttribute("aria-pressed") === "true") fireEvent.click(web);
    expect(screen.getByText("Local model", { selector: ".local-label" })).toBeTruthy();
    fireEvent.click(web);
    expect(screen.getByText("Web search enabled", { selector: ".local-label" })).toBeTruthy();
    fireEvent.click(web);
    expect(screen.getByText("Local model", { selector: ".local-label" })).toBeTruthy();
  });
  it("keeps drafts but blocks sending until the selected model passes inspection", async () => {
    modelReady = false;
    render(<App />);
    fireEvent.click(await screen.findByRole("button", { name: "Set up later" }));
    await screen.findByText("Your model needs attention");
    const input = screen.getByRole("textbox", { name: "Message LocalMind" });
    fireEvent.change(input, { target: { value: "Keep my draft" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(invoke).not.toHaveBeenCalledWith("chat_stream", expect.anything());
    expect((input as HTMLTextAreaElement).value).toBe("Keep my draft");
    modelReady = true;
    fireEvent.click(screen.getByRole("button", { name: "Check model again" }));
    await screen.findByText("Running locally");
    expect((input as HTMLTextAreaElement).value).toBe("Keep my draft");
  });
  it("renders a ready workspace with keyboard-accessible navigation", async () => {
    const input = await setup();
    expect(input).toBeDefined();
    expect(screen.getByRole("navigation", { name: "Workspace" })).toBeDefined();
    expect(
      screen
        .getByRole("button", { name: "Send message" })
        .hasAttribute("disabled"),
    ).toBe(true);
  });
  it("retains a draft when creating its saved conversation fails", async () => {
    const input = await setup();
    failCreate = true;
    fireEvent.change(input, { target: { value: "Keep this draft" } });
    fireEvent.click(screen.getByRole("button", { name: "Send message" }));
    await screen.findByText(/Your draft is still here/);
    expect(input.value).toBe("Keep this draft");
    expect(
      vi.mocked(invoke).mock.calls.some(([name]) => name === "chat_stream"),
    ).toBe(false);
  });
  it("preserves newer typing while the first conversation is being created", async () => {
    let resolve!: (value: unknown) => void;
    const input = await setup();
    pendingCreate = new Promise((r) => {
      resolve = r;
    });
    fireEvent.change(input, { target: { value: "First question" } });
    fireEvent.click(screen.getByRole("button", { name: "Send message" }));
    fireEvent.change(input, { target: { value: "A newer draft" } });
    await act(async () =>
      resolve({ ...conversation, id: "created", title: "First question" }),
    );
    await waitFor(() =>
      expect(
        vi.mocked(invoke).mock.calls.some(([name]) => name === "chat_stream"),
      ).toBe(true),
    );
    expect(input.value).toBe("A newer draft");
  });
  it("blocks a new turn when saved history cannot be loaded", async () => {
    const input = await setup();
    failLoad = true;
    fireEvent.click(screen.getByRole("button", { name: "Saved conversation" }));
    await screen.findByRole("button", { name: "Try loading again" });
    fireEvent.change(input, { target: { value: "Keep during recovery" } });
    expect(
      screen
        .getByRole("button", { name: "Send message" })
        .hasAttribute("disabled"),
    ).toBe(true);
    failLoad = false;
    fireEvent.click(screen.getByRole("button", { name: "Try loading again" }));
    await screen.findByText("Previous answer");
    expect(input.value).toBe("Keep during recovery");
    expect(
      screen
        .getByRole("button", { name: "Send message" })
        .hasAttribute("disabled"),
    ).toBe(false);
  });
  it("offers retry after stopping before the first token and replaces the failed turn", async () => {
    const input = await setup();
    fireEvent.change(input, { target: { value: "Slow question" } });
    fireEvent.click(screen.getByRole("button", { name: "Send message" }));
    fireEvent.click(
      await screen.findByRole("button", { name: "Stop generation" }),
    );
    fireEvent.click(await screen.findByRole("button", { name: "Try again" }));
    await waitFor(() =>
      expect(
        vi.mocked(invoke).mock.calls.filter(([name]) => name === "chat_stream"),
      ).toHaveLength(2),
    );
    expect(
      screen.getAllByText("Slow question", { selector: ".user-text" }),
    ).toHaveLength(1);
  });
  it("shows a recoverable local service failure delivered after startup", async () => {
    await setup();
    emit("sidecar-failed", "Process exited");
    fireEvent.click(
      await screen.findByRole("button", { name: "Restart service" }),
    );
    await waitFor(() => expect(invoke).toHaveBeenCalledWith("restart_sidecar"));
    emit("sidecar-ready", null);
    await screen.findByText("Running locally");
  });
});


describe("mini chat window", () => {
  it("removes the old transcript when starting a new chat after expanding", async () => {
    await setup();
    fireEvent.click(screen.getByRole("button", { name: "Saved conversation" }));
    await screen.findByText("Previous answer");
    fireEvent.click(screen.getByRole("button", { name: "Mini chat" }));
    await screen.findByRole("button", { name: "Expand chat" });
    fireEvent.click(screen.getByRole("button", { name: "Expand chat" }));
    await screen.findByRole("button", { name: "Mini chat" });
    fireEvent.click(screen.getByRole("button", { name: /New chat/ }));
    await screen.findByRole("heading", { name: /What’s on your mind/ });
    expect(screen.queryByText("Previous answer")).toBeNull();
    expect(screen.queryByRole("log", { name: "Conversation" })).toBeNull();
  });

  it("restores navigation when opening the knowledge library from mini chat", async () => {
    await setup();
    fireEvent.click(screen.getByRole("button", { name: "Mini chat" }));
    await screen.findByRole("button", { name: "Expand chat" });
    fireEvent.click(screen.getByRole("button", { name: "Tools" }));
    fireEvent.click(screen.getByRole("button", { name: "Open knowledge library" }));
    await screen.findByRole("button", { name: "Close sidebar" });
    expect(miniActive).toBe(false);
    expect(screen.getByRole("complementary", { name: "Workspace navigation" }).hasAttribute("inert")).toBe(false);
  });
  it("keeps a saved conversation and its draft while compacting, pinning and expanding", async () => {
    const input = await setup();
    fireEvent.click(screen.getByRole("button", { name: "Saved conversation" }));
    await screen.findByText("Previous answer");
    fireEvent.change(input, { target: { value: "Keep this exact draft" } });
    fireEvent.click(screen.getByRole("button", { name: "Mini chat" }));
    await screen.findByRole("button", { name: "Expand chat" });
    expect(input.value).toBe("Keep this exact draft");
    fireEvent.click(screen.getByRole("button", { name: "Stay on top" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Stay on top" }).getAttribute("aria-pressed")).toBe("true"));
    fireEvent.click(screen.getByRole("button", { name: "Expand chat" }));
    await screen.findByRole("button", { name: "Mini chat" });
    expect(screen.getByText("Previous answer")).toBeDefined();
    expect(input.value).toBe("Keep this exact draft");
    expect(miniPinned).toBe(false);
    expect(vi.mocked(invoke).mock.calls.filter(([name]) => name === "create_conversation")).toHaveLength(0);
  });
  it("does not cancel or restart a stream when using the compact shortcut", async () => {
    const input = await setup();
    fireEvent.change(input, { target: { value: "A question" } });
    fireEvent.click(screen.getByRole("button", { name: "Send message" }));
    await screen.findByRole("button", { name: "Stop generation" });
    fireEvent.keyDown(window, { key: "M", ctrlKey: true, shiftKey: true });
    await screen.findByRole("button", { name: "Expand chat" });
    expect(screen.getByRole("button", { name: "Stop generation" })).toBeDefined();
    expect(vi.mocked(invoke).mock.calls.filter(([name]) => name === "chat_stream")).toHaveLength(1);
    expect(vi.mocked(invoke).mock.calls.filter(([name]) => name === "cancel_chat")).toHaveLength(0);
  });
  it("keeps the full interface and draft available when the native operation fails", async () => {
    const input = await setup();
    failWindow = true;
    fireEvent.change(input, { target: { value: "Keep me" } });
    fireEvent.click(screen.getByRole("button", { name: "Mini chat" }));
    await screen.findByRole("alert");
    expect(screen.getByRole("button", { name: "Mini chat" }).hasAttribute("disabled")).toBe(false);
    expect(screen.queryByRole("button", { name: "Expand chat" })).toBeNull();
    expect(input.value).toBe("Keep me");
  });
});

describe("cloud chat handoff", () => {
  it("reviews newly included project context even when the Knowledge toggle is off", async () => {
    const original = vi.mocked(invoke).getMockImplementation()!;
    const provider = { id: "openai", label: "OpenAI", connected: true, model: "configured-model", vision: true,
      approved_scopes: ["messages"], consent_id: "messages-only", consent_model: "configured-model" };
    vi.mocked(invoke).mockImplementation(async (command, args) => {
      if (command === "list_projects") return { projects: [{ id: "project-a", name: "Research project", path: null }] };
      if (command === "provider_request") {
        const request = args as { path: string };
        if (request.path === "/providers") return { local_only: false, providers: [provider] };
        if (request.path === "/chat/preview") return { provider: "openai", model: "configured-model", scopes: ["messages", "knowledge"], messages: [{ role: "user", content: "Project excerpt" }] };
      }
      return original(command, args);
    });
    const input = await setup();
    fireEvent.click(screen.getByRole("button", { name: "Saved conversation" }));
    await screen.findByText("Previous answer");
    await screen.findByRole("option", { name: /OpenAI · configured-model/ });
    fireEvent.change(screen.getByLabelText("Chat provider"), { target: { value: "openai" } });
    fireEvent.click(screen.getByRole("button", { name: "Tools" }));
    const memory = screen.getByRole("button", { name: "Memory", pressed: true });
    if (memory.getAttribute("aria-pressed") === "true") fireEvent.click(memory);
    expect(screen.getByRole("button", { name: "Knowledge", pressed: false }).getAttribute("aria-pressed")).toBe("false");
    fireEvent.click(screen.getByRole("button", { name: "Tools" }));
    fireEvent.change(input, { target: { value: "A previously approved message" } });
    fireEvent.click(screen.getByRole("button", { name: "Send message" }));
    await waitFor(() => expect(invoke).toHaveBeenCalledWith("chat_stream", expect.objectContaining({ consentId: "messages-only" })));
    const calls = vi.mocked(invoke).mock.calls.filter(([command]) => command === "chat_stream");
    const turnId = (calls[0][1] as { requestId: string }).requestId;
    emit("chat-token", { request_id: turnId, content: "An answer." });
    emit("chat-done", { request_id: turnId, error: null });
    await waitFor(() => expect(screen.queryByRole("button", { name: "Stop generation" })).toBeNull());
    fireEvent.change(screen.getByLabelText("Move chat to project"), { target: { value: "project-a" } });
    await screen.findByText("Chat added to Research project.");
    fireEvent.change(input, { target: { value: "Use this project" } });
    fireEvent.click(screen.getByRole("button", { name: "Send message" }));
    await screen.findByText("Relevant excerpts from your indexed documents");
    expect(vi.mocked(invoke).mock.calls.filter(([command]) => command === "chat_stream")).toHaveLength(1);
    expect(invoke).toHaveBeenCalledWith("provider_request", expect.objectContaining({ path: "/chat/preview", body: expect.objectContaining({ project_id: "project-a", knowledge_enabled: false }) }));
  });
  it("keeps the draft through review and sends only after granting the selected provider", async () => {
    const original = vi.mocked(invoke).getMockImplementation()!;
    let approved = false;
    const provider = () => ({ id: "openai", label: "OpenAI", connected: true, model: "configured-model", vision: true,
      approved_scopes: approved ? ["messages", "memory"] : [], consent_id: approved ? "receipt-a" : null,
      consent_model: approved ? "configured-model" : null });
    vi.mocked(invoke).mockImplementation(async (command, args) => {
      if (command === "provider_request") {
        const request = args as { path: string; method: string };
        if (request.path === "/providers") return { local_only: false, providers: [provider()] };
        if (request.path === "/chat/preview") return { provider: "openai", model: "configured-model", scopes: ["messages", "memory"], messages: [{ role: "user", content: "My reviewed question" }] };
        if (request.path.endsWith("/consent")) { approved = true; return provider(); }
      }
      return original(command, args);
    });
    const input = await setup();
    await screen.findByRole("option", { name: /OpenAI · configured-model/ });
    fireEvent.change(screen.getByLabelText("Chat provider"), { target: { value: "openai" } });
    fireEvent.change(input, { target: { value: "My reviewed question" } });
    fireEvent.click(screen.getByRole("button", { name: "Send message" }));
    await screen.findByText("Relevant saved memories");
    expect(invoke).not.toHaveBeenCalledWith("chat_stream", expect.anything());
    expect(input.value).toBe("My reviewed question");
    fireEvent.click(screen.getByRole("button", { name: "Allow and send" }));
    await waitFor(() => expect(invoke).toHaveBeenCalledWith("chat_stream", expect.objectContaining({
      provider: "openai", model: "configured-model", consentId: "receipt-a", connectorContextEnabled: false,
    })));
    expect(screen.getByText("Cloud · OpenAI")).toBeDefined();
    expect(screen.queryByRole("dialog", { name: "Review cloud request" })).toBeNull();
  });
});
