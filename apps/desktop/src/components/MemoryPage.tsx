import { invoke } from "@tauri-apps/api/core";
import { Brain, Link, LoaderCircle, Search, Trash2, X } from "lucide-react";
import { useState } from "react";
import { Memory, MemoryLink } from "../hooks/useMemory";
import "./supporting-pages.css";

interface MemoryPageProps {
  memories: Memory[];
  links: MemoryLink[];
  onDelete: (id: string) => void;
  onDeleteLink: (linkId: string) => void;
  loading?: boolean;
  loadError?: string | null;
  onRefresh?: () => void;
}
export function MemoryPage({
  memories,
  links,
  onDelete,
  onDeleteLink,
  loading = false,
  loadError,
  onRefresh,
}: MemoryPageProps) {
  const [clearing, setClearing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [busy, setBusy] = useState(false);
  const visible = memories.filter((memory) =>
    memory.content.toLowerCase().includes(query.toLowerCase()),
  );
  const linksByMemory = new Map<string, MemoryLink[]>();
  for (const link of links) {
    for (const id of [link.from_id, link.to_id])
      linksByMemory.set(id, [...(linksByMemory.get(id) ?? []), link]);
  }
  async function clearAll() {
    if (
      !window.confirm(
        `Delete all ${memories.length} memories? This cannot be undone.`,
      )
    )
      return;
    setClearing(true);
    setError(null);
    try {
      await invoke("delete_all_memories");
    } catch {
      setError("Couldn’t clear memories. Please try again.");
    } finally {
      setClearing(false);
    }
  }
  async function remove(operation: () => void) {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await operation();
    } catch {
      setError("Couldn’t remove this saved context. Please try again.");
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className="workspace-page">
      <div className="workspace-page-inner">
        <header className="page-heading">
          <div>
            <h1>Memory</h1>
            <p>
              Context saved from your conversations. Review it and keep what
              matters.
            </p>
          </div>
          <button
            className="page-button danger"
            onClick={clearAll}
            disabled={clearing || busy || loading || !memories.length}
          >
            <Trash2 size={14} />
            {clearing ? "Clearing…" : "Clear all"}
          </button>
        </header>
        {error && (
          <p className="page-notice error" role="alert">
            {error}
          </p>
        )}
        {loadError && !error && (
          <div className="page-notice error" role="alert">
            <span>{loadError}</span>
            {onRefresh && (
              <button className="page-button" onClick={onRefresh}>
                Retry
              </button>
            )}
          </div>
        )}
        <div className="section-heading">
          <h2>
            {memories.length} saved{" "}
            {memories.length === 1 ? "memory" : "memories"}
          </h2>
          {memories.length > 0 && (
            <label className="page-search">
              <Search size={15} />
              <input
                aria-label="Search memories"
                placeholder="Search memories"
                value={query}
                onChange={(event) => setQuery(event.target.value)}
              />
            </label>
          )}
        </div>
        {loading ? (
          <div className="page-empty-state" role="status">
            <LoaderCircle size={24} className="spin" />
            <p>Loading memories…</p>
          </div>
        ) : loadError && !memories.length ? null : memories.length === 0 ? (
          <section className="page-empty-state">
            <div className="empty-icon">
              <Brain size={25} strokeWidth={1.5} />
            </div>
            <h2>A little context goes a long way</h2>
            <p>
              Turn on Memory in a conversation to save useful facts. Your saved
              memories will appear here for review.
            </p>
          </section>
        ) : visible.length === 0 ? (
          <div className="page-empty-state">
            <Search size={24} />
            <h2>No matching memories</h2>
            <p>Try a different word or clear your search.</p>
          </div>
        ) : (
          <div className="record-list">
            {visible.map((memory) => (
              <article className="memory-record" key={memory.id}>
                <div className="record-main">
                  <div className="record-copy">
                    <p>{memory.content}</p>
                    <time dateTime={memory.created_at}>
                      {new Date(memory.created_at).toLocaleDateString(
                        undefined,
                        { month: "short", day: "numeric", year: "numeric" },
                      )}
                    </time>
                  </div>
                  <button
                    className="page-icon-button"
                    disabled={busy || clearing}
                    aria-label={`Delete memory: ${memory.content}`}
                    onClick={() => void remove(() => onDelete(memory.id))}
                  >
                    <Trash2 size={15} />
                  </button>
                </div>
                {(linksByMemory.get(memory.id) ?? []).map((link) => (
                  <div className="memory-link" key={link.id}>
                    <Link size={13} />
                    <span>{link.relation.replace(/_/g, " ")}</span>
                    <p>
                      {link.from_id === memory.id
                        ? link.to_content
                        : link.from_content}
                    </p>
                    <button
                      className="page-icon-button"
                      disabled={busy || clearing}
                      aria-label="Remove memory link"
                      onClick={() => void remove(() => onDeleteLink(link.id))}
                    >
                      <X size={13} />
                    </button>
                  </div>
                ))}
              </article>
            ))}
          </div>
        )}
        <p className="page-footnote">
          Memories are stored on this device. Removing one also removes its
          links to other memories.
        </p>
      </div>
    </div>
  );
}
