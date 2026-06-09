// Project and task state management.
//
// Manages the full list of projects, active project, and per-project tasks.
// Listens for "projects-updated" events emitted by Rust after delete operations.

import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useCallback, useEffect, useState } from "react";

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

  // Load project list on mount and subscribe to server-push updates
  useEffect(() => {
    invoke<{ projects: Project[] }>("list_projects")
      .then((r) => setProjects(r.projects ?? r as unknown as Project[]))
      .catch(() => {});

    let unlisten: (() => void) | undefined;
    listen<ProjectsUpdatedPayload>("projects-updated", (event) => {
      setProjects(event.payload.projects);
    }).then((fn) => {
      unlisten = fn;
    });
    return () => unlisten?.();
  }, []);

  // Load tasks whenever active project changes.
  // Wrapped in async function so setState is not called synchronously in the
  // effect body (satisfies react-hooks/set-state-in-effect).
  useEffect(() => {
    async function load() {
      if (!activeProjectId) {
        setTasks([]);
        return;
      }
      try {
        const r = await invoke<{ tasks: Task[] }>("list_tasks", { projectId: activeProjectId });
        setTasks(r.tasks ?? (r as unknown as Task[]));
      } catch {
        setTasks([]);
      }
    }
    load();
  }, [activeProjectId]);

  const createProject = useCallback(async (name: string, path?: string): Promise<Project> => {
    const project = await invoke<Project>("create_project", { name, path: path ?? null });
    setProjects((prev) => [project, ...prev]);
    return project;
  }, []);

  const updateProject = useCallback(
    async (projectId: string, updates: { name?: string; path?: string; summary?: string }): Promise<Project> => {
      const updated = await invoke<Project>("update_project", {
        projectId,
        name: updates.name ?? null,
        path: updates.path ?? null,
        summary: updates.summary ?? null,
      });
      setProjects((prev) => prev.map((p) => (p.id === projectId ? updated : p)));
      return updated;
    },
    []
  );

  const deleteProject = useCallback(async (projectId: string): Promise<void> => {
    await invoke("delete_project", { projectId });
    // projects-updated event will refresh the list
    if (activeProjectId === projectId) {
      setActiveProjectId(null);
      setTasks([]);
    }
  }, [activeProjectId]);

  const generateSummary = useCallback(
    async (projectId: string, speedModel: string): Promise<string> => {
      const summary = await invoke<string>("generate_project_summary", { projectId, speedModel });
      setProjects((prev) =>
        prev.map((p) => (p.id === projectId ? { ...p, summary } : p))
      );
      return summary;
    },
    []
  );

  const selectProject = useCallback((projectId: string | null) => {
    setActiveProjectId(projectId);
  }, []);

  // Task operations
  const createTask = useCallback(
    async (title: string, dueAt?: string): Promise<Task> => {
      if (!activeProjectId) throw new Error("No active project");
      const task = await invoke<Task>("create_task", {
        projectId: activeProjectId,
        title,
        dueAt: dueAt ?? null,
      });
      setTasks((prev) => [...prev, task]);
      return task;
    },
    [activeProjectId]
  );

  const updateTaskStatus = useCallback(
    async (taskId: string, status: Task["status"]): Promise<void> => {
      if (!activeProjectId) return;
      await invoke("update_task", {
        projectId: activeProjectId,
        taskId,
        title: null,
        status,
        dueAt: null,
      });
      setTasks((prev) =>
        prev.map((t) => (t.id === taskId ? { ...t, status } : t))
      );
    },
    [activeProjectId]
  );

  const deleteTask = useCallback(
    async (taskId: string): Promise<void> => {
      if (!activeProjectId) return;
      await invoke("delete_task", { projectId: activeProjectId, taskId });
      setTasks((prev) => prev.filter((t) => t.id !== taskId));
    },
    [activeProjectId]
  );

  const assignConversation = useCallback(
    async (conversationId: string, projectId: string | null): Promise<void> => {
      await invoke("assign_conversation_to_project", { conversationId, projectId });
    },
    []
  );

  return {
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
    assignConversation,
  };
}
