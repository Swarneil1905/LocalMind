// Spec reference: Phase 5.5 - TasksPage
// Cross-project task view: create, status-cycle, and delete tasks.

import { invoke } from "@tauri-apps/api/core";
import { CheckSquare, Circle, CircleCheck, Loader, Plus, Trash2 } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { Project } from "../hooks/useProjects";

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

type StatusFilter = "all" | "open" | "in_progress" | "done";

const STATUS_CYCLE: Record<TaskWithProject["status"], TaskWithProject["status"]> = {
  open: "in_progress",
  in_progress: "done",
  done: "open",
  cancelled: "open",
};

const STATUS_LABEL: Record<TaskWithProject["status"], string> = {
  open: "Open",
  in_progress: "In progress",
  done: "Done",
  cancelled: "Cancelled",
};

const STATUS_COLOR: Record<TaskWithProject["status"], string> = {
  open: "var(--text-3)",
  in_progress: "#f59e0b",
  done: "#22c55e",
  cancelled: "var(--text-3)",
};

function StatusIcon({ status, size = 14 }: { status: TaskWithProject["status"]; size?: number }) {
  const color = STATUS_COLOR[status];
  if (status === "done") return <CircleCheck size={size} strokeWidth={1.5} color={color} />;
  if (status === "in_progress") return <Loader size={size} strokeWidth={1.5} color={color} />;
  return <Circle size={size} strokeWidth={1.5} color={color} />;
}

interface TasksPageProps {
  projects: Project[];
}

export function TasksPage({ projects }: TasksPageProps) {
  const [tasks, setTasks] = useState<TaskWithProject[]>([]);
  const [filter, setFilter] = useState<StatusFilter>("all");
  const [selectedProject, setSelectedProject] = useState<string>("all");
  const [newTitle, setNewTitle] = useState("");
  // Controlled project selector for the add-task row; falls back to first project
  const [newProjectId, setNewProjectId] = useState<string>("");
  const inputRef = useRef<HTMLInputElement>(null);

  // Derive the effective new-task project without a setState-in-effect
  const effectiveNewProjectId = newProjectId || projects[0]?.id || "";

  useEffect(() => {
    async function fetchTasks() {
      try {
        const result = await invoke<TaskWithProject[]>("list_all_tasks");
        setTasks(result);
      } catch {
        // sidecar not ready
      }
    }
    fetchTasks();
  }, []);

  const handleAdd = useCallback(async () => {
    if (!newTitle.trim() || !effectiveNewProjectId) return;
    try {
      const task = await invoke<TaskWithProject>("create_task", {
        projectId: effectiveNewProjectId,
        title: newTitle.trim(),
        dueAt: null,
      });
      setTasks((prev) => [task, ...prev]);
      setNewTitle("");
      inputRef.current?.focus();
    } catch {
      // swallow
    }
  }, [newTitle, effectiveNewProjectId]);

  const handleStatusCycle = useCallback(async (task: TaskWithProject) => {
    const next = STATUS_CYCLE[task.status];
    try {
      await invoke("update_task", {
        projectId: task.project_id,
        taskId: task.id,
        title: null,
        status: next,
        dueAt: null,
      });
      setTasks((prev) =>
        prev.map((t) => (t.id === task.id ? { ...t, status: next } : t))
      );
    } catch {
      // swallow
    }
  }, []);

  const handleDelete = useCallback(async (task: TaskWithProject) => {
    try {
      await invoke("delete_task", {
        projectId: task.project_id,
        taskId: task.id,
      });
      setTasks((prev) => prev.filter((t) => t.id !== task.id));
    } catch {
      // swallow
    }
  }, []);

  const visible = tasks.filter((t) => {
    const matchStatus = filter === "all" || t.status === filter;
    const matchProject =
      selectedProject === "all" || t.project_id === selectedProject;
    return matchStatus && matchProject;
  });

  const counts: Record<StatusFilter, number> = {
    all: tasks.length,
    open: tasks.filter((t) => t.status === "open").length,
    in_progress: tasks.filter((t) => t.status === "in_progress").length,
    done: tasks.filter((t) => t.status === "done").length,
  };

  const FILTERS: { id: StatusFilter; label: string }[] = [
    { id: "all", label: "All" },
    { id: "open", label: "Open" },
    { id: "in_progress", label: "In progress" },
    { id: "done", label: "Done" },
  ];

  return (
    <div style={{ flex: 1, overflowY: "auto", padding: "24px 32px" }}>
      <div style={{ maxWidth: 680, margin: "0 auto" }}>

        {/* Header */}
        <div
          style={{
            display: "flex",
            alignItems: "center",
            gap: 10,
            marginBottom: 20,
            paddingBottom: 16,
            borderBottom: "1px solid var(--border)",
          }}
        >
          <CheckSquare size={18} strokeWidth={1.5} color="var(--accent)" />
          <div>
            <h2 style={{ fontSize: 15, fontWeight: 600, color: "var(--text)" }}>
              Tasks
            </h2>
            <p style={{ fontSize: 12, color: "var(--text-3)", marginTop: 2 }}>
              Manage tasks across all projects.
            </p>
          </div>
          <span
            style={{
              marginLeft: "auto",
              fontSize: 11,
              color: "var(--text-3)",
              backgroundColor: "var(--surface-2)",
              border: "1px solid var(--border)",
              borderRadius: 10,
              padding: "2px 8px",
            }}
          >
            {counts.open} open · {counts.in_progress} in progress
          </span>
        </div>

        {/* Add task row */}
        <div style={{ display: "flex", gap: 8, marginBottom: 16 }}>
          <input
            ref={inputRef}
            value={newTitle}
            onChange={(e) => setNewTitle(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") handleAdd();
            }}
            placeholder="New task title..."
            style={{
              flex: 1,
              fontSize: 13,
              padding: "6px 10px",
              backgroundColor: "var(--surface)",
              border: "1px solid var(--border)",
              borderRadius: 6,
              color: "var(--text)",
              outline: "none",
            }}
          />
          <select
            value={effectiveNewProjectId}
            onChange={(e) => setNewProjectId(e.target.value)}
            style={{
              fontSize: 12,
              padding: "6px 8px",
              backgroundColor: "var(--surface)",
              border: "1px solid var(--border)",
              borderRadius: 6,
              color: "var(--text)",
              maxWidth: 160,
            }}
          >
            {projects.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
          <button
            onClick={handleAdd}
            disabled={!newTitle.trim() || !effectiveNewProjectId}
            style={{
              display: "flex",
              alignItems: "center",
              gap: 4,
              padding: "6px 12px",
              fontSize: 12,
              fontWeight: 500,
              backgroundColor:
                newTitle.trim() && effectiveNewProjectId
                  ? "var(--accent)"
                  : "var(--surface-2)",
              color:
                newTitle.trim() && effectiveNewProjectId
                  ? "#fff"
                  : "var(--text-3)",
              border: "1px solid var(--border)",
              borderRadius: 6,
              cursor:
                newTitle.trim() && effectiveNewProjectId ? "pointer" : "default",
              flexShrink: 0,
            }}
          >
            <Plus size={13} strokeWidth={2} />
            Add
          </button>
        </div>

        {/* Filter + project bar */}
        <div
          style={{
            display: "flex",
            alignItems: "center",
            gap: 8,
            marginBottom: 12,
            flexWrap: "wrap",
          }}
        >
          {FILTERS.map((f) => (
            <button
              key={f.id}
              onClick={() => setFilter(f.id)}
              style={{
                fontSize: 11,
                padding: "3px 10px",
                borderRadius: 10,
                border: "1px solid var(--border)",
                backgroundColor:
                  filter === f.id ? "var(--accent)" : "var(--surface-2)",
                color: filter === f.id ? "#fff" : "var(--text-3)",
                cursor: "pointer",
                fontWeight: filter === f.id ? 600 : 400,
              }}
            >
              {f.label}
              {counts[f.id] > 0 && (
                <span style={{ marginLeft: 4, opacity: 0.7 }}>
                  {counts[f.id]}
                </span>
              )}
            </button>
          ))}
          {projects.length > 1 && (
            <select
              value={selectedProject}
              onChange={(e) => setSelectedProject(e.target.value)}
              style={{
                marginLeft: "auto",
                fontSize: 11,
                padding: "3px 8px",
                backgroundColor: "var(--surface-2)",
                border: "1px solid var(--border)",
                borderRadius: 6,
                color: "var(--text-3)",
              }}
            >
              <option value="all">All projects</option>
              {projects.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          )}
        </div>

        {/* Empty state */}
        {visible.length === 0 && (
          <div
            style={{
              textAlign: "center",
              padding: "48px 0",
              color: "var(--text-3)",
            }}
          >
            <CheckSquare
              size={32}
              strokeWidth={1}
              style={{ margin: "0 auto 12px", display: "block" }}
            />
            <p style={{ fontSize: 13 }}>
              {tasks.length === 0
                ? "No tasks yet"
                : "No tasks match this filter"}
            </p>
            {tasks.length === 0 && (
              <p style={{ fontSize: 12, marginTop: 4 }}>
                Type a title above and pick a project to create your first task.
              </p>
            )}
          </div>
        )}

        {/* Task list */}
        {visible.length > 0 && (
          <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
            {visible.map((task) => (
              <div
                key={task.id}
                style={{
                  display: "flex",
                  alignItems: "center",
                  gap: 10,
                  backgroundColor: "var(--surface)",
                  border: "1px solid var(--border)",
                  borderRadius: 6,
                  padding: "8px 12px",
                  opacity: task.status === "cancelled" ? 0.5 : 1,
                }}
              >
                {/* Status toggle */}
                <button
                  onClick={() => handleStatusCycle(task)}
                  title={`Status: ${STATUS_LABEL[task.status]} — click to advance`}
                  style={{
                    display: "flex",
                    alignItems: "center",
                    flexShrink: 0,
                    color: STATUS_COLOR[task.status],
                  }}
                >
                  <StatusIcon status={task.status} />
                </button>

                {/* Title + meta */}
                <div style={{ flex: 1, minWidth: 0 }}>
                  <p
                    style={{
                      fontSize: 13,
                      color: "var(--text)",
                      lineHeight: 1.4,
                      textDecoration:
                        task.status === "done" ? "line-through" : "none",
                      overflow: "hidden",
                      textOverflow: "ellipsis",
                      whiteSpace: "nowrap",
                    }}
                  >
                    {task.title}
                  </p>
                  <p
                    style={{ fontSize: 11, color: "var(--text-3)", marginTop: 1 }}
                  >
                    {task.project_name ?? "Unknown project"}
                    {task.due_at && (
                      <>
                        {" · "}
                        <span>
                          Due{" "}
                          {new Date(task.due_at).toLocaleDateString(undefined, {
                            month: "short",
                            day: "numeric",
                          })}
                        </span>
                      </>
                    )}
                  </p>
                </div>

                {/* Status badge */}
                <span
                  style={{
                    fontSize: 10,
                    fontWeight: 500,
                    color: STATUS_COLOR[task.status],
                    backgroundColor: "var(--surface-2)",
                    border: "1px solid var(--border)",
                    borderRadius: 8,
                    padding: "1px 7px",
                    flexShrink: 0,
                  }}
                >
                  {STATUS_LABEL[task.status]}
                </span>

                {/* Delete */}
                <button
                  onClick={() => handleDelete(task)}
                  title="Delete task"
                  style={{
                    display: "flex",
                    alignItems: "center",
                    color: "var(--text-3)",
                    flexShrink: 0,
                  }}
                >
                  <Trash2 size={12} strokeWidth={1.5} />
                </button>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
