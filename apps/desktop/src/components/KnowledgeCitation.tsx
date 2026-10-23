import { useEffect, useRef, useState, type RefObject } from "react";
import { createPortal } from "react-dom";
import { invoke } from "@tauri-apps/api/core";
import type { WebSource } from "../types";
import "./knowledge-citation.css";

interface Passage {
  file_path: string;
  location: string;
  content: string;
  file_state: string;
  notice: string;
}

export function KnowledgeCitation({ source, inline = false }: { source: WebSource; inline?: boolean }) {
  const [open, setOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);
  if (!source.chunk_id) return null;
  return <>
    <button ref={trigger} type="button" className={inline ? "knowledge-citation" : "knowledge-source-button"}
      aria-label={`Open ${source.title}, ${source.location || "indexed passage"}`}
      onClick={() => setOpen(true)}>
      {inline ? `[${source.citation}]` : `${source.citation ? `[${source.citation}] ` : ""}${source.title} · ${source.location || "Indexed passage"}`}
    </button>
    {open && createPortal(<PassageDialog source={source} returnFocus={trigger} onClose={() => setOpen(false)} />, document.body)}
  </>;
}

function PassageDialog({ source, returnFocus, onClose }: { source: WebSource; returnFocus: RefObject<HTMLElement | null>; onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [passage, setPassage] = useState<Passage | null>(null);
  const [error, setError] = useState("");
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    const element = dialog.current;
    const trigger = returnFocus.current;
    element?.showModal();
    return () => { element?.close(); trigger?.focus(); };
  }, [returnFocus]);
  useEffect(() => {
    let active = true;
    invoke<Passage>("knowledge_passage", { chunkId: source.chunk_id, projectId: source.project_id ?? null })
      .then((result) => { if (active) setPassage(result); })
      .catch((reason: unknown) => { if (active) setError(String(reason)); });
    return () => { active = false; };
  }, [source.chunk_id, source.project_id, attempt]);
  return <dialog ref={dialog} className="passage-dialog" aria-labelledby="passage-title"
    onCancel={onClose}>
    <header><h2 id="passage-title">{source.title}</h2><button type="button" autoFocus onClick={onClose}>Close source</button></header>
    {error ? <div role="alert"><p>{error}</p><p>Search again if this folder was moved or reindexed.</p>
      <button type="button" onClick={() => { setError(""); setAttempt((n) => n + 1); }}>Retry opening source</button></div>
      : passage ? <>
        <p className="passage-path">{passage.file_path}</p>
        <p><strong>{passage.location}</strong> · Indexed passage</p>
        <p role="status" className={passage.file_state === "current" ? "" : "passage-warning"}>{passage.notice}</p>
        <pre tabIndex={0} aria-label="Source passage">{passage.content}</pre>
        {passage.file_state !== "current" && <p>To update this passage, open Knowledge and choose Retry indexing for its folder.</p>}
      </> : <p role="status">Opening source…</p>}
  </dialog>;
}
