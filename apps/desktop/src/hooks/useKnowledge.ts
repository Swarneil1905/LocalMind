import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

// ---------------------------------------------------------------------------
// Types (must match Rust structs in lib.rs)
// ---------------------------------------------------------------------------

export interface KnowledgeSource {
  id: string;
  path: string;
  name: string;
  file_count: number;
  chunk_count: number;
  status: "indexing" | "ready" | "error";
  created_at: string;
}

export interface KnowledgeChunk {
  id: string;
  source_id: string;
  file_path: string;
  chunk_index: number;
  content: string;
}

interface KnowledgeUpdatedPayload {
  sources: KnowledgeSource[];
}

// ---------------------------------------------------------------------------
// Hook
// ---------------------------------------------------------------------------

interface UseKnowledgeReturn {
  sources: KnowledgeSource[];
  loading: boolean;
  error: string | null;
  refreshing: boolean;
  refresh: () => Promise<void>;
  addFolder: (embedModel: string) => Promise<void>;
  deleteSource: (sourceId: string) => Promise<void>;
  search: (
    query: string,
    embedModel: string,
    limit?: number,
  ) => Promise<KnowledgeChunk[]>;
}

export function useKnowledge(): UseKnowledgeReturn {
  const [sources, setSources] = useState<KnowledgeSource[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(true);
  const generation = useRef(0);
  const revision = useRef(0);
  const adding = useRef(false);
  const searchRevision = useRef(0);
  const refresh = useCallback(async () => {
    const epoch = generation.current;
    const version = ++revision.current;
    setRefreshing(true);
    try {
      const list = await invoke<KnowledgeSource[]>("list_knowledge_sources");
      if (epoch === generation.current && version === revision.current) {
        setSources(Array.isArray(list) ? list : []);
        setError(null);
      }
    } catch {
      if (epoch === generation.current && version === revision.current)
        setError(
          "Couldn't load your folders. Refresh when the local service is ready.",
        );
    } finally {
      if (epoch === generation.current && version === revision.current)
        setRefreshing(false);
    }
  }, []);
  useEffect(() => {
    let cancelled = false;
    const epoch = ++generation.current;
    const offs: (() => void)[] = [];
    const keep = (off: () => void) => {
      if (cancelled) off();
      else offs.push(off);
    };
    void Promise.allSettled([
      listen("sidecar-ready", () => {
        if (!cancelled) void refresh();
      }).then(keep),
      listen<KnowledgeUpdatedPayload>("knowledge-updated", (event) => {
        if (cancelled) return;
        revision.current++;
        setSources(
          Array.isArray(event.payload?.sources) ? event.payload.sources : [],
        );
        setRefreshing(false);
        setError(null);
      }).then(keep),
    ]).then(() => {
      if (!cancelled) void refresh();
    });
    return () => {
      cancelled = true;
      generation.current = epoch + 1;
      offs.forEach((off) => off());
    };
  }, [refresh]);

  const addFolder = async (embedModel: string) => {
    if (adding.current) return;
    adding.current = true;
    const epoch = generation.current;
    setError(null);
    setLoading(true);
    try {
      const picked = await invoke<string | null>("pick_folder");
      if (!picked || epoch !== generation.current) return;
      // Wait for the service to accept indexing, then use its real source IDs/statuses.
      await invoke("index_knowledge", { path: picked, embedModel });
      if (epoch === generation.current) await refresh();
    } catch (cause) {
      if (epoch === generation.current)
        setError(
          "Couldn't add this folder. Check the embedding model and try again.",
        );
      throw cause;
    } finally {
      adding.current = false;
      if (epoch === generation.current) setLoading(false);
    }
  };

  const deleteSource = async (sourceId: string) => {
    const epoch = generation.current;
    setError(null);
    try {
      await invoke("delete_knowledge_source", { sourceId });
      if (epoch === generation.current) {
        revision.current++;
        setSources((prev) => prev.filter((s) => s.id !== sourceId));
        setRefreshing(false);
      }
    } catch (cause) {
      if (epoch === generation.current)
        setError(
          "Couldn't remove this folder. Your indexed source is still available; try again.",
        );
      throw cause;
    }
  };

  const search = async (
    query: string,
    embedModel: string,
    limit = 5,
  ): Promise<KnowledgeChunk[]> => {
    const epoch = generation.current;
    const version = ++searchRevision.current;
    setError(null);
    try {
      const result = await invoke<KnowledgeChunk[]>("search_knowledge", {
        query,
        embedModel,
        limit,
      });
      return Array.isArray(result) ? result : [];
    } catch (cause) {
      if (epoch === generation.current && version === searchRevision.current)
        setError(
          "Couldn't search your files. Check the embedding model and try again.",
        );
      throw cause;
    }
  };

  return {
    sources,
    loading,
    refreshing,
    error,
    refresh,
    addFolder,
    deleteSource,
    search,
  };
}
