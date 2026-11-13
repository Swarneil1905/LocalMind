import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useCallback, useEffect, useRef, useState } from "react";
import { Message, ModelMode, WebSource, ResponseDiagnostics } from "../types";
import type { ChatAttachment } from "../lib/attachments";
import type { ProviderId } from "../lib/providers";
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
    attachmentIds?: string[],
  ) => void | Promise<void>;
  onTurnFailure?: (text: string, conversationId: string | null, selection?: SendSelection) => void;
}

export interface SendSelection {
  provider?: ProviderId;
  model?: string;
  consentId?: string | null;
  attachments?: ChatAttachment[];
  attachmentDraftId?: string;
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
  startedAt: number;
  firstVisible?: number;
  id: string;
  user: string;
  raw: string;
  thinking: string;
  sources: WebSource[];
  conversationId: string | null;
  options: UseChatOptions;
  selection: SendSelection;
  finishing?: boolean;
}

function completedDiagnostics(turn: Turn, previous: ResponseDiagnostics | undefined, outcome: "completed" | "failed" | "cancelled"): ResponseDiagnostics {
  return {
    model: turn.selection.model || (turn.options.modelMode === "speed" ? turn.options.speedModel : turn.options.balancedModel),
    context_window: null, response_tokens: null, history_dropped: 0, references_dropped: 0, references_shortened: 0,
    total_seconds: null, first_token_seconds: null, input_tokens: null, output_tokens: null, tokens_per_second: null, stop_reason: "unknown",
    ...previous,
    client_first_answer_seconds: turn.firstVisible ?? null,
    client_total_seconds: (performance.now() - turn.startedAt) / 1000,
    outcome,
  };
}

function restoreDraft(turn: Turn) {
  if (turn.selection.attachments?.length) turn.options.onTurnFailure?.(turn.user, turn.conversationId, turn.selection);
  else turn.options.onTurnFailure?.(turn.user, turn.conversationId);
}

export function useChat(options: UseChatOptions) {
  const [messages, setMessages] = useState<Message[]>([]);
  const [isStreaming, setIsStreaming] = useState(false);
  const [isSearching, setIsSearching] = useState(false);
  const [webSources, setWebSources] = useState<WebSource[]>([]);
  const active = useRef<Turn | null>(null);
  const historyRef = useRef<Message[]>([]);
  const listenersReady = useRef<Promise<unknown>>(Promise.resolve());
  const persistence = useRef<Promise<void>>(Promise.resolve());

  const updateMessages = useCallback(
    (update: (prev: Message[]) => Message[]) => {
      historyRef.current = update(historyRef.current);
      setMessages(historyRef.current);
    },
    [],
  );

  const saveInterrupted = useCallback((turn: Turn, error: string | null, cancelled: boolean) => {
    if (!turn.conversationId) return;
    const parsed = parseThinkTags(turn.raw);
          if (parsed.content.trim() && turn.firstVisible === undefined) turn.firstVisible = (performance.now() - turn.startedAt) / 1000;
    persistence.current = persistence.current.catch(() => {}).then(async () => {
      await invoke("save_interrupted_turn", {
        conversationId: turn.conversationId, requestId: turn.id, userContent: turn.user,
        assistantContent: parsed.content, assistantThinking: turn.thinking || parsed.thinking || null,
        error, cancelled, attachmentIds: turn.selection.attachments?.map(item => item.id) || [],
      });
    }).catch(() => {
      updateMessages(prev => prev.map(message => message.id === turn.id
        ? { ...message, errorMessage: "The interrupted reply could not be saved. Copy it before leaving this chat." } : message));
    });
  }, [updateMessages]);

  const finish = useCallback(
    async (turn: Turn, error?: string | null) => {
      if (active.current !== turn || turn.finishing) return;
      turn.finishing = true;
      setIsSearching(false);
      const parsed = parseThinkTags(turn.raw);
      if (parsed.content.trim() && turn.firstVisible === undefined) turn.firstVisible = (performance.now() - turn.startedAt) / 1000;
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
                diagnostics: completedDiagnostics(turn, m.diagnostics, failure ? "failed" : "completed"),
              }
            : m,
        ),
      );
      if (failure) {
        saveInterrupted(turn, failure, false);
        active.current = null; setIsStreaming(false);
        restoreDraft(turn);
        return;
      }
      // Effects run once, outside React state updater functions (including StrictMode).
      try {
        const args: Parameters<NonNullable<UseChatOptions["onTurnComplete"]>> = [
          turn.user, parsed.content, turn.thinking || parsed.thinking || null,
          turn.conversationId, turn.sources,
        ];
        if (turn.selection.attachments?.length) args.push(turn.selection.attachments.map(item => item.id));
        const saved = turn.options.onTurnComplete?.(...args);
        if (saved) {
          persistence.current = saved.catch(() => {});
          await saved;
        }
      } catch {
        if (active.current === turn) {
          const error = "This reply could not be saved. Copy it before leaving this chat.";
          updateMessages(prev => prev.map(message => message.id === turn.id ? { ...message, error: true, errorMessage: error } : message));
          saveInterrupted(turn, error, false);
          active.current = null; setIsStreaming(false);
          restoreDraft(turn);
        }
        return;
      }
      if (active.current !== turn) return;
      active.current = null; setIsStreaming(false);
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
    [updateMessages, saveInterrupted],
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
          if (!turn || p.request_id !== turn.id || turn.finishing) return;
          if (p.phase === "searching" || p.phase === "answering") {
            setIsSearching(p.phase === "searching");
          }
        },
      ),
      register<{ request_id: string; content: string; thinking?: string }>(
        "chat-token",
        (p) => {
          const turn = active.current;
          if (!turn || p.request_id !== turn.id || turn.finishing) return;
          setIsSearching(false);
          turn.raw += p.content;
          turn.thinking += p.thinking || "";
          const parsed = parseThinkTags(turn.raw);
          if (parsed.content.trim() && turn.firstVisible === undefined) turn.firstVisible = (performance.now() - turn.startedAt) / 1000;
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
      register<{ request_id: string; data: ResponseDiagnostics }>("chat-diagnostics", (p) => {
        const turn = active.current;
        if (!turn || p.request_id !== turn.id) return;
        updateMessages(prev => prev.map(message => message.id === turn.id
          ? { ...message, diagnostics: { ...p.data, client_first_answer_seconds: turn.firstVisible ?? null } } : message));
      }),
      register<{ request_id: string; data: Message["usage"] }>("chat-usage", payload => {
        const turn = active.current;
        if (!turn || payload.request_id !== turn.id || turn.finishing) return;
        updateMessages(previous => previous.map(message => message.id === turn.id ? { ...message, usage: payload.data } : message));
      }),
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
          m.id === turn.id ? { ...m, isThinking: false, cancelled: true, diagnostics: completedDiagnostics(turn, m.diagnostics, "cancelled") } : m,
        ),
      );
      void invoke("cancel_chat", { requestId: turn.id }).catch(() => {});
      if (!turn.finishing) {
        saveInterrupted(turn, null, true);
        restoreDraft(turn);
      }
    }
  }, [updateMessages, saveInterrupted]);

  const sendMessage = useCallback(
    async (text: string, conversationId: string | null = null, requestId?: string, selection: SendSelection = {}) => {
      const user = text.trim();
      if (!user || active.current) return;
      const turn: Turn = {
        startedAt: performance.now(),
        id: requestId || crypto.randomUUID(),
        user,
        raw: "",
        thinking: "",
        sources: [],
        conversationId,
        options,
        selection,
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
        .map((m) => ({ role: m.role, content: m.content, ...(m.attachments?.length ? { attachment_ids: m.attachments.map(item => item.id) } : {}) }));
      updateMessages((prev) => [
        ...prev,
        { id: crypto.randomUUID(), role: "user", content: user, attachments: selection.attachments, attachmentDraftId: selection.attachmentDraftId, conversation_id: conversationId ?? undefined },
        { id: turn.id, role: "assistant", content: "" },
      ]);
      setIsStreaming(true);
      setIsSearching(false);
      setWebSources([]);
      try {
        await listenersReady.current;
        await persistence.current;
        if (active.current !== turn) return;
        await invoke("chat_stream", {
          requestId: turn.id,
          message: user,
          model: selection.model || (options.modelMode === "speed"
              ? options.speedModel
              : options.balancedModel),
          provider: selection.provider || "ollama",
          consentId: selection.consentId ?? null,
          attachmentIds: selection.attachments?.map(item => item.id) ?? [],
          attachmentDraftId: selection.attachmentDraftId ?? null,
          connectorContextEnabled: false,
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
  const waitForPersistence = useCallback(() => persistence.current, []);
  return {
    messages,
    isStreaming,
    isSearching,
    webSources,
    sendMessage,
    stopStreaming,
    loadMessages,
    clearMessages,
    waitForPersistence,
  };
}
