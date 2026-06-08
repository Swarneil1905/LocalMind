// Chat state management and streaming via Tauri events.
//
// Flow:
//   1. sendMessage() adds user + empty assistant messages, calls chat_stream command.
//   2. Rust emits "chat-token" events; we append each token to the assistant message.
//   3. Rust emits "chat-done" when streaming ends (or errors).
//   4. stopStreaming() resets streaming state; in-flight tokens are ignored.

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

export function useChat(modelMode: ModelMode) {
  const [messages, setMessages] = useState<Message[]>([]);
  const [isStreaming, setIsStreaming] = useState(false);

  // We track the ID of the assistant message being streamed so that concurrent
  // state updates from different event callbacks all target the right message.
  const streamingIdRef = useRef<string | null>(null);

  // Set up persistent Tauri event listeners. These live for the lifetime of
  // the hook (i.e. the app), not per-message, so they don't miss early tokens.
  useEffect(() => {
    let unlistenToken: (() => void) | undefined;
    let unlistenDone: (() => void) | undefined;

    listen<ChatTokenPayload>("chat-token", (event) => {
      const id = streamingIdRef.current;
      if (!id) return; // stopStreaming() was called; ignore this token
      setMessages((prev) =>
        prev.map((m) =>
          m.id === id ? { ...m, content: m.content + event.payload.content } : m
        )
      );
    }).then((fn) => {
      unlistenToken = fn;
    });

    listen<ChatDonePayload>("chat-done", (event) => {
      const id = streamingIdRef.current;
      streamingIdRef.current = null;
      setIsStreaming(false);

      if (event.payload.error && id) {
        setMessages((prev) =>
          prev.map((m) =>
            m.id === id
              ? { ...m, content: event.payload.error!, error: true }
              : m
          )
        );
      }
    }).then((fn) => {
      unlistenDone = fn;
    });

    return () => {
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

      // Snapshot history before adding the new messages
      const history = messages.map((m) => ({
        role: m.role,
        content: m.content,
      }));

      setMessages((prev) => [...prev, userMsg, assistantMsg]);
      setIsStreaming(true);
      streamingIdRef.current = assistantMsg.id;

      try {
        await invoke("chat_stream", {
          message: trimmed,
          model: MODEL_MAP[modelMode],
          history,
        });
      } catch (e) {
        // invoke() only rejects on Rust-side errors (not sidecar stream errors,
        // those come through chat-done). Handle the rare Rust error here.
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
    [isStreaming, messages, modelMode]
  );

  // UI-level stop: stop updating state with new tokens.
  // The in-flight HTTP request will complete in the background but tokens
  // will be ignored because streamingIdRef is cleared.
  const stopStreaming = useCallback(() => {
    streamingIdRef.current = null;
    setIsStreaming(false);
  }, []);

  return { messages, isStreaming, sendMessage, stopStreaming };
}
