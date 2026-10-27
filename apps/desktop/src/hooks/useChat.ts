import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useCallback, useEffect, useRef, useState } from "react";
import { Message, ModelMode, WebSource } from "../types";
export type { WebSource } from "../types";

interface UseChatOptions {
  modelMode: ModelMode;
  speedModel: string;
  balancedModel: string;
  memoryEnabled: boolean;
  knowledgeEnabled: boolean;
  hydeEnabled: boolean;
  webSearchEnabled: boolean;
  embedModel: string;
  projectId?: string | null;
  onTurnComplete?: (
    user: string,
    answer: string,
    thinking: string | null,
    conversationId: string | null,
    sources: WebSource[],
  ) => void;
}

// Hold incomplete tags until their next chunk, so markup never flashes in the answer.
export function parseThinkTags(raw: string) {
  let text = raw;
  for (const tag of ["<think>", "</think>"]) {
    for (let n = 1; n < tag.length; n++) {
      if (text.endsWith(tag.slice(0, n))) {
        text = text.slice(0, -n);
        break;
      }
    }
  }
  let thinking = "";
  const content = text.replace(
    /<think>([\s\S]*?)(?:<\/think>|$)/g,
    (_, body: string) => {
      thinking += body;
      return "";
    },
  );
  return {
    content,
    thinking,
    isThinking: text.lastIndexOf("<think>") > text.lastIndexOf("</think>"),
  };
}

interface Turn {
  id: string;
  user: string;
  raw: string;
  thinking: string;
  sources: WebSource[];
  conversationId: string | null;
  options: UseChatOptions;
}

export function useChat(options: UseChatOptions) {
  const [messages, setMessages] = useState<Message[]>([]);
  const [isStreaming, setIsStreaming] = useState(false);
  const [isSearching, setIsSearching] = useState(false);
  const [webSources, setWebSources] = useState<WebSource[]>([]);
  const active = useRef<Turn | null>(null);
  const historyRef = useRef<Message[]>([]);
  const listenersReady = useRef<Promise<unknown>>(Promise.resolve());

  const updateMessages = useCallback(
    (update: (prev: Message[]) => Message[]) => {
      historyRef.current = update(historyRef.current);
      setMessages(historyRef.current);
    },
    [],
  );

  const finish = useCallback(
    (turn: Turn, error?: string | null) => {
      if (active.current !== turn) return;
      active.current = null;
      setIsStreaming(false);
      setIsSearching(false);
      const parsed = parseThinkTags(turn.raw);
      const failure =
        error ||
        (!parsed.content.trim()
          ? "The model returned an empty reply. Please try again."
          : null);
      updateMessages((prev) =>
        prev.map((m) =>
          m.id === turn.id
            ? {
                ...m,
                content: parsed.content.trim() ? parsed.content : failure || "",
                errorMessage: failure && parsed.content.trim() ? failure : undefined,
                thinking: turn.thinking || parsed.thinking,
                isThinking: false,
                error: !!failure,
              }
            : m,
        ),
      );
      if (failure) return;
      // Effects run once, outside React state updater functions (including StrictMode).
      turn.options.onTurnComplete?.(
        turn.user,
        parsed.content,
        turn.thinking || parsed.thinking || null,
        turn.conversationId,
        turn.sources,
      );
      if (turn.options.memoryEnabled) {
        void invoke("extract_memories", {
          userMessage: turn.user,
          assistantMessage: parsed.content,
          speedModel: turn.options.speedModel,
          projectId: turn.options.projectId ?? null,
          conversationId: turn.conversationId,
        }).catch(() => {});
      }
    },
    [updateMessages],
  );

  useEffect(() => {
    let disposed = false;
    const unlisteners: (() => void)[] = [];
    const register = async <T>(name: string, handler: (payload: T) => void) => {
      const off = await listen<T>(name, (event) => {
        if (!disposed) handler(event.payload);
      });
      if (disposed) off();
      else unlisteners.push(off);
    };
    listenersReady.current = Promise.all([
      register<{ request_id: string; phase: "searching" | "answering" }>(
        "chat-status",
        (p) => {
          const turn = active.current;
          if (!turn || p.request_id !== turn.id) return;
          if (p.phase === "searching" || p.phase === "answering") {
            setIsSearching(p.phase === "searching");
          }
        },
      ),
      register<{ request_id: string; content: string; thinking?: string }>(
        "chat-token",
        (p) => {
          const turn = active.current;
          if (!turn || p.request_id !== turn.id) return;
          setIsSearching(false);
          turn.raw += p.content;
          turn.thinking += p.thinking || "";
          const parsed = parseThinkTags(turn.raw);
          updateMessages((prev) =>
            prev.map((m) =>
              m.id === turn.id
                ? {
                    ...m,
                    ...parsed,
                    thinking: turn.thinking || parsed.thinking,
                    isThinking: !!p.thinking || parsed.isThinking,
                  }
                : m,
            ),
          );
        },
      ),
      register<{ request_id: string; error: string | null }>(
        "chat-done",
        (p) => {
          const turn = active.current;
          if (turn && p.request_id === turn.id) finish(turn, p.error);
        },
      ),
      register<{ request_id: string; sources: WebSource[] }>(
        "chat-sources",
        (p) => {
          const turn = active.current;
          if (!turn || p.request_id !== turn.id) return;
          turn.sources = p.sources;
          setWebSources(p.sources);
          setIsSearching(false);
          updateMessages((prev) =>
            prev.map((m) =>
              m.id === turn.id ? { ...m, sources: p.sources } : m,
            ),
          );
        },
      ),
    ]);
    // Prevent an unhandled rejection before the first send awaits registration.
    void listenersReady.current.catch(() => {});
    return () => {
      disposed = true;
      unlisteners.forEach((off) => off());
      if (active.current)
        void invoke("cancel_chat", { requestId: active.current.id }).catch(
          () => {},
        );
      active.current = null;
    };
  }, [finish, updateMessages]);

  const stopStreaming = useCallback(() => {
    const turn = active.current;
    active.current = null; // Invalidate first: late events must never mutate another turn.
    setIsStreaming(false);
    setIsSearching(false);
    if (turn) {
      updateMessages((prev) =>
        prev.map((m) =>
          m.id === turn.id ? { ...m, isThinking: false, cancelled: true } : m,
        ),
      );
      void invoke("cancel_chat", { requestId: turn.id }).catch(() => {});
    }
  }, [updateMessages]);

  const sendMessage = useCallback(
    async (text: string, conversationId: string | null = null, requestId?: string) => {
      const user = text.trim();
      if (!user || active.current) return;
      const turn: Turn = {
        id: requestId || crypto.randomUUID(),
        user,
        raw: "",
        thinking: "",
        sources: [],
        conversationId,
        options,
      };
      active.current = turn;
      const history = historyRef.current
        .filter(
          (m, i, all) =>
            !m.error &&
            !m.cancelled &&
            m.content.trim() &&
            !(
              m.role === "user" &&
              (all[i + 1]?.error || all[i + 1]?.cancelled)
            ),
        )
        .map((m) => ({ role: m.role, content: m.content }));
      updateMessages((prev) => [
        ...prev,
        { id: crypto.randomUUID(), role: "user", content: user },
        { id: turn.id, role: "assistant", content: "" },
      ]);
      setIsStreaming(true);
      setIsSearching(false);
      setWebSources([]);
      try {
        await listenersReady.current;
        if (active.current !== turn) return;
        await invoke("chat_stream", {
          requestId: turn.id,
          message: user,
          model:
            options.modelMode === "speed"
              ? options.speedModel
              : options.balancedModel,
          history,
          memoryEnabled: options.memoryEnabled,
          knowledgeEnabled: options.knowledgeEnabled,
          embedModel: options.embedModel,
          hydeEnabled: options.hydeEnabled,
          webSearchEnabled: options.webSearchEnabled,
          projectId: options.projectId ?? null,
          conversationId: turn.conversationId,
          profile: options.modelMode,
        });
      } catch (error) {
        finish(turn, String(error));
      }
    },
    [options, finish, updateMessages],
  );

  const loadMessages = useCallback(
    (msgs: Message[]) => {
      stopStreaming();
      updateMessages(() => msgs);
      setWebSources([]);
    },
    [stopStreaming, updateMessages],
  );
  const clearMessages = useCallback(() => loadMessages([]), [loadMessages]);
  return {
    messages,
    isStreaming,
    isSearching,
    webSources,
    sendMessage,
    stopStreaming,
    loadMessages,
    clearMessages,
  };
}
