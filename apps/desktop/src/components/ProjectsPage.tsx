// Projects screen - Phase 4.
//
// Layout: narrow project list on the left, tabbed detail view on the right.
// Tabs: Summary | Tasks | Conversations | Decisions (Memory filtered to project)
//
// Spec reference: Section 15 (Projects Screen)

import { useCallback, useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import {
  FolderOpen,
  Plus,
  Trash2,
  RefreshCw,
  CheckSquare,
  Square,
  Clock,
  ChevronRight,
  Loader,
} from "lucide-react";
import { Project, Task, useProjects } from "../hooks/useProjects";
import { Conversation } from "../hooks/useConversations";
import { MODEL_MAP } from "../types";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type Tab = "summary" | "tasks" | "conversations";

// ---------------------------------------------------------------------------
// Main component
// ---------------------------------------------------------------------------

export function ProjectsPage() {
  const {
    projects,
    activeProjectId,
    tasks,
    selectProject,
    createProject,
    updateProject,
    deleteProject,
    generateSummary,
    createTask,
    updateTaskStatus,
    deleteTask,
  } = useProjects();

  const [activeTab, setActiveTab] = useState<Tab>("summary");
  const [newProjectName, setNewProjectName] = useState("");
  const [showNewProject, setShowNewProject] = useState(false);
  const [creating, setCreating] = useState(false);
  const [generatingSummary, setGeneratingSummary] = useState(false);
  const [newTaskTitle, setNewTaskTitle] = useState("");
  const [addingTask, setAddingTask] = useState(false);
  const [projectConvs, setProjectConvs] = useState<Conversation[]>([]);

  const activeProject = projects.find((p) => p.id === activeProjectId) ?? null;

  // Load conversations for active project when Conversations tab is shown
  useEffect(() => {
    if (activeTab !== "conversations" || !activeProjectId) {
      setProjectConvs([]);
      return;
    }
    invoke<{ conversations: Conversation[] }>("get_project_conversations", {
      projectId: activeProjectId,
    })
      .then((r) => setProjectConvs(r.conversations ?? []))
      .catch(() => setProjectConvs([]));
  }, [activeTab, activeProjectId]);

  const handleCreateProject = useCallback(async () => {
    const name = newProjectName.trim();
    if (!name) return;
    setCreating(true);
    try {
      const proj = await createProject(name);
      selectProject(proj.id);
      setNewProjectName("");
      setShowNewProject(false);
    } finally {
      setCreating(false);
    }
  }, [newProjectName, createProject, selectProject]);

  const handleGenerateSummary = useCallback(async () => {
    if (!activeProjectId) return;
    setGeneratingSummary(true);
    try {
      await generateSummary(activeProjectId, MODEL_MAP.speed);
    } finally {
      setGeneratingSummary(false);
    }
  }, [activeProjectId, generateSummary]);

  const handleAddTask = useCallback(async () => {
    const title = newTaskTitle.trim();
    if (!title) return;
    setAddingTask(true);
    try {
      await createTask(title);
      setNewTaskTitle("");
    } finally {
      setAddingTask(false);
    }
  }, [newTaskTitle, createTask]);

  const handleDeleteProject = useCallback(async (id: string) => {
    if (!confirm("Delete this project? Tasks will also be deleted.")) return;
    await deleteProject(id);
  }, [deleteProject]);

  return (
    <div style={{ display: "flex", flex: 1, overflow: "hidden" }}>
      {/* Project list */}
      <ProjectList
        projects={projects}
        activeId={activeProjectId}
        showNewProject={showNewProject}
        newProjectName={newProjectName}
        creating={creating}
        onSelect={selectProject}
        onDelete={handleDeleteProject}
        onNewProjectNameChange={setNewProjectName}
        onShowNewProject={() => setShowNewProject(true)}
        onCancelNew={() => { setShowNewProject(false); setNewProjectName(""); }}
        onCreateProject={handleCreateProject}
      />

      {/* Detail area */}
      <div style={{ flex: 1, display: "flex", flexDirection: "column", overflow: "hidden" }}>
        {activeProject ? (
          <>
            {/* Project header */}
            <div
              style={{
                padding: "12px 20px",
                borderBottom: "1px solid var(--border)",
                flexShrink: 0,
                display: "flex",
                alignItems: "center",
                gap: 12,
              }}
            >
              <span style={{ fontSize: 14, fontWeight: 600, color: "var(--text)", flex: 1 }}>
                {activeProject.name}
              </span>
              {activeProject.path && (
                <span style={{ fontSize: 11, color: "var(--text-3)", fontFamily: "monospace" }}>
                  {activeProject.path}
                </span>
              )}
            </div>

            {/* Tabs */}
            <div
              style={{
                display: "flex",
                borderBottom: "1px solid var(--border)",
                flexShrink: 0,
                padding: "0 20px",
              }}
            >
              {(["summary", "tasks", "conversations"] as Tab[]).map((tab) => (
                <button
                  key={tab}
                  onClick={() => setActiveTab(tab)}
                  style={{
                    padding: "8px 14px",
                    fontSize: 12,
                    fontWeight: activeTab === tab ? 600 : 400,
                    color: activeTab === tab ? "var(--text)" : "var(--text-2)",
                    borderBottom: `2px solid ${activeTab === tab ? "var(--accent)" : "transparent"}`,
                    textTransform: "capitalize",
                    marginBottom: -1,
                  }}
                >
                  {tab}
                </button>
              ))}
            </div>

            {/* Tab content */}
            <div style={{ flex: 1, overflowY: "auto", padding: 20 }}>
              {activeTab === "summary" && (
                <SummaryTab
                  project={activeProject}
                  generatingSummary={generatingSummary}
                  onGenerateSummary={handleGenerateSummary}
                  onUpdatePath={(path) => updateProject(activeProject.id, { path })}
                />
              )}
              {activeTab === "tasks" && (
                <TasksTab
                  tasks={tasks}
                  newTaskTitle={newTaskTitle}
                  addingTask={addingTask}
                  onNewTaskTitleChange={setNewTaskTitle}
                  onAddTask={handleAddTask}
                  onUpdateStatus={updateTaskStatus}
                  onDeleteTask={deleteTask}
                />
              )}
              {activeTab === "conversations" && (
                <ConversationsTab conversations={projectConvs} />
              )}
            </div>
          </>
        ) : (
          <EmptyState />
        )}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Project list sidebar
// ---------------------------------------------------------------------------

interface ProjectListProps {
  projects: Project[];
  activeId: string | null;
  showNewProject: boolean;
  newProjectName: string;
  creating: boolean;
  onSelect: (id: string) => void;
  onDelete: (id: string) => void;
  onNewProjectNameChange: (v: string) => void;
  onShowNewProject: () => void;
  onCancelNew: () => void;
  onCreateProject: () => void;
}

function ProjectList({
  projects,
  activeId,
  showNewProject,
  newProjectName,
  creating,
  onSelect,
  onDelete,
  onNewProjectNameChange,
  onShowNewProject,
  onCancelNew,
  onCreateProject,
}: ProjectListProps) {
  return (
    <div
      style={{
        width: 220,
        minWidth: 220,
        borderRight: "1px solid var(--border)",
        backgroundColor: "var(--surface)",
        display: "flex",
        flexDirection: "column",
        overflow: "hidden",
        flexShrink: 0,
      }}
    >
      <div style={{ padding: "10px 10px 6px" }}>
        <button
          onClick={onShowNewProject}
          style={{
            display: "flex",
            alignItems: "center",
            gap: 7,
            width: "100%",
            padding: "7px 10px",
            borderRadius: 5,
            backgroundColor: "var(--accent)",
            color: "#fff",
            fontSize: 12,
            fontWeight: 500,
            justifyContent: "center",
          }}
        >
          <Plus size={14} strokeWidth={1.5} />
          New project
        </button>
      </div>

      {showNewProject && (
        <div style={{ padding: "6px 10px", display: "flex", flexDirection: "column", gap: 6 }}>
          <input
            autoFocus
            placeholder="Project name"
            value={newProjectName}
            onChange={(e) => onNewProjectNameChange(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") onCreateProject();
              if (e.key === "Escape") onCancelNew();
            }}
            style={{
              fontSize: 12,
              padding: "5px 8px",
              borderRadius: 4,
              border: "1px solid var(--border)",
              backgroundColor: "var(--bg)",
              color: "var(--text)",
              outline: "none",
              width: "100%",
            }}
          />
          <div style={{ display: "flex", gap: 6 }}>
            <button
              onClick={onCreateProject}
              disabled={creating || !newProjectName.trim()}
              style={{
                flex: 1,
                fontSize: 11,
                padding: "4px 0",
                borderRadius: 3,
                backgroundColor: "var(--accent)",
                color: "#fff",
                opacity: creating || !newProjectName.trim() ? 0.6 : 1,
              }}
            >
              {creating ? "Creating..." : "Create"}
            </button>
            <button
              onClick={onCancelNew}
              style={{
                flex: 1,
                fontSize: 11,
                padding: "4px 0",
                borderRadius: 3,
                backgroundColor: "var(--surface-2)",
                color: "var(--text-2)",
              }}
            >
              Cancel
            </button>
          </div>
        </div>
      )}

      <div style={{ flex: 1, overflowY: "auto", padding: "0 6px 8px" }}>
        {projects.length === 0 ? (
          <p style={{ fontSize: 12, color: "var(--text-3)", textAlign: "center", marginTop: 16, padding: "0 8px" }}>
            No projects yet
          </p>
        ) : (
          projects.map((p) => (
            <ProjectRow
              key={p.id}
              project={p}
              active={p.id === activeId}
              onSelect={() => onSelect(p.id)}
              onDelete={() => onDelete(p.id)}
            />
          ))
        )}
      </div>
    </div>
  );
}

function ProjectRow({
  project,
  active,
  onSelect,
  onDelete,
}: {
  project: Project;
  active: boolean;
  onSelect: () => void;
  onDelete: () => void;
}) {
  const [hovered, setHovered] = useState(false);

  return (
    <div
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      style={{
        display: "flex",
        alignItems: "center",
        padding: "2px 4px",
        borderRadius: 4,
        backgroundColor: active ? "var(--surface-2)" : "transparent",
        marginBottom: 1,
      }}
    >
      <button
        onClick={onSelect}
        style={{
          flex: 1,
          textAlign: "left",
          fontSize: 12,
          color: active ? "var(--text)" : "var(--text-2)",
          padding: "6px 6px",
          borderRadius: 3,
          whiteSpace: "nowrap",
          overflow: "hidden",
          textOverflow: "ellipsis",
        }}
      >
        {project.name}
      </button>
      {(hovered || active) && (
        <button
          onClick={(e) => { e.stopPropagation(); onDelete(); }}
          style={{
            display: "flex",
            alignItems: "center",
            justifyContent: "center",
            width: 20,
            height: 20,
            borderRadius: 3,
            color: "var(--text-3)",
            flexShrink: 0,
          }}
        >
          <Trash2 size={12} strokeWidth={1.5} />
        </button>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Summary tab
// ---------------------------------------------------------------------------

function SummaryTab({
  project,
  generatingSummary,
  onGenerateSummary,
  onUpdatePath,
}: {
  project: Project;
  generatingSummary: boolean;
  onGenerateSummary: () => void;
  onUpdatePath: (path: string) => void;
}) {
  const [editingPath, setEditingPath] = useState(false);
  const [pathValue, setPathValue] = useState(project.path ?? "");

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 20, maxWidth: 600 }}>
      {/* Summary */}
      <div>
        <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 10 }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: "var(--text-3)", textTransform: "uppercase", letterSpacing: "0.06em" }}>
            Summary
          </span>
          <button
            onClick={onGenerateSummary}
            disabled={generatingSummary}
            style={{
              display: "flex",
              alignItems: "center",
              gap: 5,
              fontSize: 11,
              color: "var(--accent)",
              opacity: generatingSummary ? 0.6 : 1,
            }}
          >
            {generatingSummary ? (
              <Loader size={12} strokeWidth={1.5} style={{ animation: "spin 600ms linear infinite" }} />
            ) : (
              <RefreshCw size={12} strokeWidth={1.5} />
            )}
            {generatingSummary ? "Generating..." : "Regenerate"}
          </button>
        </div>
        {project.summary ? (
          <p style={{ fontSize: 13, color: "var(--text-2)", lineHeight: 1.6 }}>
            {project.summary}
          </p>
        ) : (
          <p style={{ fontSize: 12, color: "var(--text-3)", fontStyle: "italic" }}>
            No summary yet. Click Regenerate to generate one using the Speed model.
          </p>
        )}
      </div>

      {/* Path */}
      <div>
        <span style={{ fontSize: 11, fontWeight: 600, color: "var(--text-3)", textTransform: "uppercase", letterSpacing: "0.06em", display: "block", marginBottom: 8 }}>
          Project path
        </span>
        {editingPath ? (
          <div style={{ display: "flex", gap: 8 }}>
            <input
              autoFocus
              value={pathValue}
              onChange={(e) => setPathValue(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") { onUpdatePath(pathValue); setEditingPath(false); }
                if (e.key === "Escape") { setPathValue(project.path ?? ""); setEditingPath(false); }
              }}
              style={{
                flex: 1,
                fontSize: 12,
                padding: "5px 8px",
                borderRadius: 4,
                border: "1px solid var(--border)",
                backgroundColor: "var(--bg)",
                color: "var(--text)",
                fontFamily: "monospace",
                outline: "none",
              }}
            />
            <button
              onClick={() => { onUpdatePath(pathValue); setEditingPath(false); }}
              style={{ fontSize: 11, padding: "5px 12px", borderRadius: 4, backgroundColor: "var(--accent)", color: "#fff" }}
            >
              Save
            </button>
          </div>
        ) : (
          <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <span style={{ fontSize: 12, color: "var(--text-2)", fontFamily: "monospace", flex: 1 }}>
              {project.path || "Not set"}
            </span>
            <button
              onClick={() => setEditingPath(true)}
              style={{ fontSize: 11, color: "var(--accent)" }}
            >
              {project.path ? "Change" : "Set path"}
            </button>
          </div>
        )}
      </div>

      {/* Meta */}
      <div style={{ display: "flex", gap: 24 }}>
        <div>
          <span style={{ fontSize: 11, color: "var(--text-3)" }}>Created</span>
          <p style={{ fontSize: 12, color: "var(--text-2)", marginTop: 2 }}>
            {new Date(project.created_at).toLocaleDateString()}
          </p>
        </div>
        <div>
          <span style={{ fontSize: 11, color: "var(--text-3)" }}>Last updated</span>
          <p style={{ fontSize: 12, color: "var(--text-2)", marginTop: 2 }}>
            {new Date(project.updated_at).toLocaleDateString()}
          </p>
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Tasks tab
// ---------------------------------------------------------------------------

const STATUS_LABELS: Record<Task["status"], string> = {
  open: "Open",
  in_progress: "In Progress",
  done: "Done",
  cancelled: "Cancelled",
};

const STATUS_ORDER: Task["status"][] = ["open", "in_progress", "done", "cancelled"];

function TasksTab({
  tasks,
  newTaskTitle,
  addingTask,
  onNewTaskTitleChange,
  onAddTask,
  onUpdateStatus,
  onDeleteTask,
}: {
  tasks: Task[];
  newTaskTitle: string;
  addingTask: boolean;
  onNewTaskTitleChange: (v: string) => void;
  onAddTask: () => void;
  onUpdateStatus: (id: string, status: Task["status"]) => void;
  onDeleteTask: (id: string) => void;
}) {
  const [statusFilter, setStatusFilter] = useState<Task["status"] | "all">("all");

  const filtered = statusFilter === "all"
    ? tasks
    : tasks.filter((t) => t.status === statusFilter);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16, maxWidth: 600 }}>
      {/* Add task */}
      <div style={{ display: "flex", gap: 8 }}>
        <input
          placeholder="Add a task..."
          value={newTaskTitle}
          onChange={(e) => onNewTaskTitleChange(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") onAddTask(); }}
          style={{
            flex: 1,
            fontSize: 12,
            padding: "6px 10px",
            borderRadius: 4,
            border: "1px solid var(--border)",
            backgroundColor: "var(--bg)",
            color: "var(--text)",
            outline: "none",
          }}
        />
        <button
          onClick={onAddTask}
          disabled={addingTask || !newTaskTitle.trim()}
          style={{
            fontSize: 12,
            padding: "6px 14px",
            borderRadius: 4,
            backgroundColor: "var(--accent)",
            color: "#fff",
            opacity: addingTask || !newTaskTitle.trim() ? 0.6 : 1,
          }}
        >
          Add
        </button>
      </div>

      {/* Status filter */}
      <div style={{ display: "flex", gap: 6 }}>
        {(["all", ...STATUS_ORDER] as const).map((s) => (
          <button
            key={s}
            onClick={() => setStatusFilter(s)}
            style={{
              fontSize: 11,
              padding: "3px 10px",
              borderRadius: 3,
              backgroundColor: statusFilter === s ? "var(--surface-2)" : "transparent",
              color: statusFilter === s ? "var(--text)" : "var(--text-3)",
              border: `1px solid ${statusFilter === s ? "var(--border)" : "transparent"}`,
            }}
          >
            {s === "all" ? "All" : STATUS_LABELS[s]}
          </button>
        ))}
      </div>

      {/* Task list */}
      {filtered.length === 0 ? (
        <p style={{ fontSize: 12, color: "var(--text-3)" }}>No tasks yet.</p>
      ) : (
        <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          {filtered.map((task) => (
            <TaskRow
              key={task.id}
              task={task}
              onUpdateStatus={onUpdateStatus}
              onDelete={onDeleteTask}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function TaskRow({
  task,
  onUpdateStatus,
  onDelete,
}: {
  task: Task;
  onUpdateStatus: (id: string, status: Task["status"]) => void;
  onDelete: (id: string) => void;
}) {
  const [hovered, setHovered] = useState(false);
  const done = task.status === "done";
  const inProgress = task.status === "in_progress";

  return (
    <div
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      style={{
        display: "flex",
        alignItems: "center",
        gap: 10,
        padding: "8px 10px",
        borderRadius: 5,
        backgroundColor: "var(--surface)",
        border: "1px solid var(--border-subtle)",
      }}
    >
      {/* Toggle done */}
      <button
        onClick={() => onUpdateStatus(task.id, done ? "open" : "done")}
        style={{ color: done ? "var(--success)" : "var(--text-3)", flexShrink: 0 }}
      >
        {done ? (
          <CheckSquare size={16} strokeWidth={1.5} />
        ) : (
          <Square size={16} strokeWidth={1.5} />
        )}
      </button>

      <span
        style={{
          flex: 1,
          fontSize: 13,
          color: done ? "var(--text-3)" : "var(--text-2)",
          textDecoration: done ? "line-through" : "none",
        }}
      >
        {task.title}
      </span>

      {inProgress && (
        <span style={{ fontSize: 10, color: "var(--warning)", fontWeight: 600, textTransform: "uppercase" }}>
          Active
        </span>
      )}

      {task.due_at && (
        <span style={{ display: "flex", alignItems: "center", gap: 3, fontSize: 11, color: "var(--text-3)" }}>
          <Clock size={11} strokeWidth={1.5} />
          {new Date(task.due_at).toLocaleDateString()}
        </span>
      )}

      {/* Status cycle: open -> in_progress */}
      {hovered && !done && (
        <button
          onClick={() =>
            onUpdateStatus(task.id, inProgress ? "open" : "in_progress")
          }
          title={inProgress ? "Mark as open" : "Mark as in progress"}
          style={{ fontSize: 10, color: "var(--accent)", flexShrink: 0 }}
        >
          {inProgress ? "Pause" : "Start"}
        </button>
      )}

      {hovered && (
        <button
          onClick={() => onDelete(task.id)}
          style={{ color: "var(--text-3)", flexShrink: 0 }}
        >
          <Trash2 size={13} strokeWidth={1.5} />
        </button>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Conversations tab
// ---------------------------------------------------------------------------

function ConversationsTab({ conversations }: { conversations: Conversation[] }) {
  if (conversations.length === 0) {
    return (
      <p style={{ fontSize: 12, color: "var(--text-3)" }}>
        No conversations linked to this project yet. Use the project selector in the chat header to assign conversations.
      </p>
    );
  }
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 4, maxWidth: 600 }}>
      {conversations.map((c) => (
        <div
          key={c.id}
          style={{
            display: "flex",
            alignItems: "center",
            gap: 10,
            padding: "8px 12px",
            borderRadius: 5,
            backgroundColor: "var(--surface)",
            border: "1px solid var(--border-subtle)",
          }}
        >
          <span style={{ flex: 1, fontSize: 13, color: "var(--text-2)" }}>{c.title}</span>
          <span style={{ fontSize: 11, color: "var(--text-3)" }}>
            {new Date(c.updated_at).toLocaleDateString()}
          </span>
          <ChevronRight size={14} strokeWidth={1.5} color="var(--text-3)" />
        </div>
      ))}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Empty state
// ---------------------------------------------------------------------------

function EmptyState() {
  return (
    <div
      style={{
        flex: 1,
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
        gap: 12,
        color: "var(--text-3)",
      }}
    >
      <FolderOpen size={36} strokeWidth={1} />
      <p style={{ fontSize: 13, color: "var(--text-3)" }}>Select or create a project</p>
    </div>
  );
}
