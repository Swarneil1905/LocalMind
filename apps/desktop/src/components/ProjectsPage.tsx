import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import {
  CheckCircle2,
  Circle,
  FolderOpen,
  LoaderCircle,
  MessageSquare,
  Plus,
  RefreshCw,
  Trash2,
} from "lucide-react";
import { Project, Task, useProjects } from "../hooks/useProjects";
import { Conversation } from "../hooks/useConversations";
import { MODEL_MAP } from "../types";
import "./supporting-pages.css";

type Tab = "summary" | "tasks" | "conversations";
const STATUS_LABEL: Record<Task["status"], string> = {
  open: "Open",
  in_progress: "In progress",
  done: "Done",
  cancelled: "Cancelled",
};

export function ProjectsPage({
  speedModel = MODEL_MAP.speed,
}: {
  speedModel?: string;
}) {
  const {
    projects,
    activeProjectId,
    tasks,
    loading,
    tasksLoading,
    error: loadError,
    refresh,
    selectProject,
    createProject,
    updateProject,
    deleteProject,
    generateSummary,
    createTask,
    updateTaskStatus,
    deleteTask,
  } = useProjects();
  const [tab, setTab] = useState<Tab>("summary");
  const [newName, setNewName] = useState("");
  const [showCreate, setShowCreate] = useState(false);
  const [newTask, setNewTask] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [conversations, setConversations] = useState<Conversation[]>([]);
  const [conversationState, setConversationState] = useState<
    "loading" | "ready" | "error"
  >("loading");
  const activeProject = projects.find(
    (project) => project.id === activeProjectId,
  );
  const activeTasks = tasks.filter(
    (task) => task.project_id === activeProjectId,
  );
  useEffect(() => {
    if (tab !== "conversations" || !activeProjectId) return;
    let cancelled = false;
    invoke<{ conversations: Conversation[] }>("get_project_conversations", {
      projectId: activeProjectId,
    })
      .then((result) => {
        if (!cancelled) {
          setConversations(result.conversations ?? []);
          setConversationState("ready");
        }
      })
      .catch(() => {
        if (!cancelled) setConversationState("error");
      });
    return () => {
      cancelled = true;
    };
  }, [tab, activeProjectId]);
  async function run(name: string, operation: () => Promise<unknown>) {
    if (busy) return;
    setBusy(name);
    setError(null);
    try {
      await operation();
    } catch {
      setError("That change couldn’t be saved. Please try again.");
    } finally {
      setBusy(null);
    }
  }
  function choose(id: string) {
    selectProject(id);
    setNewTask("");
    setError(null);
    setConversationState("loading");
  }
  async function create(event: React.FormEvent) {
    event.preventDefault();
    if (!newName.trim()) return;
    await run("create", async () => {
      const project = await createProject(newName.trim());
      choose(project.id);
      setNewName("");
      setShowCreate(false);
    });
  }
  return (
    <div className="workspace-page projects-page">
      <div className="workspace-page-inner">
        <header className="page-heading">
          <div>
            <h1>Projects</h1>
            <p>Keep related conversations and tasks together.</p>
          </div>
          <button
            className="page-button primary"
            onClick={() => setShowCreate(true)}
          >
            <Plus size={15} />
            New project
          </button>
        </header>
        {error && (
          <p className="page-notice error" role="alert">
            {error}
          </p>
        )}
        {loadError && (
          <div className="page-notice error" role="alert">
            <span>{loadError}</span>
            <button className="page-button" onClick={() => void refresh()}>
              Retry
            </button>
          </div>
        )}
        {showCreate && (
          <form className="page-form-row project-create" onSubmit={create}>
            <input
              className="page-input grow"
              autoFocus
              aria-label="New project name"
              placeholder="Give your project a name"
              value={newName}
              onChange={(event) => setNewName(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === "Escape") setShowCreate(false);
              }}
            />
            <button
              className="page-button primary"
              disabled={!newName.trim() || !!busy}
            >
              {busy === "create" ? "Creating…" : "Create project"}
            </button>
            <button
              type="button"
              className="page-button"
              onClick={() => {
                setShowCreate(false);
                setNewName("");
              }}
            >
              Cancel
            </button>
          </form>
        )}
        <div className="projects-layout">
          <nav className="project-list" aria-label="Projects">
            <div className="project-list-label">
              Your projects <span>{projects.length}</span>
            </div>
            {projects.map((project) => (
              <div
                className={`project-list-item ${project.id === activeProjectId ? "active" : ""}`}
                key={project.id}
              >
                <button
                  className="project-select"
                  aria-current={
                    project.id === activeProjectId ? "page" : undefined
                  }
                  onClick={() => choose(project.id)}
                >
                  <FolderOpen size={16} />
                  <span>{project.name}</span>
                </button>
                <button
                  className="page-icon-button"
                  aria-label={`Delete project: ${project.name}`}
                  disabled={!!busy}
                  onClick={() => {
                    if (
                      window.confirm(
                        `Delete “${project.name}” and its tasks? This cannot be undone.`,
                      )
                    )
                      void run("delete", () => deleteProject(project.id));
                  }}
                >
                  <Trash2 size={14} />
                </button>
              </div>
            ))}
            {!projects.length && (
              <p className="project-list-empty">
                {loading
                  ? "Loading projects…"
                  : loadError
                    ? "Projects unavailable"
                    : "No projects yet"}
              </p>
            )}
          </nav>
          <section
            className="project-detail"
            aria-label={activeProject?.name ?? "Project details"}
          >
            {activeProject ? (
              <>
                <div className="project-detail-heading">
                  <div className="empty-icon small">
                    <FolderOpen size={19} />
                  </div>
                  <div>
                    <h2>{activeProject.name}</h2>
                    <p>
                      Updated{" "}
                      {new Date(activeProject.updated_at).toLocaleDateString(
                        undefined,
                        { month: "short", day: "numeric" },
                      )}
                    </p>
                  </div>
                </div>
                <div className="page-tabs" aria-label="Project view">
                  {(["summary", "tasks", "conversations"] as Tab[]).map(
                    (value) => (
                      <button
                        key={value}
                        aria-pressed={tab === value}
                        onClick={() => {
                          setTab(value);
                          if (value === "conversations" && value !== tab)
                            setConversationState("loading");
                        }}
                      >
                        {value === "summary"
                          ? "Overview"
                          : value === "tasks"
                            ? "Tasks"
                            : "Conversations"}
                        {value === "tasks" && <span>{activeTasks.length}</span>}
                      </button>
                    ),
                  )}
                </div>
                <div className="project-detail-body">
                  {tab === "summary" && (
                    <ProjectSummary
                      key={activeProject.id}
                      project={activeProject}
                      busy={busy}
                      onGenerate={() =>
                        void run("summary", () =>
                          generateSummary(activeProject.id, speedModel),
                        )
                      }
                      onSavePath={async (path) => {
                        await updateProject(activeProject.id, { path });
                      }}
                    />
                  )}
                  {tab === "tasks" && (
                    <>
                      <form
                        className="page-form-row"
                        onSubmit={(event) => {
                          event.preventDefault();
                          if (newTask.trim())
                            void run("task", async () => {
                              await createTask(newTask.trim());
                              setNewTask("");
                            });
                        }}
                      >
                        <input
                          className="page-input grow"
                          aria-label="New project task"
                          placeholder="Add a next step"
                          value={newTask}
                          onChange={(event) => setNewTask(event.target.value)}
                        />
                        <button
                          className="page-button primary"
                          disabled={!newTask.trim() || !!busy}
                        >
                          <Plus size={15} />
                          Add
                        </button>
                      </form>
                      {tasksLoading ? (
                        <p className="muted" role="status">
                          Loading tasks…
                        </p>
                      ) : !activeTasks.length ? (
                        <div className="quiet-empty">
                          <CheckCircle2 size={22} />
                          <div>
                            <h3>No tasks yet</h3>
                            <p>Add a next step for this project.</p>
                          </div>
                        </div>
                      ) : (
                        <div className="record-list">
                          {activeTasks.map((task) => (
                            <div
                              className={`task-record ${task.status === "done" ? "is-complete" : ""}`}
                              key={task.id}
                            >
                              <button
                                className="page-icon-button"
                                disabled={!!busy}
                                aria-label={`Mark ${task.title} ${task.status === "done" ? "open" : "done"}`}
                                onClick={() =>
                                  void run(task.id, () =>
                                    updateTaskStatus(
                                      task.id,
                                      task.status === "done" ? "open" : "done",
                                    ),
                                  )
                                }
                              >
                                {task.status === "done" ? (
                                  <CheckCircle2 size={18} />
                                ) : (
                                  <Circle size={18} />
                                )}
                              </button>
                              <div className="record-copy">
                                <h3>{task.title}</h3>
                              </div>
                              <select
                                className="page-select task-state-select"
                                aria-label={`Status for ${task.title}`}
                                disabled={!!busy}
                                value={task.status}
                                onChange={(event) =>
                                  void run(task.id, () =>
                                    updateTaskStatus(
                                      task.id,
                                      event.target.value as Task["status"],
                                    ),
                                  )
                                }
                              >
                                {Object.entries(STATUS_LABEL).map(
                                  ([value, label]) => (
                                    <option key={value} value={value}>
                                      {label}
                                    </option>
                                  ),
                                )}
                              </select>
                              <button
                                className="page-icon-button"
                                aria-label={`Delete task: ${task.title}`}
                                disabled={!!busy}
                                onClick={() => {
                                  if (window.confirm(`Delete “${task.title}”?`))
                                    void run(task.id, () =>
                                      deleteTask(task.id),
                                    );
                                }}
                              >
                                <Trash2 size={14} />
                              </button>
                            </div>
                          ))}
                        </div>
                      )}
                    </>
                  )}
                  {tab === "conversations" &&
                    (conversationState === "loading" ? (
                      <p className="muted" role="status">
                        Loading conversations…
                      </p>
                    ) : conversationState === "error" ? (
                      <p className="page-notice error" role="alert">
                        Couldn’t load conversations. Switch tabs to try again.
                      </p>
                    ) : conversations.length ? (
                      <div className="record-list">
                        {conversations.map((conversation) => (
                          <div
                            className="conversation-record"
                            key={conversation.id}
                          >
                            <MessageSquare size={16} />
                            <div className="record-copy">
                              <h3>{conversation.title}</h3>
                              <time dateTime={conversation.updated_at}>
                                {new Date(
                                  conversation.updated_at,
                                ).toLocaleDateString()}
                              </time>
                            </div>
                          </div>
                        ))}
                      </div>
                    ) : (
                      <div className="quiet-empty">
                        <MessageSquare size={22} />
                        <div>
                          <h3>No linked conversations</h3>
                          <p>
                            Select this project from the chat header to keep
                            conversations here.
                          </p>
                        </div>
                      </div>
                    ))}
                </div>
              </>
            ) : (
              <div className="page-empty-state">
                <div className="empty-icon">
                  <FolderOpen size={26} strokeWidth={1.5} />
                </div>
                <h2>
                  {projects.length
                    ? "Choose a project"
                    : "Give your work a home"}
                </h2>
                <p>
                  {projects.length
                    ? "Select a project to see its tasks and conversations."
                    : "Create a project to group related conversations and track what’s next."}
                </p>
                {!projects.length && (
                  <button
                    className="page-button"
                    onClick={() => setShowCreate(true)}
                  >
                    <Plus size={15} />
                    Create a project
                  </button>
                )}
              </div>
            )}
          </section>
        </div>
      </div>
    </div>
  );
}
function ProjectSummary({
  project,
  busy,
  onGenerate,
  onSavePath,
}: {
  project: Project;
  busy: string | null;
  onGenerate: () => void;
  onSavePath: (path: string) => Promise<void>;
}) {
  const [editing, setEditing] = useState(false);
  const [path, setPath] = useState(project.path ?? "");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(false);
  async function save(event: React.FormEvent) {
    event.preventDefault();
    setSaving(true);
    setError(false);
    try {
      await onSavePath(path);
      setEditing(false);
    } catch {
      setError(true);
    } finally {
      setSaving(false);
    }
  }
  return (
    <div className="project-summary">
      <section>
        <div className="section-heading">
          <h3>Summary</h3>
          <button
            className="page-text-button"
            disabled={!!busy}
            onClick={onGenerate}
          >
            {busy === "summary" ? (
              <LoaderCircle size={14} className="spin" />
            ) : (
              <RefreshCw size={14} />
            )}
            {busy === "summary"
              ? "Generating…"
              : project.summary
                ? "Regenerate"
                : "Generate summary"}
          </button>
        </div>
        <p className="project-summary-text">
          {project.summary ||
            "Generate a summary from this project’s linked conversations."}
        </p>
      </section>
      <section>
        <div className="section-heading">
          <h3>Working folder</h3>
          {!editing && (
            <button
              className="page-text-button"
              onClick={() => setEditing(true)}
            >
              {project.path ? "Change" : "Set folder path"}
            </button>
          )}
        </div>
        {editing ? (
          <form className="page-form-row" onSubmit={save}>
            <input
              className="page-input grow"
              autoFocus
              aria-label="Project folder path"
              placeholder="Full path to a folder"
              value={path}
              onChange={(event) => setPath(event.target.value)}
            />
            <button className="page-button primary" disabled={saving}>
              {saving ? "Saving…" : "Save"}
            </button>
            <button
              className="page-button"
              type="button"
              onClick={() => {
                setPath(project.path ?? "");
                setEditing(false);
              }}
            >
              Cancel
            </button>
          </form>
        ) : (
          <p className="record-path">
            {project.path || "No working folder set"}
          </p>
        )}
        {error && (
          <p className="error-text" role="alert">
            Couldn’t save the folder path. Please try again.
          </p>
        )}
        <p className="page-footnote">
          A reference for this project. Add the folder in Knowledge to index its
          contents.
        </p>
      </section>
      <p className="muted">
        Created{" "}
        {new Date(project.created_at).toLocaleDateString(undefined, {
          month: "long",
          day: "numeric",
          year: "numeric",
        })}
      </p>
    </div>
  );
}
