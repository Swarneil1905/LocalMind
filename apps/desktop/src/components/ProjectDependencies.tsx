import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import type { Project, Task } from "../hooks/useProjects";
import "./project-dependencies.css";
import { RepairReview, type RepairOperation } from "./RepairReview";

interface Source {
  id: string; path: string; sha256: string; content: string;
  state: string; notice: string | null; checked_at: string;
}
interface Link {
  id: string; source_id: string; source_path: string; fact: string; reason: string;
  target_kind: "file" | "task"; target_id: string; target_preview: string;
  state: "proposed" | "confirmed" | "rejected"; target_state: string; source_state: string;
  needs_review: boolean; impact_reason: string | null; target_notice: string | null;
}
interface Graph { root: { path: string } | null; sources: Source[]; links: Link[]; tasks: Task[]; repairs?: RepairOperation[] }

export function ProjectDependencies({ project }: { project: Project }) {
  const [graph, setGraph] = useState<Graph | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [path, setPath] = useState("");
  const [sourceId, setSourceId] = useState("");
  const [fact, setFact] = useState("");
  const [reason, setReason] = useState("");
  const [kind, setKind] = useState<"file" | "task">("file");
  const [target, setTarget] = useState("");
  const epoch = useRef(0);
  const request = useCallback((operation: string, payload?: object) => invoke<Graph>("project_graph", { projectId: project.id, operation, payload: payload ?? null }), [project.id]);
  const refresh = useCallback(async () => {
    const version = ++epoch.current;
    try { const value = await request("get"); if (version === epoch.current) { setGraph(value); setError(""); } }
    catch (cause) { if (version === epoch.current) setError(String(cause)); }
  }, [request]);
  useEffect(() => {
    const version = ++epoch.current;
    let active = true;
    request("get").then(value => { if (active && version === epoch.current) { setGraph(value); setError(""); } })
      .catch(cause => { if (active && version === epoch.current) setError(String(cause)); });
    return () => { active = false; epoch.current = version + 1; };
  }, [request]);
  async function run(operation: string, payload?: object) {
    if (busy) return false;
    setBusy(true); setError("");
    const version = epoch.current;
    try { await request(operation, payload); if (version === epoch.current) await refresh(); return true; }
    catch (cause) { if (version === epoch.current) setError(String(cause)); return false; }
    finally { setBusy(false); }
  }
  const selected = graph?.sources.find(source => source.id === sourceId);
  const impacts = graph?.links.filter(link => link.needs_review) ?? [];
  return <section className="project-dependencies" aria-label="Project dependencies">
    <div className="section-heading"><h3>Sources and dependencies</h3><button className="page-button" disabled={busy} onClick={() => void refresh()}>Refresh</button></div>
    <p>Track an exact source quote and the files or tasks that depend on it. File changes flag items for review; LocalMind does not rewrite them.</p>
    {error && <p role="alert" className="error-text">{error}</p>}
    {!graph ? <p role="status">{error ? "Could not load dependencies. Use Refresh to try again." : "Loading dependencies…"}</p> : <>
      {!graph.root ? <div className="dependency-card">
        <h4>Approve a working folder</h4>
        <p className="record-path">{project.path || "Set a working folder in Overview first."}</p>
        <p>Only the text files you register below will be read. Supported: UTF-8 Markdown and text, up to 256 KiB each. Linked paths are excluded.</p>
        <button className="page-button primary" disabled={busy || !project.path} onClick={() => void run("approve")}>Approve folder for dependency tracking</button>
      </div> : <>
        <div className="dependency-card">
          <div className="section-heading"><h4>Approved folder</h4><button className="page-button" disabled={busy} onClick={() => void run("rescan")}>{busy ? "Working…" : "Rescan tracked files"}</button></div>
          <p className="record-path">{graph.root.path}</p>
          <p className="page-footnote">Results reflect the last scan. Only registered sources and dependency targets are checked.</p>
        </div>
        <form className="page-form-row" onSubmit={async event => { event.preventDefault(); if (await run("sources", { path })) setPath(""); }}>
          <label className="dependency-field grow">Source file, relative to this folder<input className="page-input" value={path} onChange={event => setPath(event.target.value)} placeholder="notes/release.md" required maxLength={1000} /></label>
          <button className="page-button" disabled={busy || !path.trim()}>Capture source</button>
        </form>
        {graph.sources.map(source => <details className="dependency-card" key={source.id}>
          <summary>{source.path} <span className="dependency-state">{source.state}</span></summary>
          <p>Checked {new Date(source.checked_at).toLocaleString()}</p>
          {source.notice && <p>{source.notice}</p>}
          <p className="page-footnote">Captured revision {source.sha256.slice(0, 12)}. Select an exact quote from this snapshot.</p>
          <pre tabIndex={0} aria-label={`Captured source ${source.path}`}>{source.content}</pre>
        </details>)}
        {graph.sources.length > 0 && <form className="dependency-form" onSubmit={async event => {
          event.preventDefault();
          if (await run("links", { source_id: sourceId, fact, reason, target_kind: kind, target_id: target })) { setFact(""); setReason(""); setTarget(""); }
        }}>
          <h4>Propose a dependency</h4>
          <label className="dependency-field">Supporting source<select className="page-input" value={sourceId} onChange={event => { setSourceId(event.target.value); setFact(""); }} required><option value="">Choose a source</option>{graph.sources.map(source => <option key={source.id} value={source.id}>{source.path}</option>)}</select></label>
          {selected && <details><summary>Read captured source</summary><pre tabIndex={0}>{selected.content}</pre></details>}
          <label className="dependency-field">Exact source quote<textarea className="page-input" value={fact} onChange={event => setFact(event.target.value)} required maxLength={2000} rows={3} /></label>
          <label className="dependency-field">Why this item depends on the quote<input className="page-input" value={reason} onChange={event => setReason(event.target.value)} required maxLength={1000} /></label>
          <label className="dependency-field">Dependent item type<select className="page-input" value={kind} onChange={event => { setKind(event.target.value as "file" | "task"); setTarget(""); }}><option value="file">Text file</option><option value="task">Project task</option></select></label>
          {kind === "file" ? <label className="dependency-field">Dependent file, relative path<input className="page-input" value={target} onChange={event => setTarget(event.target.value)} placeholder="drafts/announcement.md" required maxLength={1000} /></label> : <label className="dependency-field">Dependent task<select className="page-input" value={target} onChange={event => setTarget(event.target.value)} required><option value="">Choose a task</option>{graph.tasks.map(task => <option key={task.id} value={task.id}>{task.title}</option>)}</select></label>}
          <button className="page-button primary" disabled={busy || !selected || !fact.trim() || !reason.trim() || !target.trim()}>Review dependency</button>
        </form>}
        <h4>{impacts.length} {impacts.length === 1 ? "item needs" : "items need"} review</h4>
        <p className="page-footnote">A source revision change is a signal to check the quote. It does not prove the meaning changed.</p>
        {graph.links.length === 0 && <p>No dependencies yet. Capture a source and propose its first link.</p>}
        {[...graph.links].sort((a, b) => Number(b.needs_review) - Number(a.needs_review)).map(link => <article className={`dependency-card ${link.needs_review ? "needs-review" : ""}`} key={link.id} aria-label={`Dependency from ${link.source_path}`}>
          <div className="section-heading"><strong>{link.target_kind === "file" ? link.target_id : link.target_preview}</strong><span className="dependency-state">{link.needs_review ? "Needs review" : link.state}</span></div>
          <p>Source: <strong>{link.source_path}</strong> · {link.source_state}</p>
          <blockquote>{link.fact}</blockquote><p>{link.reason}</p>
          {link.impact_reason && <p role="status">{link.impact_reason}</p>}
          <p>Dependent item: {link.target_state}</p>{link.target_notice && <p>{link.target_notice}</p>}
          <details><summary>Inspect captured dependent item</summary><pre tabIndex={0}>{link.target_preview}</pre><small>{link.target_id}</small></details>
          {link.state === "proposed" && <div className="dependency-actions"><button className="page-button primary" disabled={busy} onClick={() => void run("review", { link_id: link.id, decision: "confirm" })}>Confirm dependency</button><button className="page-button" disabled={busy} onClick={() => void run("review", { link_id: link.id, decision: "reject" })}>Reject</button></div>}
        </article>)}
        <RepairReview sources={graph.sources} repairs={graph.repairs ?? []} busy={busy} run={run} />
      </>}
    </>}
  </section>;
}
