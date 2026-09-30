import { StrictMode } from "react";
import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useMemory } from "../hooks/useMemory";
import { useKnowledge } from "../hooks/useKnowledge";
import { useProjects } from "../hooks/useProjects";

type Handler = (event: { payload: unknown }) => void;
const handlers = new Map<string, Set<Handler>>();
const memory = { id: "m1", content: "Saved context", created_at: "2026-10-10" };
const source = {
  id: "s1",
  name: "Notes",
  path: "C:/Notes",
  file_count: 2,
  chunk_count: 5,
  status: "ready",
  created_at: "2026-10-10",
};
const project = (id: string) => ({
  id,
  name: id,
  path: null,
  summary: null,
  created_at: "2026-10-10",
  updated_at: "2026-10-10",
});
const task = (id: string, projectId: string) => ({
  id,
  project_id: projectId,
  title: id,
  status: "open",
  due_at: null,
  created_at: "2026-10-10",
  updated_at: "2026-10-10",
});
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: Error) => void;
  const promise = new Promise<T>((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
}
const emit = (name: string, payload: unknown = {}) =>
  act(() => {
    handlers.get(name)?.forEach((handler) => handler({ payload }));
  });
beforeEach(() => {
  vi.clearAllMocks();
  handlers.clear();
  vi.mocked(listen).mockImplementation(async (name, handler) => {
    const group = handlers.get(name) ?? new Set<Handler>();
    group.add(handler as Handler);
    handlers.set(name, group);
    return () => {
      group.delete(handler as Handler);
    };
  });
  vi.mocked(invoke).mockImplementation(async (command) => {
    if (command === "list_projects")
      return { projects: [project("a"), project("b")] };
    if (command === "list_tasks") return { tasks: [] };
    if (command === "list_memories") return [memory];
    if (command === "list_knowledge_sources") return [source];
    return [];
  });
});

describe("workspace startup and subscriptions", () => {
  it("reloads all workspace data after the local service becomes ready", async () => {
    let ready = false;
    vi.mocked(invoke).mockImplementation(async (command) => {
      if (!ready) throw new Error("starting");
      if (command === "list_memories") return [memory];
      if (command === "list_knowledge_sources") return [source];
      if (command === "list_projects") return { projects: [project("a")] };
      return [];
    });
    const { result } = renderHook(() => ({
      memory: useMemory(),
      knowledge: useKnowledge(),
      projects: useProjects(),
    }));
    await waitFor(() => expect(result.current.projects.error).toBeTruthy());
    ready = true;
    emit("sidecar-ready");
    await waitFor(() => {
      expect(result.current.memory.memories).toEqual([memory]);
      expect(result.current.knowledge.sources).toEqual([source]);
      expect(result.current.projects.projects).toEqual([project("a")]);
      expect(result.current.memory.error).toBeNull();
      expect(result.current.knowledge.error).toBeNull();
      expect(result.current.projects.error).toBeNull();
    });
  });

  it("cleans up listeners whose registration resolves after StrictMode cleanup", async () => {
    const gate = deferred<void>();
    const releases: ReturnType<typeof vi.fn>[] = [];
    vi.mocked(listen).mockImplementation(async () => {
      await gate.promise;
      const release = vi.fn();
      releases.push(release);
      return release;
    });
    const { unmount } = renderHook(
      () => {
        useMemory();
        useKnowledge();
        useProjects();
      },
      { wrapper: StrictMode },
    );
    await act(async () => gate.resolve());
    expect(releases).toHaveLength(12);
    expect(
      releases.filter((release) => release.mock.calls.length === 1),
    ).toHaveLength(6);
    unmount();
    expect(releases.every((release) => release.mock.calls.length === 1)).toBe(
      true,
    );
  });

  it("does not let an old memory fetch overwrite a newer update event", async () => {
    const oldRead = deferred<unknown>();
    vi.mocked(invoke).mockImplementation(async (command) =>
      command === "list_memories" ? oldRead.promise : [],
    );
    const { result } = renderHook(() => useMemory());
    await waitFor(() => expect(invoke).toHaveBeenCalledWith("list_memories"));
    emit("memories-updated", { memories: [memory] });
    await act(async () => oldRead.resolve([]));
    expect(result.current.memories).toEqual([memory]);
  });
});

describe("confirmed workspace mutations", () => {
  it("keeps a Knowledge source until deletion succeeds and reports a failed deletion", async () => {
    const deletion = deferred<void>();
    vi.mocked(invoke).mockImplementation(async (command) =>
      command === "list_knowledge_sources"
        ? [source]
        : command === "delete_knowledge_source"
          ? deletion.promise
          : [],
    );
    const { result } = renderHook(() => useKnowledge());
    await waitFor(() => expect(result.current.sources).toEqual([source]));
    let operation!: Promise<void>;
    act(() => {
      operation = result.current.deleteSource("s1");
    });
    expect(result.current.sources).toEqual([source]);
    await act(async () => {
      deletion.reject(new Error("service unavailable"));
      await expect(operation).rejects.toThrow("service unavailable");
    });
    expect(result.current.sources).toEqual([source]);
    expect(result.current.error).toMatch(/Couldn't remove/);
  });

  it("does not leave a fake indexing source after indexing is rejected", async () => {
    vi.mocked(invoke).mockImplementation(async (command) => {
      if (command === "pick_folder") return "C:/Notes";
      if (command === "index_knowledge")
        throw new Error("embedding model missing");
      return [];
    });
    const { result } = renderHook(() => useKnowledge());
    await waitFor(() => expect(result.current.refreshing).toBe(false));
    await act(async () => {
      await expect(result.current.addFolder("missing")).rejects.toThrow(
        "embedding model missing",
      );
    });
    expect(result.current.sources).toEqual([]);
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toMatch(/Couldn't add/);
  });

  it("propagates memory deletion failures without removing saved data", async () => {
    vi.mocked(invoke).mockImplementation(async (command) => {
      if (command === "delete_memory") throw new Error("read only");
      return command === "list_memories" ? [memory] : [];
    });
    const { result } = renderHook(() => useMemory());
    await waitFor(() => expect(result.current.memories).toEqual([memory]));
    await act(async () => {
      await expect(result.current.deleteMemory("m1")).rejects.toThrow(
        "read only",
      );
    });
    expect(result.current.memories).toEqual([memory]);
    expect(result.current.error).toMatch(/Couldn't delete/);
  });
});

describe("project navigation races", () => {
  it("ignores a late task list from the previous project", async () => {
    const first = deferred<unknown>();
    vi.mocked(invoke).mockImplementation(async (command, args) => {
      if (command === "list_projects")
        return { projects: [project("a"), project("b")] };
      if (command === "list_tasks")
        return (args as { projectId: string }).projectId === "a"
          ? first.promise
          : { tasks: [task("b-task", "b")] };
      return [];
    });
    const { result } = renderHook(() => useProjects());
    await waitFor(() => expect(result.current.loading).toBe(false));
    act(() => result.current.selectProject("a"));
    act(() => result.current.selectProject("b"));
    await waitFor(() => expect(result.current.tasks[0]?.id).toBe("b-task"));
    await act(async () => first.resolve({ tasks: [task("a-task", "a")] }));
    expect(result.current.activeProjectId).toBe("b");
    expect(result.current.tasks.map((t) => t.id)).toEqual(["b-task"]);
  });

  it("does not clear the new selection when a previous project's deletion completes", async () => {
    const deletion = deferred<void>();
    vi.mocked(invoke).mockImplementation(async (command) => {
      if (command === "list_projects")
        return { projects: [project("a"), project("b")] };
      if (command === "delete_project") return deletion.promise;
      return { tasks: [] };
    });
    const { result } = renderHook(() => useProjects());
    await waitFor(() => expect(result.current.loading).toBe(false));
    act(() => result.current.selectProject("a"));
    let operation!: Promise<void>;
    act(() => {
      operation = result.current.deleteProject("a");
      result.current.selectProject("b");
    });
    await act(async () => {
      deletion.resolve();
      await operation;
    });
    expect(result.current.activeProjectId).toBe("b");
    expect(result.current.projects.map((p) => p.id)).toEqual(["b"]);
  });

  it("keeps a task created for the previous project out of the current project's list", async () => {
    const creation = deferred<unknown>();
    vi.mocked(invoke).mockImplementation(async (command) => {
      if (command === "list_projects")
        return { projects: [project("a"), project("b")] };
      if (command === "create_task") return creation.promise;
      return { tasks: [] };
    });
    const { result } = renderHook(() => useProjects());
    await waitFor(() => expect(result.current.loading).toBe(false));
    act(() => result.current.selectProject("a"));
    let operation!: ReturnType<typeof result.current.createTask>;
    act(() => {
      operation = result.current.createTask("a-task");
      result.current.selectProject("b");
    });
    await act(async () => {
      creation.resolve(task("a-task", "a"));
      await operation;
    });
    expect(result.current.activeProjectId).toBe("b");
    expect(result.current.tasks).toEqual([]);
  });
});
