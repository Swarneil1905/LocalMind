// Memory state management.
//
// Loads all memories on mount, then stays in sync via the "memories-updated"
// Tauri event that fires whenever extraction or deletion changes the list.

import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useCallback, useEffect, useRef, useState } from "react";

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
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const generation = useRef(0);
  const memoryRevision = useRef(0);
  const linkRevision = useRef(0);
  const refreshLinks = useCallback(async () => {
    const epoch = generation.current;
    const version = ++linkRevision.current;
    try {
      const result = await invoke<MemoryLink[]>("list_memory_links");
      if (epoch === generation.current && version === linkRevision.current)
        setLinks(Array.isArray(result) ? result : []);
    } catch {
      if (epoch === generation.current && version === linkRevision.current)
        setError("Couldn't load memory links. Try refreshing.");
    }
  }, []);
  const refresh = useCallback(async () => {
    const epoch = generation.current;
    const version = ++memoryRevision.current;
    setLoading(true);
    setError(null);
    const linksRequest = refreshLinks();
    try {
      const list = await invoke<Memory[]>("list_memories");
      if (epoch === generation.current && version === memoryRevision.current)
        setMemories(Array.isArray(list) ? list : []);
    } catch {
      if (epoch === generation.current && version === memoryRevision.current)
        setError(
          "Couldn't load memories. Try refreshing when the local service is ready.",
        );
    }
    await linksRequest;
    if (epoch === generation.current && version === memoryRevision.current)
      setLoading(false);
  }, [refreshLinks]);
  useEffect(() => {
    let cancelled = false;
    const epoch = ++generation.current;
    const offs: (() => void)[] = [];
    const keep = (off: () => void) => {
      if (cancelled) off();
      else offs.push(off);
    };
    // Install before fetching so readiness cannot fall between the initial read and subscription.
    void Promise.allSettled([
      listen("sidecar-ready", () => {
        if (!cancelled) void refresh();
      }).then(keep),
      listen<MemoriesUpdatedPayload>("memories-updated", (event) => {
        if (cancelled) return;
        memoryRevision.current++;
        setMemories(
          Array.isArray(event.payload?.memories) ? event.payload.memories : [],
        );
        setError(null);
        setLoading(false);
        void refreshLinks();
      }).then(keep),
    ]).then(() => {
      if (!cancelled) void refresh();
    });
    return () => {
      cancelled = true;
      generation.current = epoch + 1;
      offs.forEach((off) => off());
    };
  }, [refresh, refreshLinks]);

  const deleteMemory = useCallback(async (id: string) => {
    const epoch = generation.current;
    try {
      await invoke("delete_memory", { memoryId: id });
      if (epoch !== generation.current) return;
      memoryRevision.current++;
      linkRevision.current++;
      setMemories((prev) => prev.filter((m) => m.id !== id));
      setLinks((prev) =>
        prev.filter((link) => link.from_id !== id && link.to_id !== id),
      );
      setError(null);
      setLoading(false);
    } catch (cause) {
      if (epoch === generation.current)
        setError("Couldn't delete this memory. Try again.");
      throw cause;
    }
  }, []);

  const deleteLink = useCallback(async (linkId: string) => {
    const epoch = generation.current;
    try {
      await invoke("delete_memory_link", { linkId });
      if (epoch !== generation.current) return;
      linkRevision.current++;
      setLinks((prev) => prev.filter((l) => l.id !== linkId));
      setError(null);
    } catch (cause) {
      if (epoch === generation.current)
        setError("Couldn't remove this link. Try again.");
      throw cause;
    }
  }, []);

  const createLink = useCallback(
    async (fromId: string, toId: string, relation: string) => {
      const epoch = generation.current;
      try {
        const link = await invoke<MemoryLink>("create_memory_link", {
          fromId,
          toId,
          relation,
        });
        if (epoch === generation.current) {
          linkRevision.current++;
          setLinks((prev) => [link, ...prev.filter((l) => l.id !== link.id)]);
          setError(null);
        }
        return link;
      } catch (cause) {
        if (epoch === generation.current)
          setError("Couldn't create this link. Try again.");
        throw cause;
      }
    },
    [],
  );

  return {
    memories,
    links,
    error,
    loading,
    refresh,
    deleteMemory,
    deleteLink,
    createLink,
  };
}
