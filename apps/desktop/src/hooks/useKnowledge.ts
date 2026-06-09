import { useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";

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
  addFolder: (embedModel: string) => Promise<void>;
  deleteSource: (sourceId: string) => Promise<void>;
  search: (query: string, embedModel: string, limit?: number) => Promise<KnowledgeChunk[]>;
}

export function useKnowledge(): UseKnowledgeReturn {
  const [sources, setSources] = useState<KnowledgeSource[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const unlistenRef = useRef<UnlistenFn | null>(null);

  // Load sources on mount and subscribe to updates
  useEffect(() => {
    invoke<KnowledgeSource[]>("list_knowledge_sources")
      .then(setSources)
      .catch((e) => setError(String(e)));

    listen<KnowledgeUpdatedPayload>("knowledge-updated", (event) => {
      setSources(event.payload.sources);
    }).then((unlisten) => {
      unlistenRef.current = unlisten;
    });

    return () => {
      unlistenRef.current?.();
    };
  }, []);

  const addFolder = async (embedModel: string) => {
    setError(null);
    const picked = await invoke<string | null>("pick_folder").catch((e) => {
      setError(String(e));
      return null;
    });
    if (!picked) return;

    setLoading(true);
    // Optimistically add an "indexing" entry so the UI updates immediately
    const optimistic: KnowledgeSource = {
      id: "__optimistic__",
      path: picked,
      name: picked.split(/[\\/]/).pop() ?? picked,
      file_count: 0,
      chunk_count: 0,
      status: "indexing",
      created_at: new Date().toISOString(),
    };
    setSources((prev) => [optimistic, ...prev]);

    invoke("index_knowledge", { path: picked, embedModel })
      .catch((e) => setError(String(e)))
      .finally(() => setLoading(false));
    // The "knowledge-updated" event will replace sources (including the optimistic entry)
  };

  const deleteSource = async (sourceId: string) => {
    setError(null);
    invoke("delete_knowledge_source", { sourceId }).catch((e) =>
      setError(String(e))
    );
    // Optimistically remove from list; "knowledge-updated" will confirm
    setSources((prev) => prev.filter((s) => s.id !== sourceId));
  };

  const search = async (
    query: string,
    embedModel: string,
    limit = 5
  ): Promise<KnowledgeChunk[]> => {
    return invoke<KnowledgeChunk[]>("search_knowledge", {
      query,
      embedModel,
      limit,
    }).catch((e) => {
      setError(String(e));
      return [];
    });
  };

  return { sources, loading, error, addFolder, deleteSource, search };
}
