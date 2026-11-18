import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import type { Task } from "../hooks/useProjects";

interface Dependency { task_id: string; requires_id: string }
interface Snapshot { revision: number | string; dependencies: Dependency[] }
type State = { status: "loading" } | { status: "error" } | { status: "ready"; dependencies: Dependency[] };

export function TaskPrerequisites({ projectId, tasks, tasksLoading }: { projectId: string; tasks: Task[]; tasksLoading: boolean }) {
  const [state, setState] = useState<State>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let active = true;
    setState({ status: "loading" });
    invoke<Snapshot>("agent_hub_snapshot", { projectId })
      .then(snapshot => {
        if (!active) return;
        // Trust boundary: the snapshot comes from a separate service.
        if (!Array.isArray(snapshot?.dependencies) || !snapshot.dependencies.every(
          d => typeof d?.task_id === "string" && typeof d?.requires_id === "string",
        )) throw new Error("Malformed snapshot");
        setState({ status: "ready", dependencies: snapshot.dependencies });
      })
      .catch(() => { if (active) setState({ status: "error" }); });
    return () => { active = false; };
  }, [projectId, attempt]);

  const titles = new Map(tasks.filter(task => task.project_id === projectId).map(task => [task.id, task.title]));
  const label = (id: string) => titles.get(id) ?? "Task no longer in this project";
  return <section className="task-prerequisites" aria-label="Task prerequisites">
    <div className="section-heading"><h3>Task prerequisites</h3></div>
    {state.status === "loading" || (state.status === "ready" && tasksLoading) ? <p className="muted" role="status">Loading prerequisites…</p>
      : state.status === "error" ? <div className="page-notice error" role="alert">
        <span>Task prerequisites are unavailable. Try loading them again.</span>
        <button className="page-button" onClick={() => setAttempt(value => value + 1)}>Retry</button>
      </div>
      : !state.dependencies.length ? <p className="muted">No task prerequisites recorded for this project.</p>
      : <ul className="record-list">
        {state.dependencies.map(dependency => <li className="task-record" key={`${dependency.task_id}:${dependency.requires_id}`}>
          <div className="record-copy">
            <h3 title={titles.has(dependency.task_id) ? undefined : dependency.task_id}>{label(dependency.task_id)}</h3>
            <p>Requires <strong title={titles.has(dependency.requires_id) ? undefined : dependency.requires_id}>{label(dependency.requires_id)}</strong></p>
          </div>
        </li>)}
      </ul>}
  </section>;
}
