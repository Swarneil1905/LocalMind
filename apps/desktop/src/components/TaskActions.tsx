import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import "./project-dependencies.css";

interface TaskAction {
  id: string;
  project_id: string;
  project_name?: string;
  state: "proposed" | "created" | "dismissed";
  arguments: { title: string; due_at: string | null };
  receipt: { task_id: string; title: string; created_at: string } | null;
  task_exists?: boolean;
}

export function TaskActions({ conversationId, projectId, isStreaming, onOpenTasks }: {
  conversationId: string; projectId: string | null; isStreaming: boolean; onOpenTasks: () => void;
}) {
  const [actions, setActions] = useState<TaskAction[]>([]);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const epoch = useRef(0);
  const refresh = useCallback(async () => {
    const version = ++epoch.current;
    try {
      const result = await invoke<{ actions: TaskAction[] }>("list_task_actions", { conversationId });
      if (version === epoch.current) { setActions(Array.isArray(result?.actions) ? result.actions : []); setError(""); }
    } catch (reason) {
      if (version === epoch.current) setError(String(reason));
    }
  }, [conversationId]);
  useEffect(() => {
    const version = ++epoch.current;
    let active = true;
    if (!isStreaming) {
      invoke<{ actions: TaskAction[] }>("list_task_actions", { conversationId })
        .then(result => { if (active && version === epoch.current) { setActions(Array.isArray(result?.actions) ? result.actions : []); setError(""); } })
        .catch(reason => { if (active && version === epoch.current) setError(String(reason)); });
    }
    return () => { active = false; epoch.current = version + 1; };
  }, [conversationId, isStreaming]);

  async function review(id: string, decision: "confirm" | "dismiss") {
    if (busy) return;
    setBusy(id); setError("");
    const version = epoch.current;
    try {
      const saved = await invoke<TaskAction>("review_task_action", { operationId: id, conversationId, decision });
      if (version === epoch.current) {
        setActions(previous => previous.map(action => action.id === id ? saved : action));
        await refresh();
      }
    } catch (reason) {
      if (version === epoch.current) setError(`${String(reason)} You can retry this same proposal safely.`);
    } finally { if (version <= epoch.current) setBusy(null); }
  }
  if (!actions.length && !error) return null;
  return <section className="task-action-panel" aria-label="Task requests">
    <details open={actions.some(action => action.state === "proposed") || !!error}>
      <summary>Task requests <span>{actions.length}</span></summary>
      {error && <div role="alert"><p>{error}</p><button className="page-button" onClick={() => void refresh()}>Refresh task requests</button></div>}
      {actions.map(action => <article className="dependency-card" key={action.id} aria-label={`Task request: ${action.arguments.title}`}>
        <strong>{action.arguments.title}</strong>
        <p>Project: {action.project_name || action.project_id}</p>
        <p>{action.arguments.due_at ? `Due ${action.arguments.due_at}` : "No due date"}</p>
        {action.state === "proposed" ? <>
          <p>Proposed task. Nothing has been created yet.</p>
          {action.project_id !== projectId && <p role="status">This request belongs to the chat’s previous project. Move the chat back or dismiss it.</p>}
          <div className="dependency-actions">
            <button className="page-button primary" disabled={!!busy || isStreaming || action.project_id !== projectId} onClick={() => void review(action.id, "confirm")}>{busy === action.id ? "Saving…" : "Create task"}</button>
            <button className="page-button" disabled={!!busy || isStreaming} onClick={() => void review(action.id, "dismiss")}>Dismiss</button>
          </div>
        </> : action.state === "created" && action.receipt ? <>
          <p role="status">Task created{action.task_exists === false ? ". It has since been removed." : "."}</p>
          <small>Task ID: {action.receipt.task_id}</small>
          <button className="page-text-button" onClick={onOpenTasks}>View tasks</button>
        </> : <p>Dismissed. No task created.</p>}
      </article>)}
    </details>
  </section>;
}
