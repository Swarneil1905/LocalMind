import { useRef, useState } from "react";
import {
  FolderOpen,
  Search,
  Trash2,
  FileText,
  LoaderCircle,
} from "lucide-react";
import { useKnowledge, type KnowledgeChunk } from "../hooks/useKnowledge";
import "./supporting-pages.css";

export function KnowledgePage({ embedModel }: { embedModel: string }) {
  const {
    sources,
    loading,
    refreshing,
    refresh,
    error,
    addFolder,
    deleteSource,
    search,
  } = useKnowledge();
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<KnowledgeChunk[]>([]);
  const [searching, setSearching] = useState(false);
  const [submittedQuery, setSubmittedQuery] = useState("");
  const searchVersion = useRef(0);
  const readySources = sources.filter((source) => source.status === "ready");
  async function handleSearch(event: React.FormEvent) {
    event.preventDefault();
    const term = query.trim();
    if (!term || searching) return;
    const version = ++searchVersion.current;
    setSearching(true);
    try {
      const chunks = await search(term, embedModel);
      if (version === searchVersion.current) {
        setResults(chunks);
        setSubmittedQuery(term);
      }
    } catch {
      /* The knowledge hook exposes the error to the page. */
    } finally {
      if (version === searchVersion.current) setSearching(false);
    }
  }
  return (
    <div className="workspace-page">
      <div className="workspace-page-inner">
        <header className="page-heading">
          <div>
            <h1>Knowledge</h1>
            <p>Bring your own files into the conversation.</p>
          </div>
          <button
            className="page-button primary"
            onClick={() => void addFolder(embedModel).catch(() => {})}
            disabled={loading}
          >
            <FolderOpen size={15} />
            {loading ? "Adding folder…" : "Add folder"}
          </button>
        </header>
        {error && (
          <div className="page-notice error" role="alert">
            <span>{error}</span>
            <button className="page-button" onClick={() => void refresh()}>
              Refresh
            </button>
          </div>
        )}
        <section className="page-section" aria-labelledby="knowledge-sources">
          <div className="section-heading">
            <h2 id="knowledge-sources">Folders</h2>
            <span className="muted">{sources.length} added</span>
          </div>
          {refreshing && !sources.length ? (
            <div className="page-empty-state" role="status">
              <LoaderCircle size={24} className="spin" />
              <p>Loading folders…</p>
            </div>
          ) : error && !sources.length ? null : !sources.length ? (
            <div className="page-empty-state">
              <div className="empty-icon">
                <FolderOpen size={26} strokeWidth={1.5} />
              </div>
              <h2>Your files, within reach</h2>
              <p>
                Add a folder to search its contents and reference it in chat.
                Your original files stay where they are.
              </p>
              <button
                className="page-button"
                onClick={() => void addFolder(embedModel).catch(() => {})}
                disabled={loading}
              >
                <FolderOpen size={15} />
                Choose a folder
              </button>
            </div>
          ) : (
            <div className="record-list">
              {sources.map((source) => (
                <article className="source-record" key={source.id}>
                  <FolderOpen size={20} strokeWidth={1.5} />
                  <div className="record-copy">
                    <h3>{source.name}</h3>
                    <p className="record-path" title={source.path}>
                      {source.path}
                    </p>
                    <span
                      className={
                        source.status === "error" ? "error-text" : "muted"
                      }
                    >
                      {source.status === "indexing"
                        ? "Indexing files…"
                        : source.status === "error"
                          ? "Indexing failed. Remove the source and add it again to retry."
                          : `${source.file_count} files · ${source.chunk_count} indexed passages`}
                    </span>
                  </div>
                  {source.status === "indexing" && (
                    <LoaderCircle size={16} className="spin" />
                  )}
                  <button
                    className="page-icon-button"
                    aria-label={`Remove ${source.name} from Knowledge`}
                    disabled={source.status === "indexing"}
                    onClick={() => {
                      if (
                        window.confirm(
                          `Remove “${source.name}” from Knowledge? Your original files won’t be deleted.`,
                        )
                      )
                        void deleteSource(source.id).catch(() => {});
                    }}
                  >
                    <Trash2 size={15} />
                  </button>
                </article>
              ))}
            </div>
          )}
        </section>
        {readySources.length > 0 && (
          <section className="page-section" aria-labelledby="knowledge-search">
            <div className="section-heading">
              <h2 id="knowledge-search">Search your files</h2>
              <span className="muted">Search by meaning</span>
            </div>
            <form className="page-form-row" onSubmit={handleSearch}>
              <label className="page-search grow">
                <Search size={16} />
                <input
                  aria-label="Search your files"
                  value={query}
                  onChange={(event) => setQuery(event.target.value)}
                  placeholder="What would you like to find?"
                />
              </label>
              <button
                className="page-button primary"
                disabled={searching || !query.trim()}
              >
                {searching && <LoaderCircle size={14} className="spin" />}
                {searching ? "Searching…" : "Search"}
              </button>
            </form>
            <div aria-live="polite">
              {submittedQuery && !searching && !error && (
                <p className="result-caption">
                  {results.length
                    ? `${results.length} matching passages`
                    : "No matching passages"}{" "}
                  for “{submittedQuery}”
                </p>
              )}
              <div className="search-results">
                {results.map((chunk) => (
                  <article className="search-result" key={chunk.id}>
                    <div className="compact-row">
                      <FileText size={15} />
                      <strong title={chunk.file_path}>
                        {chunk.file_path.split(/[\\/]/).pop()}
                      </strong>
                    </div>
                    <p>{chunk.content}</p>
                  </article>
                ))}
              </div>
            </div>
          </section>
        )}
        <p className="page-footnote">
          Enable Knowledge in chat to include relevant passages from your
          indexed folders.
        </p>
      </div>
    </div>
  );
}
