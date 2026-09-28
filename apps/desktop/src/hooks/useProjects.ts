// Project and task state management.
//
// Manages the full list of projects, active project, and per-project tasks.
// Listens for "projects-updated" events emitted by Rust after delete operations.

import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useCallback, useEffect, useRef, useState } from "react";

export interface Project {
  id: string;
  name: string;
  path: string | null;
  summary: string | null;
  created_at: string;
  updated_at: string;
}

export interface Task {
  id: string;
  project_id: string;
  title: string;
  status: "open" | "in_progress" | "done" | "cancelled";
  due_at: string | null;
  created_at: string;
  updated_at: string;
}

interface ProjectsUpdatedPayload {
  projects: Project[];
}

export function useProjects() {
  const [projects, setProjects] = useState<Project[]>([]);
  const [activeProjectId, setActiveProjectId] = useState<string | null>(null);
  const [tasks, setTasks] = useState<Task[]>([]);
  const [loading, setLoading] = useState(true);
  const [tasksLoading, setTasksLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const generation = useRef(0);
  const projectRevision = useRef(0);
  const taskRevision = useRef(0);
  const selected = useRef<string | null>(null);
  const refreshTasks = useCallback(async () => {
    const projectId = selected.current;
    const epoch = generation.current;
    const version = ++taskRevision.current;
    if (!projectId) {
      setTasks([]);
      setTasksLoading(false);
      return;
    }
    setTasksLoading(true);
    try {
      const result = await invoke<{ tasks?: Task[] } | Task[] | null>(
        "list_tasks",
        { projectId },
      );
      const list = Array.isArray(result) ? result : result?.tasks;
      if (
        epoch === generation.current &&
        version === taskRevision.current &&
        selected.current === projectId
      )
        setTasks(Array.isArray(list) ? list : []);
    } catch {
      if (epoch === generation.current && version === taskRevision.current)
        setError("Couldn't load this project's tasks. Try refreshing.");
    } finally {
      if (epoch === generation.current && version === taskRevision.current)
        setTasksLoading(false);
    }
  }, []);
  const applyProjects = useCallback((list: Project[]) => {
    setProjects(list);
    if (selected.current && !list.some((p) => p.id === selected.current)) {
      selected.current = null;
      taskRevision.current++;
      setActiveProjectId(null);
      setTasks([]);
      setTasksLoading(false);
    }
  }, []);
  const refresh = useCallback(async () => {
    const epoch = generation.current;
    const version = ++projectRevision.current;
    setLoading(true);
    setError(null);
    const taskRequest = refreshTasks();
    try {
      const result = await invoke<{ projects?: Project[] } | Project[] | null>(
        "list_projects",
      );
      const list = Array.isArray(result) ? result : result?.projects;
      if (epoch === generation.current && version === projectRevision.current)
        applyProjects(Array.isArray(list) ? list : []);
    } catch {
      if (epoch === generation.current && version === projectRevision.current)
        setError(
          "Couldn't load projects. Refresh when the local service is ready.",
        );
    } finally {
      if (epoch === generation.current && version === projectRevision.current)
        setLoading(false);
    }
    await taskRequest;
  }, [applyProjects, refreshTasks]);
  useEffect(() => {
    let cancelled = false;
    const epoch = ++generation.current;
    const offs: (() => void)[] = [];
    const keep = (off: () => void) => {
      if (cancelled) off();
      else offs.push(off);
    };
    void Promise.allSettled([
      listen("sidecar-ready", () => {
        if (!cancelled) void refresh();
      }).then(keep),
      listen<ProjectsUpdatedPayload>("projects-updated", (event) => {
        if (cancelled) return;
        projectRevision.current++;
        applyProjects(
          Array.isArray(event.payload?.projects) ? event.payload.projects : [],
        );
        setLoading(false);
      }).then(keep),
    ]).then(() => {
      if (!cancelled) void refresh();
    });
    return () => {
      cancelled = true;
      generation.current = epoch + 1;
      offs.forEach((off) => off());
    };
  }, [applyProjects, refresh]);

  const createProject = useCallback(
    async (name: string, path?: string): Promise<Project> => {
      const epoch = generation.current;
      const project = await invoke<Project>("create_project", {
        name,
        path: path ?? null,
      });
      if (epoch === generation.current) {
        projectRevision.current++;
        setProjects((prev) => [
          project,
          ...prev.filter((p) => p.id !== project.id),
        ]);
        setLoading(false);
      }
      return project;
    },
    [],
  );

  const updateProject = useCallback(
    async (
      projectId: string,
      updates: { name?: string; path?: string; summary?: string },
    ): Promise<Project> => {
      const epoch = generation.current;
      const updated = await invoke<Project>("update_project", {
        projectId,
        name: updates.name ?? null,
        path: updates.path ?? null,
        summary: updates.summary ?? null,
      });
      if (epoch === generation.current) {
        projectRevision.current++;
        setProjects((prev) =>
          prev.map((p) => (p.id === projectId ? updated : p)),
        );
        setLoading(false);
      }
      return updated;
    },
    [],
  );

  const deleteProject = useCallback(
    async (projectId: string): Promise<void> => {
      const epoch = generation.current;
      await invoke("delete_project", { projectId });
      if (epoch !== generation.current) return;
      projectRevision.current++;
      setProjects((prev) => prev.filter((p) => p.id !== projectId));
      setLoading(false);
      if (selected.current === projectId) {
        selected.current = null;
        taskRevision.current++;
        setActiveProjectId(null);
        setTasks([]);
        setTasksLoading(false);
      }
    },
    [],
  );

  const generateSummary = useCallback(
    async (projectId: string, speedModel: string): Promise<string> => {
      const epoch = generation.current;
      const summary = await invoke<string>("generate_project_summary", {
        projectId,
        speedModel,
      });
      if (epoch === generation.current) {
        projectRevision.current++;
        setProjects((prev) =>
          prev.map((p) => (p.id === projectId ? { ...p, summary } : p)),
        );
        setLoading(false);
      }
      return summary;
    },
    [],
  );

  const selectProject = useCallback(
    (projectId: string | null) => {
      selected.current = projectId;
      setActiveProjectId(projectId);
      setTasks([]);
      setError(null);
      void refreshTasks();
    },
    [refreshTasks],
  );

  // Task operations
  const createTask = useCallback(
    async (title: string, dueAt?: string): Promise<Task> => {
      const projectId = selected.current;
      const epoch = generation.current;
      if (!projectId) throw new Error("No active project");
      const task = await invoke<Task>("create_task", {
        projectId,
        title,
        dueAt: dueAt ?? null,
      });
      if (epoch === generation.current && selected.current === projectId) {
        taskRevision.current++;
        setTasks((prev) => [...prev.filter((t) => t.id !== task.id), task]);
        void refreshTasks();
      }
      return task;
    },
    [refreshTasks],
  );

  const updateTaskStatus = useCallback(
    async (taskId: string, status: Task["status"]): Promise<void> => {
      const projectId = selected.current;
      const epoch = generation.current;
      if (!projectId) throw new Error("No active project");
      await invoke("update_task", {
        projectId,
        taskId,
        title: null,
        status,
        dueAt: null,
      });
      if (epoch === generation.current && selected.current === projectId) {
        taskRevision.current++;
        setTasks((prev) =>
          prev.map((t) => (t.id === taskId ? { ...t, status } : t)),
        );
        void refreshTasks();
      }
    },
    [refreshTasks],
  );

  const deleteTask = useCallback(
    async (taskId: string): Promise<void> => {
      const projectId = selected.current;
      const epoch = generation.current;
      if (!projectId) throw new Error("No active project");
      await invoke("delete_task", { projectId, taskId });
      if (epoch === generation.current && selected.current === projectId) {
        taskRevision.current++;
        setTasks((prev) => prev.filter((t) => t.id !== taskId));
        void refreshTasks();
      }
    },
    [refreshTasks],
  );

  const assignConversation = useCallback(
    async (conversationId: string, projectId: string | null): Promise<void> => {
      await invoke("assign_conversation_to_project", {
        conversationId,
        projectId,
      });
    },
    [],
  );

  return {
    projects,
    loading,
    tasksLoading,
    error,
    refresh,
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
    assignConversation,
  };
}
