// Chat state management and streaming via Tauri events.
//
// Flow:
//   1. sendMessage() adds user + empty assistant messages, calls chat_stream command.
//   2. Rust emits "chat-token" events; we append each token to the assistant message.
//   3. Rust emits "chat-done" when streaming ends (or errors).
//   4. After a successful reply, if memoryEnabled, calls extract_memories in the background.
//   5. stopStreaming() resets streaming state; in-flight tokens are ignored.

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

interface UseChatOptions {
  modelMode: ModelMode;
  speedModel: string;
  memoryEnabled: boolean;
  knowledgeEnabled: boolean;
  embedModel: string;
}

export function useChat({ modelMode, speedModel, memoryEnabled, knowledgeEnabled, embedModel }: UseChatOptions) {
  const [messages, setMessages] = useState<Message[]>([]);
  const [isStreaming, setIsStreaming] = useState(false);

  const streamingIdRef = useRef<string | null>(null);
  // Track the last user message so we can pass it to extraction after the reply
  const lastUserMessageRef = useRef<string>("");
  // Keep these in refs so event-handler closures always see current values
  const memoryEnabledRef = useRef(memoryEnabled);
  const speedModelRef = useRef(speedModel);
  const knowledgeEnabledRef = useRef(knowledgeEnabled);
  const embedModelRef = useRef(embedModel);
  memoryEnabledRef.current = memoryEnabled;
  speedModelRef.current = speedModel;
  knowledgeEnabledRef.current = knowledgeEnabled;
  embedModelRef.current = embedModel;

  useEffect(() => {
    let cancelled = false;
    let unlistenToken: (() => void) | undefined;
    let unlistenDone: (() => void) | undefined;

    listen<ChatTokenPayload>("chat-token", (event) => {
      const id = streamingIdRef.current;
      if (!id) return;
      setMessages((prev) =>
        prev.map((m) =>
          m.id === id ? { ...m, content: m.content + event.payload.content } : m
        )
      );
    }).then((fn) => {
      if (cancelled) fn();
      else unlistenToken = fn;
    });

    listen<ChatDonePayload>("chat-done", (event) => {
      const assistantId = streamingIdRef.current;
      streamingIdRef.current = null;
      setIsStreaming(false);

      if (event.payload.error && assistantId) {
        setMessages((prev) =>
          prev.map((m) =>
            m.id === assistantId
              ? { ...m, content: event.payload.error!, error: true }
              : m
          )
        );
        return;
      }

      // Trigger background memory extraction after a successful reply.
      // We read the final assistant content via a no-op state update so we
      // have access to the latest messages snapshot.
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

    return () => {
      cancelled = true;
      unlistenToken?.();
      unlistenDone?.();
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
      };

      const history = messages.map((m) => ({
        role: m.role,
        content: m.content,
      }));

      setMessages((prev) => [...prev, userMsg, assistantMsg]);
      setIsStreaming(true);
      streamingIdRef.current = assistantMsg.id;
      lastUserMessageRef.current = trimmed;

      try {
        await invoke("chat_stream", {
          message: trimmed,
          model: MODEL_MAP[modelMode],
          history,
          memoryEnabled,
          knowledgeEnabled: knowledgeEnabledRef.current,
          embedModel: embedModelRef.current,
        });
      } catch (e) {
        const id = streamingIdRef.current;
        streamingIdRef.current = null;
        setIsStreaming(false);
        if (id) {
          setMessages((prev) =>
            prev.map((m) =>
              m.id === id
                ? { ...m, content: String(e), error: true }
                : m
            )
          );
        }
      }
    },
    [isStreaming, messages, modelMode, memoryEnabled, knowledgeEnabled, embedModel]
  );

  const stopStreaming = useCallback(() => {
    streamingIdRef.current = null;
    setIsStreaming(false);
  }, []);

  return { messages, isStreaming, sendMessage, stopStreaming };
}
