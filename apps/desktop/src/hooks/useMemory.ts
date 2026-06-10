// Memory state management.
//
// Loads all memories on mount, then stays in sync via the "memories-updated"
// Tauri event that fires whenever extraction or deletion changes the list.

import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useCallback, useEffect, useState } from "react";

export interface Memory {
  id: string;
  content: string;
  created_at: string;
}

export interface MemoryLink {
  id: string;
  from_id: string;
  to_id: string;
  relation: string;
  created_at: string;
  from_content: string;
  to_content: string;
}

interface MemoriesUpdatedPayload {
  memories: Memory[];
}

export function useMemory() {
  const [memories, setMemories] = useState<Memory[]>([]);
  const [links, setLinks] = useState<MemoryLink[]>([]);

  const refreshLinks = useCallback(async () => {
    try {
      const result = await invoke<MemoryLink[]>("list_memory_links");
      setLinks(result);
    } catch {
      // sidecar may not be ready - silently skip
    }
  }, []);

  // Load initial list and listen for updates
  useEffect(() => {
    let cancelled = false;
    let unlisten: (() => void) | undefined;

    // Fetch current list from DB on mount
    invoke<Memory[]>("list_memories")
      .then((list) => {
        if (!cancelled) setMemories(list);
      })
      .catch(() => {}); // sidecar may not be ready yet - silently skip

    refreshLinks();

    listen<MemoriesUpdatedPayload>("memories-updated", (event) => {
      setMemories(event.payload.memories);
      refreshLinks();
    }).then((fn) => {
      if (cancelled) fn();
      else unlisten = fn;
    });

    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, [refreshLinks]);

  const deleteMemory = useCallback(async (id: string) => {
    try {
      await invoke("delete_memory", { memoryId: id });
    } catch {
      // swallow - UI will sync on next memories-updated event
    }
  }, []);

  const deleteLink = useCallback(async (linkId: string) => {
    try {
      await invoke("delete_memory_link", { linkId });
      setLinks((prev) => prev.filter((l) => l.id !== linkId));
    } catch {
      // swallow
    }
  }, []);

  const createLink = useCallback(
    async (fromId: string, toId: string, relation: string) => {
      try {
        const link = await invoke<MemoryLink>("create_memory_link", {
          fromId,
          toId,
          relation,
        });
        setLinks((prev) => [link, ...prev]);
        return link;
      } catch {
        return null;
      }
    },
    [],
  );

  return { memories, links, deleteMemory, deleteLink, createLink };
}
