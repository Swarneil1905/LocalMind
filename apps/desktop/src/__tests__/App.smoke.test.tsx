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
        { id: "old-user", role: "user", content: "Previous question" },
        { id: "old-answer", role: "assistant", content: "Previous answer" },
      ];
    }
    if (command.startsWith("list_") || command === "get_followup_questions")
      return [];
    return null;
  });
});
afterEach(cleanup);

describe("desktop chat workflow", () => {
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
