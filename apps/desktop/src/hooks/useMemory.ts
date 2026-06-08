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

interface MemoriesUpdatedPayload {
  memories: Memory[];
}

export function useMemory() {
  const [memories, setMemories] = useState<Memory[]>([]);

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

    listen<MemoriesUpdatedPayload>("memories-updated", (event) => {
      setMemories(event.payload.memories);
    }).then((fn) => {
      if (cancelled) fn();
      else unlisten = fn;
    });

    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, []);

  const deleteMemory = useCallback(async (id: string) => {
    try {
      await invoke("delete_memory", { memoryId: id });
    } catch {
      // swallow - UI will sync on next memories-updated event
    }
  }, []);

  return { memories, deleteMemory };
}
