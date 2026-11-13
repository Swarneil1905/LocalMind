import { StrictMode } from "react";
import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useChat, parseThinkTags } from "../hooks/useChat";

const handlers = new Map<string, (event: { payload: unknown }) => void>();
const complete = vi.fn();
const options = {
  modelMode: "balanced" as const,
  speedModel: "speed-custom",
  balancedModel: "chat-custom",
  memoryEnabled: true,
  knowledgeEnabled: false,
  hydeEnabled: false,
  webSearchEnabled: true,
  embedModel: "embed",
  onTurnComplete: complete,
};
const emit = (name: string, payload: unknown) =>
  act(() => handlers.get(name)?.({ payload }));
const start = async (
  result: { current: ReturnType<typeof useChat> },
  text: string,
) => {
  await act(() => result.current.sendMessage(text, "conversation-a"));
  const calls = vi
    .mocked(invoke)
    .mock.calls.filter(([name]) => name === "chat_stream");
  return (calls[calls.length - 1][1] as { requestId: string }).requestId;
};
beforeEach(() => {
  handlers.clear();
  vi.clearAllMocks();
  vi.mocked(listen).mockImplementation(async (name, handler) => {
    handlers.set(name, handler as (event: { payload: unknown }) => void);
    return () => {
      if (handlers.get(name) === handler) handlers.delete(name);
    };
  });
  vi.mocked(invoke).mockResolvedValue(undefined);
});

describe("request lifecycle", () => {
  it("does not extract memory from a reply whose durable save failed", async () => {
    const failed = vi.fn();
    const { result } = renderHook(() => useChat({ ...options, onTurnComplete: async () => { throw new Error("Database unavailable"); }, onTurnFailure: failed }));
    const id = await start(result, "Help with the draft");
    emit("chat-token", { request_id: id, content: "A useful answer to preserve." });
    emit("chat-done", { request_id: id, error: null });
    await waitFor(() => expect(result.current.isStreaming).toBe(false));
    expect(result.current.messages[1]).toMatchObject({ content: "A useful answer to preserve.", error: true });
    expect(failed).toHaveBeenCalledWith("Help with the draft", "conversation-a");
    expect(invoke).not.toHaveBeenCalledWith("extract_memories", expect.anything());
    await waitFor(() => expect(invoke).toHaveBeenCalledWith("save_interrupted_turn", expect.objectContaining({ requestId: id, assistantContent: "A useful answer to preserve." })));
  });
  it("waits for persistence before accepting a follow-up and ignores chunks after completion", async () => {
    let save!: () => void;
    const persist = vi.fn(() => new Promise<void>(resolve => { save = resolve; }));
    const { result } = renderHook(() => useChat({ ...options, onTurnComplete: persist }));
    const id = await start(result, "Explain tides");
    emit("chat-token", { request_id: id, content: "Gravity drives tides." });
    emit("chat-done", { request_id: id, error: null });
    emit("chat-token", { request_id: id, content: "Late incorrect chunk" });
    await act(() => result.current.sendMessage("Explain more", "conversation-a"));
    expect(vi.mocked(invoke).mock.calls.filter(([name]) => name === "chat_stream")).toHaveLength(1);
    expect(result.current.messages[1].content).toBe("Gravity drives tides.");
    await act(async () => save());
    expect(result.current.isStreaming).toBe(false);
    await act(() => result.current.sendMessage("Explain more", "conversation-a"));
    expect(vi.mocked(invoke).mock.calls.filter(([name]) => name === "chat_stream")).toHaveLength(2);
  });
  it("snapshots the cloud provider and attachment identity without replaying a failed generation", async () => {
    const failed = vi.fn();
    const { result } = renderHook(() => useChat({ ...options, onTurnFailure: failed }));
    const attachment = { id: "attachment-a", filename: "image.png", mime_type: "image/png", bytes: 8, width: 1, height: 1 };
    await act(() => result.current.sendMessage("Explain this", "conversation-a", "turn-a", {
      provider: "anthropic", model: "configured-model", consentId: "consent-a", attachments: [attachment], attachmentDraftId: "draft-a",
    }));
    expect(invoke).toHaveBeenCalledWith("chat_stream", expect.objectContaining({ provider: "anthropic", model: "configured-model",
      consentId: "consent-a", attachmentIds: ["attachment-a"], attachmentDraftId: "draft-a", connectorContextEnabled: false }));
    emit("chat-token", { request_id: "turn-a", content: "The image shows" });
    emit("chat-done", { request_id: "turn-a", error: "Connection lost. A charge may have occurred." });
    expect(failed).toHaveBeenCalledWith("Explain this", "conversation-a", expect.objectContaining({ attachmentDraftId: "draft-a", attachments: [attachment] }));
    expect(result.current.messages[0].attachments).toEqual([attachment]);
    expect(result.current.messages[1].content).toBe("The image shows");
    expect(vi.mocked(invoke).mock.calls.filter(([name]) => name === "chat_stream")).toHaveLength(1);
  });
  it("binds diagnostics to their active response and ignores late events from a previous turn", async () => {
    const { result } = renderHook(() => useChat(options));
    const id = await start(result, "First question");
    const data = { model: "chat-custom", first_token_seconds: 0.4, history_dropped: 2 };
    emit("chat-diagnostics", { request_id: "other", data });
    expect(result.current.messages[1].diagnostics).toBeUndefined();
    emit("chat-token", { request_id: id, content: "First answer" });
    emit("chat-diagnostics", { request_id: id, data });
    emit("chat-done", { request_id: id, error: null });
    expect(result.current.messages[1].diagnostics).toMatchObject(data);
    await start(result, "Next question");
    emit("chat-diagnostics", { request_id: id, data: { model: "wrong" } });
    expect(result.current.messages[3].diagnostics).toBeUndefined();
    expect(result.current.messages[1].diagnostics).toMatchObject(data);
  });
  it("uses the reviewed voice turn identity and saves the normal text conversation once", async () => {
    const { result } = renderHook(() => useChat(options));
    const turnId = crypto.randomUUID();
    await act(() => result.current.sendMessage("Reviewed transcript", "voice-conversation", turnId));
    expect(invoke).toHaveBeenCalledWith("chat_stream", expect.objectContaining({ requestId: turnId, message: "Reviewed transcript" }));
    emit("chat-token", { request_id: turnId, content: "Text answer." });
    emit("chat-done", { request_id: turnId, error: null });
    emit("chat-done", { request_id: turnId, error: null });
    expect(complete).toHaveBeenCalledOnce();
    expect(complete).toHaveBeenCalledWith("Reviewed transcript", "Text answer.", null, "voice-conversation", []);
  });
  it("keeps project, conversation and profile bound to the completed turn", async () => {
    const { result, rerender } = renderHook(
      ({ projectId }) => useChat({ ...options, projectId }),
      { initialProps: { projectId: "atlas" } },
    );
    const id = await start(result, "Explain the Atlas plan");
    expect(invoke).toHaveBeenCalledWith("chat_stream", expect.objectContaining({
      projectId: "atlas", conversationId: "conversation-a", profile: "balanced",
    }));
    rerender({ projectId: "other" });
    emit("chat-token", { request_id: id, content: "The Atlas plan has two steps." });
    emit("chat-done", { request_id: id, error: null });
    expect(invoke).toHaveBeenCalledWith("extract_memories", expect.objectContaining({
      projectId: "atlas", conversationId: "conversation-a",
    }));
  });

  it("shows searching only while the active request reports actual retrieval", async () => {
    const { result } = renderHook(() => useChat(options));
    const id = await start(result, "explain trees");
    expect(result.current.isStreaming).toBe(true);
    expect(result.current.isSearching).toBe(false);
    emit("chat-status", { request_id: id, phase: "searching" });
    expect(result.current.isSearching).toBe(true);
    emit("chat-status", { request_id: id, phase: "unknown" });
    expect(result.current.isSearching).toBe(true);
    emit("chat-status", { request_id: id, phase: "answering" });
    expect(result.current.isSearching).toBe(false);
    emit("chat-token", { request_id: id, content: "A tree splits examples." });
    emit("chat-done", { request_id: id, error: null });
    emit("chat-status", { request_id: id, phase: "searching" });
    expect(result.current.isSearching).toBe(false);
  });

  it("clears searching on cancellation and ignores old status during a new request", async () => {
    const { result } = renderHook(() => useChat(options));
    const old = await start(result, "latest news");
    emit("chat-status", { request_id: old, phase: "searching" });
    expect(result.current.isSearching).toBe(true);
    act(() => result.current.stopStreaming());
    expect(result.current.isSearching).toBe(false);
    const current = await start(result, "new question");
    emit("chat-status", { request_id: old, phase: "searching" });
    expect(result.current.isSearching).toBe(false);
    emit("chat-status", { request_id: current, phase: "searching" });
    emit("chat-status", { request_id: old, phase: "answering" });
    expect(result.current.isSearching).toBe(true);
    emit("chat-done", { request_id: current, error: "Retrieval stopped." });
    expect(result.current.isSearching).toBe(false);
  });

  it("keeps older token-only streams working without a status event", async () => {
    const { result } = renderHook(() => useChat(options));
    const id = await start(result, "hello");
    expect(result.current.isSearching).toBe(false);
    emit("chat-token", { request_id: id, content: "Hello." });
    emit("chat-done", { request_id: id, error: null });
    expect(result.current.messages[1].content).toBe("Hello.");
    expect(result.current.isStreaming).toBe(false);
    expect(complete).toHaveBeenCalledTimes(1);
  });

  it("ignores old tokens, sources, completion and errors after stop and resend", async () => {
    const { result } = renderHook(() => useChat(options));
    const old = await start(result, "old question");
    act(() => result.current.stopStreaming());
    const current = await start(result, "hello");
    emit("chat-token", { request_id: old, content: "old news" });
    emit("chat-sources", { request_id: old, sources: [{ title: "old" }] });
    emit("chat-done", { request_id: old, error: "late error" });
    expect(result.current.isStreaming).toBe(true);
    expect(result.current.webSources).toEqual([]);
    emit("chat-token", { request_id: current, content: "Hello." });
    emit("chat-done", { request_id: current, error: null });
    expect(result.current.messages[3].content).toBe("Hello.");
    expect(complete).toHaveBeenCalledTimes(1);
    expect(invoke).toHaveBeenCalledWith("cancel_chat", { requestId: old });
  });
  it("persists and extracts exactly once in StrictMode, and uses configured models", async () => {
    const { result } = renderHook(() => useChat(options), {
      wrapper: StrictMode,
    });
    const id = await start(result, "question");
    emit("chat-token", { request_id: id, content: "answer" });
    emit("chat-done", { request_id: id, error: null });
    emit("chat-done", { request_id: id, error: null });
    expect(complete).toHaveBeenCalledExactlyOnceWith(
      "question",
      "answer",
      null,
      "conversation-a",
      [],
    );
    expect(
      vi
        .mocked(invoke)
        .mock.calls.filter(([name]) => name === "extract_memories"),
    ).toHaveLength(1);
    expect(invoke).toHaveBeenCalledWith(
      "chat_stream",
      expect.objectContaining({ model: "chat-custom" }),
    );
  });
  it("blocks a double send before React rerenders and cancels when loading another chat", async () => {
    const { result } = renderHook(() => useChat(options));
    await act(async () => {
      await Promise.all([
        result.current.sendMessage("one"),
        result.current.sendMessage("two"),
      ]);
    });
    expect(
      vi.mocked(invoke).mock.calls.filter(([name]) => name === "chat_stream"),
    ).toHaveLength(1);
    act(() =>
      result.current.loadMessages([
        { id: "saved", role: "user", content: "another chat" },
      ]),
    );
    expect(result.current.isStreaming).toBe(false);
    expect(result.current.messages).toHaveLength(1);
  });
  it("waits for event listener registration before invoking the stream", async () => {
    let ready!: () => void;
    const gate = new Promise<void>((resolve) => {
      ready = resolve;
    });
    vi.mocked(listen).mockImplementation(async () => {
      await gate;
      return () => {};
    });
    const { result } = renderHook(() => useChat(options));
    let sending!: Promise<void>;
    act(() => {
      sending = result.current.sendMessage("hello");
    });
    expect(invoke).not.toHaveBeenCalled();
    await act(async () => {
      ready();
      await sending;
    });
    await waitFor(() =>
      expect(invoke).toHaveBeenCalledWith("chat_stream", expect.anything()),
    );
  });
  it("preserves a partial answer on failure without saving it as complete", async () => {
    const { result } = renderHook(() => useChat(options));
    const id = await start(result, "Make a plan");
    emit("chat-token", { request_id: id, content: "Start with a short walk." });
    emit("chat-done", { request_id: id, error: "The reply reached its length limit." });
    expect(result.current.messages[1]).toMatchObject({
      content: "Start with a short walk.",
      error: true,
      errorMessage: "The reply reached its length limit.",
    });
    expect(complete).not.toHaveBeenCalled();
    expect(invoke).not.toHaveBeenCalledWith("extract_memories", expect.anything());
    expect(result.current.isStreaming).toBe(false);
    await start(result, "A different question");
    expect(invoke).toHaveBeenLastCalledWith("chat_stream", expect.objectContaining({ history: [] }));
  });

  it("does not persist errors or empty replies", async () => {
    const { result } = renderHook(() => useChat(options));
    const id = await start(result, "question");
    emit("chat-done", { request_id: id, error: null });
    expect(result.current.messages[1].error).toBe(true);
    expect(complete).not.toHaveBeenCalled();
    expect(result.current.isSearching).toBe(false);
  });
});

it("buffers split reasoning delimiters", () => {
  expect(parseThinkTags("<thi").content).toBe("");
  expect(parseThinkTags("<think>working</thi").thinking).toBe("working");
  expect(parseThinkTags("<think>working</think>Answer").content).toBe("Answer");
});

it("measures answer arrival separately from reasoning and retains cancelled timing", async () => {
  let now = 0;
  const clock = vi.spyOn(performance, "now").mockImplementation(() => now);
  try {
    const { result, unmount } = renderHook(() => useChat(options));
    const id = await start(result, "Explain this");
    now = 200;
    emit("chat-token", { request_id: id, content: "<think>working" });
    now = 500;
    emit("chat-token", { request_id: id, content: "</think>Answer" });
    now = 1500;
    act(() => result.current.stopStreaming());
    expect(result.current.messages[1].diagnostics).toMatchObject({
      client_first_answer_seconds: 0.5, client_total_seconds: 1.5, outcome: "cancelled",
      total_seconds: null, context_window: null,
    });
    emit("chat-diagnostics", { request_id: id, data: { model: "late" } });
    expect(result.current.messages[1].diagnostics?.outcome).toBe("cancelled");
    unmount();
  } finally { clock.mockRestore(); }
});
