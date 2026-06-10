// Chat state management and streaming via Tauri events.
//
// Flow:
//   1. sendMessage() adds user + empty assistant messages, calls chat_stream command.
//   2. Rust emits "chat-token" events; we parse <think>...</think> tags from the stream.
//   3. Content inside <think> populates message.thinking; content after populates message.content.
//   4. Rust emits "chat-done" when streaming ends (or errors).
//   5. Rust emits "chat-sources" before tokens when web search is enabled.
//   6. After a successful reply, onTurnComplete is called so the caller can persist the turn.
//   7. If memoryEnabled, extract_memories runs in the background.
//   8. stopStreaming() resets streaming state; in-flight tokens are ignored.

import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useCallback, useEffect, useRef, useState } from "react";
import { Message, MODEL_MAP, ModelMode } from "../types";

interface ChatTokenPayload {
  content: string;
}

interface ChatDonePayload {
  error: string | null;
}

interface ChatSourcesPayload {
  sources: WebSource[];
}

export interface WebSource {
  title: string;
  url: string;
  snippet: string;
}

interface UseChatOptions {
  modelMode: ModelMode;
  speedModel: string;
  memoryEnabled: boolean;
  knowledgeEnabled: boolean;
  hydeEnabled: boolean;
  webSearchEnabled: boolean;
  embedModel: string;
  /** Called after each successful assistant reply, for conversation persistence. */
  onTurnComplete?: (
    userContent: string,
    assistantContent: string,
    assistantThinking: string | null
  ) => void;
}

// ---------------------------------------------------------------------------
// <think> tag parser
//
// DeepSeek R1 wraps its chain-of-thought in <think>...</think> before the
// final answer. Because tokens arrive one chunk at a time, the tag boundary
// may be split across multiple chunks. We buffer the full raw text and scan
// it on every new token.
//
// Returns:
//   thinking   - text that was inside <think> blocks (may be partial if still open)
//   content    - text outside <think> blocks (the final answer)
//   isThinking - true while the model is still inside an open <think> tag
// ---------------------------------------------------------------------------

function parseThinkTags(raw: string): {
  thinking: string;
  content: string;
  isThinking: boolean;
} {
  let thinking = "";
  let content = "";
  let inThink = false;
  let i = 0;

  while (i < raw.length) {
    if (!inThink) {
      const start = raw.indexOf("<think>", i);
      if (start === -1) {
        content += raw.slice(i);
        break;
      }
      content += raw.slice(i, start);
      i = start + "<think>".length;
      inThink = true;
    } else {
      const end = raw.indexOf("</think>", i);
      if (end === -1) {
        // Still inside the think block - partial content
        thinking += raw.slice(i);
        break;
      }
      thinking += raw.slice(i, end);
      i = end + "</think>".length;
      inThink = false;
    }
  }

  return { thinking, content, isThinking: inThink };
}

// ---------------------------------------------------------------------------
// Hook
// ---------------------------------------------------------------------------

export function useChat({
  modelMode,
  speedModel,
  memoryEnabled,
  knowledgeEnabled,
  hydeEnabled,
  webSearchEnabled,
  embedModel,
  onTurnComplete,
}: UseChatOptions) {
  const [messages, setMessages] = useState<Message[]>([]);
  const [isStreaming, setIsStreaming] = useState(false);
  const [webSources, setWebSources] = useState<WebSource[]>([]);

  const streamingIdRef = useRef<string | null>(null);
  // Raw accumulated text per streaming message (includes <think> tags)
  const rawBufferRef = useRef<string>("");
  // Track the last user message so we can pass it to extraction after the reply
  const lastUserMessageRef = useRef<string>("");
  // Keep these in refs so event-handler closures always see current values
  const memoryEnabledRef = useRef(memoryEnabled);
  const speedModelRef = useRef(speedModel);
  const knowledgeEnabledRef = useRef(knowledgeEnabled);
  const hydeEnabledRef = useRef(hydeEnabled);
  const webSearchEnabledRef = useRef(webSearchEnabled);
  const embedModelRef = useRef(embedModel);
  const onTurnCompleteRef = useRef(onTurnComplete);
  memoryEnabledRef.current = memoryEnabled;
  speedModelRef.current = speedModel;
  knowledgeEnabledRef.current = knowledgeEnabled;
  hydeEnabledRef.current = hydeEnabled;
  webSearchEnabledRef.current = webSearchEnabled;
  embedModelRef.current = embedModel;
  onTurnCompleteRef.current = onTurnComplete;

  useEffect(() => {
    let cancelled = false;
    let unlistenToken: (() => void) | undefined;
    let unlistenDone: (() => void) | undefined;
    let unlistenSources: (() => void) | undefined;

    listen<ChatTokenPayload>("chat-token", (event) => {
      const id = streamingIdRef.current;
      if (!id) return;

      rawBufferRef.current += event.payload.content;
      const { thinking, content, isThinking } = parseThinkTags(rawBufferRef.current);

      setMessages((prev) =>
        prev.map((m) =>
          m.id === id
            ? { ...m, content, thinking, isThinking }
            : m
        )
      );
    }).then((fn) => {
      if (cancelled) fn();
      else unlistenToken = fn;
    });

    listen<ChatDonePayload>("chat-done", (event) => {
      const assistantId = streamingIdRef.current;
      streamingIdRef.current = null;
      rawBufferRef.current = "";
      setIsStreaming(false);

      if (event.payload.error && assistantId) {
        setMessages((prev) =>
          prev.map((m) =>
            m.id === assistantId
              ? { ...m, content: event.payload.error!, isThinking: false, error: true }
              : m
          )
        );
        return;
      }

      // Clear isThinking flag on completion
      if (assistantId) {
        setMessages((prev) => {
          const updated = prev.map((m) =>
            m.id === assistantId ? { ...m, isThinking: false } : m
          );

          // Persist the turn - read the final assistant message from the updated list
          const assistantMsg = updated.find((m) => m.id === assistantId);
          if (assistantMsg && onTurnCompleteRef.current) {
            onTurnCompleteRef.current(
              lastUserMessageRef.current,
              assistantMsg.content,
              assistantMsg.thinking ?? null
            );
          }

          return updated;
        });
      }

      // Trigger background memory extraction after a successful reply.
      if (memoryEnabledRef.current && assistantId) {
        const userMsg = lastUserMessageRef.current;
        setMessages((prev) => {
          const assistantMsg = prev.find((m) => m.id === assistantId);
          if (assistantMsg?.content) {
            invoke("extract_memories", {
              userMessage: userMsg,
              assistantMessage: assistantMsg.content,
              speedModel: speedModelRef.current,
            }).catch(() => {});
          }
          return prev; // no state change - purely a read
        });
      }
    }).then((fn) => {
      if (cancelled) fn();
      else unlistenDone = fn;
    });

    listen<ChatSourcesPayload>("chat-sources", (event) => {
      setWebSources(event.payload.sources);
    }).then((fn) => {
      if (cancelled) fn();
      else unlistenSources = fn;
    });

    return () => {
      cancelled = true;
      unlistenToken?.();
      unlistenDone?.();
      unlistenSources?.();
    };
  }, []);

  const sendMessage = useCallback(
    async (text: string) => {
      const trimmed = text.trim();
      if (!trimmed || isStreaming) return;

      const userMsg: Message = {
        id: crypto.randomUUID(),
        role: "user",
        content: trimmed,
      };

      const assistantMsg: Message = {
        id: crypto.randomUUID(),
        role: "assistant",
        content: "",
        thinking: "",
        isThinking: false,
      };

      const history = messages.map((m) => ({
        role: m.role,
        content: m.content,
      }));

      setMessages((prev) => [...prev, userMsg, assistantMsg]);
      setIsStreaming(true);
      streamingIdRef.current = assistantMsg.id;
      rawBufferRef.current = "";
      lastUserMessageRef.current = trimmed;
      // Clear sources from previous turn
      setWebSources([]);

      try {
        await invoke("chat_stream", {
          message: trimmed,
          model: MODEL_MAP[modelMode],
          history,
          memoryEnabled,
          // These are read via refs inside the callback - listed here for
          // correctness but refs ensure closures always see the latest value.
          knowledgeEnabled: knowledgeEnabledRef.current,
          embedModel: embedModelRef.current,
          hydeEnabled: hydeEnabledRef.current,
          webSearchEnabled: webSearchEnabledRef.current,
        });
      } catch (e) {
        const id = streamingIdRef.current;
        streamingIdRef.current = null;
        rawBufferRef.current = "";
        setIsStreaming(false);
        if (id) {
          setMessages((prev) =>
            prev.map((m) =>
              m.id === id
                ? { ...m, content: String(e), isThinking: false, error: true }
                : m
            )
          );
        }
      }
    },
    // knowledgeEnabled, webSearchEnabled, embedModel are read via refs -
    // they are not direct deps because they are accessed via ref.current at call time.
    [isStreaming, messages, modelMode, memoryEnabled]
  );

  // Load a set of persisted messages (e.g. when switching conversations).
  const loadMessages = useCallback((msgs: Message[]) => {
    setMessages(msgs);
  }, []);

  // Clear all messages (e.g. when starting a new conversation).
  const clearMessages = useCallback(() => {
    setMessages([]);
    setWebSources([]);
  }, []);

  const stopStreaming = useCallback(() => {
    streamingIdRef.current = null;
    rawBufferRef.current = "";
    setIsStreaming(false);
  }, []);

  return { messages, isStreaming, webSources, sendMessage, stopStreaming, loadMessages, clearMessages };
}
