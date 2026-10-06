// Conversation persistence hook.
//
// Manages the list of saved conversations and the currently active one.
// The UI calls these to create/switch/delete conversations, and useChat
// calls saveCurrentTurn() after each assistant reply completes.

import type { WebSource } from "../types";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useCallback, useEffect, useRef, useState } from "react";

export interface Conversation {
  id: string;
  title: string;
  created_at: string;
  updated_at: string;
}

export interface ConversationMessage {
  id: string;
  conversation_id: string;
  role: "user" | "assistant";
  content: string;
  thinking: string | null;
  sources?: WebSource[];
  created_at: string;
}

interface ConversationsUpdatedPayload {
  conversations: Conversation[];
}

export function useConversations() {
  const [conversations, setConversations] = useState<Conversation[]>([]);
  const selectionVersion = useRef(0);
  const [activeId, setActiveId] = useState<string | null>(null);

  // Load list on mount and subscribe to server-push updates (e.g. after delete).
  // Guard against a non-array response (e.g. mocked invoke() in tests, or a
  // sidecar hiccup) so a bad payload can't null out state and crash the render.
  useEffect(() => {
    let disposed = false;
    const refresh = () =>
      void invoke<Conversation[]>("list_conversations")
        .then((list) => {
          if (!disposed) setConversations(Array.isArray(list) ? list : []);
        })
        .catch(() => {});
    refresh();
    const offs: (() => void)[] = [];
    const keep = (off: () => void) => {
      if (disposed) off();
      else offs.push(off);
    };
    void listen("sidecar-ready", refresh).then(keep);
    void listen<ConversationsUpdatedPayload>(
      "conversations-updated",
      (event) => {
        if (!disposed)
          setConversations(
            Array.isArray(event.payload?.conversations)
              ? event.payload.conversations
              : [],
          );
      },
    ).then(keep);
    // Covers a readiness event emitted just before listeners were installed.
    const retry = setTimeout(refresh, 2500);
    return () => {
      disposed = true;
      clearTimeout(retry);
      offs.forEach((off) => off());
    };
  }, []);

  // Create a new conversation, set it as active, return it so the caller
  // can reset the chat messages.
  const createConversation = useCallback(
    async (title: string): Promise<Conversation> => {
      const version = ++selectionVersion.current;
      const conv = await invoke<Conversation>("create_conversation", { title });
      setConversations((prev) => [conv, ...prev]);
      if (version === selectionVersion.current) setActiveId(conv.id);
      return conv;
    },
    [],
  );

  // Switch to an existing conversation; returns its messages so the caller
  // can hydrate the chat message list.
  const selectConversation = useCallback(
    async (id: string): Promise<ConversationMessage[]> => {
      selectionVersion.current++;
      setActiveId(id);
      const messages = await invoke<ConversationMessage[]>(
        "get_conversation_messages",
        { conversationId: id },
      );
      // Guard against a non-array response (e.g. mocked invoke() in tests, or a
      // sidecar hiccup) - the caller immediately calls .map() on this result.
      return Array.isArray(messages) ? messages : [];
    },
    [],
  );

  // Persist the just-completed user+assistant turn.
  const saveCurrentTurn = useCallback(
    async (
      userContent: string,
      assistantContent: string,
      assistantThinking: string | null,
      conversationId: string | null = activeId,
      sources: WebSource[] = [],
    ): Promise<void> => {
      if (!conversationId) return;
      await invoke("save_conversation_turn", {
        conversationId,
        userContent,
        assistantContent,
        assistantThinking,
        sources,
      });
    },
    [activeId],
  );

  const deleteConversation = useCallback(async (id: string): Promise<void> => {
    await invoke("delete_conversation", { conversationId: id });
    // conversations-updated event will refresh the list
    setActiveId((current) => (current === id ? null : current));
    setConversations((current) => current.filter((c) => c.id !== id));
  }, []);

  const renameConversation = useCallback(
    async (id: string, title: string): Promise<void> => {
      await invoke("rename_conversation", { conversationId: id, title });
      setConversations((prev) =>
        prev.map((c) => (c.id === id ? { ...c, title } : c)),
      );
    },
    [],
  );

  const clearSelection = useCallback(() => {
    selectionVersion.current++;
    setActiveId(null);
  }, []);

  return {
    conversations,
    activeId,
    setActiveId,
    clearSelection,
    createConversation,
    selectConversation,
    saveCurrentTurn,
    deleteConversation,
    renameConversation,
  };
}
