import { invoke } from "@tauri-apps/api/core";
import {
  CheckSquare,
  Circle,
  CircleCheck,
  CircleDot,
  LoaderCircle,
  Plus,
  RefreshCw,
  Trash2,
} from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { Project } from "../hooks/useProjects";
import "./supporting-pages.css";

interface TaskWithProject {
  id: string;
  project_id: string;
  title: string;
  status: "open" | "in_progress" | "done" | "cancelled";
  due_at: string | null;
  created_at: string;
  updated_at: string;
  project_name: string | null;
}
type Filter = "all" | TaskWithProject["status"];
const STATUS_LABEL = {
  open: "Open",
  in_progress: "In progress",
  done: "Done",
  cancelled: "Cancelled",
};
const NEXT_STATUS: Record<
  TaskWithProject["status"],
  TaskWithProject["status"]
> = {
  open: "in_progress",
  in_progress: "done",
  done: "open",
  cancelled: "open",
};

export function TasksPage({ projects }: { projects: Project[] }) {
  const [tasks, setTasks] = useState<TaskWithProject[]>([]);
  const [filter, setFilter] = useState<Filter>("all");
  const [projectFilter, setProjectFilter] = useState("all");
  const [newTitle, setNewTitle] = useState("");
  const [newProjectId, setNewProjectId] = useState("");
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [retry, setRetry] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const selectedId = projects.some((item) => item.id === newProjectId)
    ? newProjectId
    : (projects[0]?.id ?? "");
  useEffect(() => {
    let cancelled = false;
    invoke<TaskWithProject[]>("list_all_tasks")
      .then((result) => {
        if (!cancelled) {
          setTasks(Array.isArray(result) ? result : []);
          setError(null);
        }
      })
      .catch(() => {
        if (!cancelled) setError("Couldn’t load tasks. Please try again.");
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [retry]);
  async function add(event: React.FormEvent) {
    event.preventDefault();
    if (!newTitle.trim() || !selectedId || busy) return;
    setBusy("new");
    setError(null);
    try {
      const task = await invoke<TaskWithProject>("create_task", {
        projectId: selectedId,
        title: newTitle.trim(),
        dueAt: null,
      });
      setTasks((previous) => [
        {
          ...task,
          project_name:
            projects.find((item) => item.id === selectedId)?.name ?? null,
        },
        ...previous,
      ]);
      setNewTitle("");
      inputRef.current?.focus();
    } catch {
      setError(
        "Couldn’t create the task. Your draft is still here; try again.",
      );
    } finally {
      setBusy(null);
    }
  }
  async function update(task: TaskWithProject, remove = false) {
    if (busy) return;
    setBusy(task.id);
    setError(null);
    try {
      if (remove) {
        await invoke("delete_task", {
          projectId: task.project_id,
          taskId: task.id,
        });
        setTasks((previous) => previous.filter((item) => item.id !== task.id));
      } else {
        const next = NEXT_STATUS[task.status];
        await invoke("update_task", {
          projectId: task.project_id,
          taskId: task.id,
          title: null,
          status: next,
          dueAt: null,
        });
        setTasks((previous) =>
          previous.map((item) =>
            item.id === task.id ? { ...item, status: next } : item,
          ),
        );
      }
    } catch {
      setError(
        remove
          ? "Couldn’t delete the task. Please try again."
          : "Couldn’t update the task. Please try again.",
      );
    } finally {
      setBusy(null);
    }
  }
  const projectTasks = tasks.filter(
    (task) => projectFilter === "all" || task.project_id === projectFilter,
  );
  const visible = projectTasks.filter(
    (task) => filter === "all" || task.status === filter,
  );
  return (
    <div className="workspace-page">
      <div className="workspace-page-inner">
        <header className="page-heading">
          <div>
            <h1>Tasks</h1>
            <p>Keep track of the next steps across your projects.</p>
          </div>
          <button
            className="page-icon-button"
            aria-label="Refresh tasks"
            disabled={loading}
            onClick={() => {
              setLoading(true);
              setRetry((value) => value + 1);
            }}
          >
            <RefreshCw size={16} />
          </button>
        </header>
        {error && (
          <p className="page-notice error" role="alert">
            {error}
          </p>
        )}
        {projects.length ? (
          <form className="page-form-row task-create" onSubmit={add}>
            <input
              ref={inputRef}
              className="page-input grow"
              aria-label="Task title"
              placeholder="What needs to be done?"
              value={newTitle}
              onChange={(event) => setNewTitle(event.target.value)}
            />
            <select
              className="page-select"
              aria-label="Project for new task"
              value={selectedId}
              onChange={(event) => setNewProjectId(event.target.value)}
            >
              {projects.map((project) => (
                <option key={project.id} value={project.id}>
                  {project.name}
                </option>
              ))}
            </select>
            <button
              className="page-button primary"
              disabled={!newTitle.trim() || !!busy}
            >
              {busy === "new" ? (
                <LoaderCircle size={15} className="spin" />
              ) : (
                <Plus size={15} />
              )}
              Add task
            </button>
          </form>
        ) : (
          <p className="page-notice">
            Create a project in Projects first. Each task belongs to a project.
          </p>
        )}
        <div className="page-toolbar">
          <div className="page-tabs" aria-label="Filter tasks">
            {(
              ["all", "open", "in_progress", "done", "cancelled"] as Filter[]
            ).map((value) => (
              <button
                key={value}
                aria-pressed={filter === value}
                onClick={() => setFilter(value)}
              >
                {value === "all" ? "All tasks" : STATUS_LABEL[value]}
                <span>
                  {
                    projectTasks.filter(
                      (task) => value === "all" || task.status === value,
                    ).length
                  }
                </span>
              </button>
            ))}
          </div>
          {projects.length > 1 && (
            <select
              className="page-select"
              aria-label="Filter by project"
              value={projectFilter}
              onChange={(event) => setProjectFilter(event.target.value)}
            >
              <option value="all">All projects</option>
              {projects.map((project) => (
                <option key={project.id} value={project.id}>
                  {project.name}
                </option>
              ))}
            </select>
          )}
        </div>
        {loading ? (
          <div className="page-empty-state" role="status">
            <LoaderCircle className="spin" size={23} />
            <p>Loading tasks…</p>
          </div>
        ) : !visible.length ? (
          <div className="page-empty-state">
            <div className="empty-icon">
              <CheckSquare size={25} strokeWidth={1.5} />
            </div>
            <h2>
              {tasks.length
                ? "No tasks in this view"
                : "A clear place for your next steps"}
            </h2>
            <p>
              {tasks.length
                ? "Choose a different status or project to see more tasks."
                : "Add a task above to keep your work moving."}
            </p>
          </div>
        ) : (
          <div className="record-list">
            {visible.map((task) => (
              <article
                className={`task-record ${task.status === "done" ? "is-complete" : ""}`}
                key={task.id}
              >
                <button
                  className="page-icon-button task-status"
                  aria-label={`Mark “${task.title}” as ${STATUS_LABEL[NEXT_STATUS[task.status]].toLowerCase()}`}
                  disabled={!!busy}
                  onClick={() => void update(task)}
                >
                  {task.status === "done" ? (
                    <CircleCheck size={18} />
                  ) : task.status === "in_progress" ? (
                    <CircleDot size={18} />
                  ) : (
                    <Circle size={18} />
                  )}
                </button>
                <div className="record-copy">
                  <h3>{task.title}</h3>
                  <p className="muted">
                    {task.project_name ??
                      projects.find((project) => project.id === task.project_id)
                        ?.name ??
                      "Project"}
                    {task.due_at
                      ? ` · Due ${new Date(task.due_at).toLocaleDateString(undefined, { month: "short", day: "numeric" })}`
                      : ""}
                  </p>
                </div>
                <span className={`page-badge task-badge ${task.status}`}>
                  {STATUS_LABEL[task.status]}
                </span>
                <button
                  className="page-icon-button"
                  disabled={!!busy}
                  aria-label={`Delete task: ${task.title}`}
                  onClick={() => {
                    if (window.confirm(`Delete “${task.title}”?`))
                      void update(task, true);
                  }}
                >
                  <Trash2 size={15} />
                </button>
              </article>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
