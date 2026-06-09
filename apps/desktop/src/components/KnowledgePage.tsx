import { useState } from "react";
import { FolderOpen, Search, Trash2, FileText, Loader } from "lucide-react";
import { useKnowledge, type KnowledgeSource, type KnowledgeChunk } from "../hooks/useKnowledge";

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

interface SourceRowProps {
  source: KnowledgeSource;
  onDelete: (id: string) => void;
}

function SourceRow({ source, onDelete }: SourceRowProps) {
  const isIndexing = source.status === "indexing";
  const isError = source.status === "error";

  return (
    <div
      style={{
        display: "flex",
        alignItems: "center",
        gap: 10,
        padding: "10px 14px",
        borderRadius: 8,
        background: "var(--surface-2)",
        border: "1px solid var(--border)",
      }}
    >
      <FolderOpen
        size={16}
        strokeWidth={1.5}
        style={{ color: "var(--text-3)", flexShrink: 0 }}
      />
      <div style={{ flex: 1, minWidth: 0 }}>
        <p
          style={{
            fontSize: 13,
            fontWeight: 500,
            color: "var(--text-1)",
            overflow: "hidden",
            textOverflow: "ellipsis",
            whiteSpace: "nowrap",
          }}
        >
          {source.name}
        </p>
        <p
          style={{
            fontSize: 11,
            color: isError ? "var(--accent)" : "var(--text-3)",
            marginTop: 2,
          }}
        >
          {isIndexing && (
            <span style={{ display: "inline-flex", alignItems: "center", gap: 4 }}>
              <Loader size={10} style={{ animation: "spin 1s linear infinite" }} />
              Indexing...
            </span>
          )}
          {isError && "Error during indexing"}
          {!isIndexing && !isError &&
            `${source.file_count} files · ${source.chunk_count} chunks`}
        </p>
      </div>
      <button
        title="Remove source"
        disabled={isIndexing}
        onClick={() => onDelete(source.id)}
        style={{
          background: "none",
          border: "none",
          padding: 4,
          borderRadius: 4,
          cursor: isIndexing ? "not-allowed" : "pointer",
          color: "var(--text-3)",
          flexShrink: 0,
          opacity: isIndexing ? 0.4 : 1,
        }}
      >
        <Trash2 size={14} strokeWidth={1.5} />
      </button>
    </div>
  );
}

interface SearchResultProps {
  chunk: KnowledgeChunk;
}

function SearchResult({ chunk }: SearchResultProps) {
  const filename = chunk.file_path.split(/[\\/]/).pop() ?? chunk.file_path;

  return (
    <div
      style={{
        padding: "10px 14px",
        borderRadius: 8,
        background: "var(--surface-2)",
        border: "1px solid var(--border)",
      }}
    >
      <div
        style={{
          display: "flex",
          alignItems: "center",
          gap: 6,
          marginBottom: 6,
        }}
      >
        <FileText size={12} strokeWidth={1.5} style={{ color: "var(--text-3)" }} />
        <span
          style={{
            fontSize: 11,
            color: "var(--text-3)",
            fontFamily: "var(--font-mono, monospace)",
            overflow: "hidden",
            textOverflow: "ellipsis",
            whiteSpace: "nowrap",
          }}
          title={chunk.file_path}
        >
          {filename}
        </span>
      </div>
      <p
        style={{
          fontSize: 12,
          color: "var(--text-2)",
          lineHeight: 1.6,
          whiteSpace: "pre-wrap",
          wordBreak: "break-word",
          maxHeight: 120,
          overflow: "hidden",
        }}
      >
        {chunk.content}
      </p>
    </div>
  );
}

// ---------------------------------------------------------------------------
// KnowledgePage
// ---------------------------------------------------------------------------

interface KnowledgePageProps {
  embedModel: string;
}

export function KnowledgePage({ embedModel }: KnowledgePageProps) {
  const { sources, loading, error, addFolder, deleteSource, search } =
    useKnowledge();

  const [query, setQuery] = useState("");
  const [results, setResults] = useState<KnowledgeChunk[]>([]);
  const [searching, setSearching] = useState(false);

  const handleSearch = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!query.trim()) return;
    setSearching(true);
    const chunks = await search(query.trim(), embedModel);
    setResults(chunks);
    setSearching(false);
  };

  const readySources = sources.filter((s) => s.status === "ready");

  return (
    <div
      style={{
        flex: 1,
        display: "flex",
        flexDirection: "column",
        overflow: "hidden",
        padding: "24px 28px",
        gap: 24,
      }}
    >
      {/* Header */}
      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
        <div>
          <h2 style={{ fontSize: 15, fontWeight: 600, color: "var(--text-1)" }}>
            Knowledge
          </h2>
          <p style={{ fontSize: 12, color: "var(--text-3)", marginTop: 2 }}>
            Index local files and folders for semantic search and in-chat context.
          </p>
        </div>
        <button
          onClick={() => addFolder(embedModel)}
          disabled={loading}
          style={{
            display: "flex",
            alignItems: "center",
            gap: 6,
            padding: "7px 14px",
            borderRadius: 8,
            background: "var(--accent)",
            color: "#fff",
            border: "none",
            fontSize: 13,
            fontWeight: 500,
            cursor: loading ? "not-allowed" : "pointer",
            opacity: loading ? 0.7 : 1,
          }}
        >
          <FolderOpen size={14} strokeWidth={2} />
          Add folder
        </button>
      </div>

      {/* Error */}
      {error && (
        <p style={{ fontSize: 12, color: "var(--accent)", padding: "6px 10px", background: "var(--surface-2)", borderRadius: 6, border: "1px solid var(--border)" }}>
          {error}
        </p>
      )}

      {/* Indexed sources */}
      <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
        <p style={{ fontSize: 11, fontWeight: 600, color: "var(--text-3)", textTransform: "uppercase", letterSpacing: "0.05em" }}>
          Indexed sources ({sources.length})
        </p>
        {sources.length === 0 ? (
          <p style={{ fontSize: 13, color: "var(--text-3)", padding: "16px 0" }}>
            No sources yet. Add a folder to get started.
          </p>
        ) : (
          <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
            {sources.map((s) => (
              <SourceRow key={s.id} source={s} onDelete={deleteSource} />
            ))}
          </div>
        )}
      </div>

      {/* Search */}
      {readySources.length > 0 && (
        <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
          <p style={{ fontSize: 11, fontWeight: 600, color: "var(--text-3)", textTransform: "uppercase", letterSpacing: "0.05em" }}>
            Semantic search
          </p>
          <form onSubmit={handleSearch} style={{ display: "flex", gap: 8 }}>
            <input
              type="text"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Search your knowledge base..."
              style={{
                flex: 1,
                padding: "8px 12px",
                borderRadius: 8,
                border: "1px solid var(--border)",
                background: "var(--surface-2)",
                color: "var(--text-1)",
                fontSize: 13,
                outline: "none",
              }}
            />
            <button
              type="submit"
              disabled={searching || !query.trim()}
              style={{
                display: "flex",
                alignItems: "center",
                gap: 6,
                padding: "8px 14px",
                borderRadius: 8,
                background: "var(--surface-2)",
                color: "var(--text-2)",
                border: "1px solid var(--border)",
                fontSize: 13,
                cursor: searching || !query.trim() ? "not-allowed" : "pointer",
                opacity: searching || !query.trim() ? 0.6 : 1,
              }}
            >
              {searching ? (
                <Loader size={14} style={{ animation: "spin 1s linear infinite" }} />
              ) : (
                <Search size={14} strokeWidth={1.5} />
              )}
              Search
            </button>
          </form>

          {results.length > 0 && (
            <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
              {results.map((chunk) => (
                <SearchResult key={chunk.id} chunk={chunk} />
              ))}
            </div>
          )}
          {results.length === 0 && query && !searching && (
            <p style={{ fontSize: 13, color: "var(--text-3)" }}>No results found.</p>
          )}
        </div>
      )}
    </div>
  );
}
