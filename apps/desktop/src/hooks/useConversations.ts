// Conversation persistence hook.
//
// Manages the list of saved conversations and the currently active one.
// The UI calls these to create/switch/delete conversations, and useChat
// calls saveCurrentTurn() after each assistant reply completes.

import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useCallback, useEffect, useState } from "react";

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
  created_at: string;
}

interface ConversationsUpdatedPayload {
  conversations: Conversation[];
}

export function useConversations() {
  const [conversations, setConversations] = useState<Conversation[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);

  // Load list on mount and subscribe to server-push updates (e.g. after delete)
  useEffect(() => {
    invoke<Conversation[]>("list_conversations")
      .then(setConversations)
      .catch(() => {});

    let unlisten: (() => void) | undefined;
    listen<ConversationsUpdatedPayload>("conversations-updated", (event) => {
      setConversations(event.payload.conversations);
    }).then((fn) => {
      unlisten = fn;
    });

    return () => unlisten?.();
  }, []);

  // Create a new conversation, set it as active, return it so the caller
  // can reset the chat messages.
  const createConversation = useCallback(async (title: string): Promise<Conversation> => {
    const conv = await invoke<Conversation>("create_conversation", { title });
    setConversations((prev) => [conv, ...prev]);
    setActiveId(conv.id);
    return conv;
  }, []);

  // Switch to an existing conversation; returns its messages so the caller
  // can hydrate the chat message list.
  const selectConversation = useCallback(
    async (id: string): Promise<ConversationMessage[]> => {
      setActiveId(id);
      const messages = await invoke<ConversationMessage[]>(
        "get_conversation_messages",
        { conversationId: id }
      );
      return messages;
    },
    []
  );

  // Persist the just-completed user+assistant turn.
  const saveCurrentTurn = useCallback(
    async (
      userContent: string,
      assistantContent: string,
      assistantThinking: string | null
    ): Promise<void> => {
      if (!activeId) return;
      await invoke("save_conversation_turn", {
        conversationId: activeId,
        userContent,
        assistantContent,
        assistantThinking,
      }).catch(() => {}); // non-fatal
    },
    [activeId]
  );

  const deleteConversation = useCallback(
    async (id: string): Promise<void> => {
      await invoke("delete_conversation", { conversationId: id });
      // conversations-updated event will refresh the list
      if (activeId === id) {
        setActiveId(null);
      }
    },
    [activeId]
  );

  const renameConversation = useCallback(
    async (id: string, title: string): Promise<void> => {
      await invoke("rename_conversation", { conversationId: id, title });
      setConversations((prev) =>
        prev.map((c) => (c.id === id ? { ...c, title } : c))
      );
    },
    []
  );

  return {
    conversations,
    activeId,
    setActiveId,
    createConversation,
    selectConversation,
    saveCurrentTurn,
    deleteConversation,
    renameConversation,
  };
}
