import { beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { invoke } from "@tauri-apps/api/core";
import { KnowledgePage } from "../components/KnowledgePage";
import { TasksPage } from "../components/TasksPage";
import { FirstRunSetup } from "../components/FirstRunSetup";
import { MemoryPage } from "../components/MemoryPage";
import { SettingsPage } from "../components/SettingsPage";
import { Project } from "../hooks/useProjects";

const project: Project = {
  id: "project-a",
  name: "Research",
  path: null,
  summary: null,
  created_at: "2026-01-01",
  updated_at: "2026-01-01",
};
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(invoke).mockResolvedValue([]);
});

describe("supporting page recovery", () => {
  it("assigns a Knowledge folder explicitly and limits search to the selected project", async () => {
    const source = {
      id: "source-a", path: "fixture", name: "Notes", file_count: 1,
      chunk_count: 2, status: "ready", created_at: "2026-01-01", project_id: null,
    };
    vi.mocked(invoke).mockImplementation(async (command) => {
      if (command === "list_projects") return [project];
      if (command === "list_knowledge_sources") return [source];
      if (command === "assign_knowledge_project") return [{ ...source, project_id: project.id }];
      return [];
    });
    render(<KnowledgePage embedModel="embed" />);
    const assignment = await screen.findByRole("combobox", { name: "Project for Notes" });
    await waitFor(() => expect(screen.getAllByRole("option", { name: "Research" })).toHaveLength(2));
    fireEvent.change(assignment, { target: { value: project.id } });
    await waitFor(() => expect(invoke).toHaveBeenCalledWith("assign_knowledge_project", {
      sourceId: "source-a", projectId: project.id,
    }));
    fireEvent.change(screen.getByRole("combobox", { name: "Knowledge project" }), {
      target: { value: project.id },
    });
    fireEvent.change(screen.getByRole("textbox", { name: "Search your files" }), {
      target: { value: "deadline" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Search" }));
    await waitFor(() => expect(invoke).toHaveBeenCalledWith("search_knowledge", {
      query: "deadline", embedModel: "embed", limit: 5, projectId: project.id,
    }));
  });

  it("preserves an unavailable saved search provider until the user chooses another", async () => {
    vi.mocked(invoke).mockResolvedValue({
      provider: "brave",
      privacy_mode: "strict",
      enabled: true,
      searxng_url: "http://localhost:8080",
    });
    render(
      <SettingsPage
        ollamaStatus={null}
        speedModel="speed"
        balancedModel="balanced"
        onSpeedModelChange={vi.fn()}
        onBalancedModelChange={vi.fn()}
        hydeEnabled={false}
        onHydeToggle={vi.fn()}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "Web search" }));
    const provider = (await screen.findByRole("combobox", {
      name: "Search provider",
    })) as HTMLSelectElement;
    expect(provider.value).toBe("brave");
    expect(
      (
        screen.getByRole("button", {
          name: "Save changes",
        }) as HTMLButtonElement
      ).disabled,
    ).toBe(true);
    expect(
      screen.queryByRole("textbox", { name: "Search provider API key" }),
    ).toBeNull();
    expect(
      vi
        .mocked(invoke)
        .mock.calls.some(([command]) => command === "update_search_config"),
    ).toBe(false);
    fireEvent.change(provider, { target: { value: "duckduckgo" } });
    expect(
      (
        screen.getByRole("button", {
          name: "Save changes",
        }) as HTMLButtonElement
      ).disabled,
    ).toBe(false);
  });

  it("unlocks Knowledge search after a folder moves and ignores its pending old result", async () => {
    const source = {
      id: "source-a", path: "fixture", name: "Notes", file_count: 1,
      chunk_count: 2, status: "ready", created_at: "2026-01-01", project_id: null,
    };
    let finishSearch!: (value: unknown[]) => void;
    const pending = new Promise<unknown[]>((resolve) => { finishSearch = resolve; });
    vi.mocked(invoke).mockImplementation(async (command) => {
      if (command === "list_projects") return [project];
      if (command === "list_knowledge_sources") return [source];
      if (command === "assign_knowledge_project") return [{ ...source, project_id: project.id }];
      if (command === "search_knowledge") return pending;
      return [];
    });
    render(<KnowledgePage embedModel="embed" />);
    const input = await screen.findByRole("textbox", { name: "Search your files" });
    fireEvent.change(input, { target: { value: "deadline" } });
    fireEvent.click(screen.getByRole("button", { name: "Search" }));
    expect((screen.getByRole("button", { name: "Searching…" }) as HTMLButtonElement).disabled).toBe(true);
    await waitFor(() => expect(screen.getAllByRole("option", { name: "Research" })).toHaveLength(2));
    fireEvent.change(screen.getByRole("combobox", { name: "Project for Notes" }), {
      target: { value: project.id },
    });
    await waitFor(() => expect((screen.getByRole("button", { name: "Search" }) as HTMLButtonElement).disabled).toBe(false));
    await act(async () => {
      finishSearch([{ id: "old", source_id: source.id, file_path: "old.txt",
        chunk_index: 0, content: "Stale scope result" }]);
      await pending;
    });
    expect(screen.queryByText("Stale scope result")).toBeNull();
    expect((screen.getByRole("combobox", { name: "Knowledge project" }) as HTMLSelectElement).disabled).toBe(false);
  });

  it("loads and saves the configured SearxNG address", async () => {
    const config = {
      provider: "searxng",
      privacy_mode: "standard",
      enabled: false,
      searxng_url: "http://localhost:8090",
    };
    vi.mocked(invoke).mockImplementation(async (command) => {
      if (command === "get_search_config") return config;
      if (command === "update_search_config")
        return { ...config, searxng_url: "http://localhost:9090" };
      return [];
    });
    render(
      <SettingsPage
        ollamaStatus={null}
        speedModel="speed"
        balancedModel="balanced"
        onSpeedModelChange={vi.fn()}
        onBalancedModelChange={vi.fn()}
        hydeEnabled={false}
        onHydeToggle={vi.fn()}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "Web search" }));
    const address = (await screen.findByRole("textbox", {
      name: "SearxNG address",
    })) as HTMLInputElement;
    expect(address.value).toBe("http://localhost:8090");
    expect(
      screen.queryByRole("switch", { name: "Enable web search" }),
    ).toBeNull();
    expect(
      screen.queryByRole("combobox", { name: "Search privacy mode" }),
    ).toBeNull();
    fireEvent.change(address, { target: { value: "http://localhost:9090" } });
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
    await screen.findByRole("button", { name: "Saved" });
    expect(invoke).toHaveBeenCalledWith("update_search_config", {
      provider: "searxng",
      privacyMode: "standard",
      enabled: false,
      apiKey: null,
      searxngUrl: "http://localhost:9090",
    });
  });

  it("shows knowledge search results only after submitting a query", async () => {
    vi.mocked(invoke).mockImplementation(async (command) => {
      if (command === "list_knowledge_sources")
        return [
          {
            id: "source",
            path: "/notes",
            name: "Notes",
            status: "ready",
            file_count: 1,
            chunk_count: 2,
            created_at: "2026-01-01",
          },
        ];
      return [];
    });
    render(<KnowledgePage embedModel="embed" />);
    const input = await screen.findByRole("textbox", {
      name: "Search your files",
    });
    fireEvent.change(input, { target: { value: "launch plan" } });
    expect(screen.queryByText(/No matching passages/)).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Search" }));
    expect(
      await screen.findByText("No matching passages for “launch plan”"),
    ).toBeDefined();
    expect(invoke).toHaveBeenCalledWith("search_knowledge", {
      query: "launch plan",
      embedModel: "embed",
      limit: 5,
      projectId: null,
    });
  });

  it("retains the task draft and exposes a failed save", async () => {
    vi.mocked(invoke).mockImplementation(async (command) => {
      if (command === "create_task") throw new Error("offline");
      return [];
    });
    render(<TasksPage projects={[project]} />);
    await screen.findByText("A clear place for your next steps");
    const input = screen.getByRole("textbox", {
      name: "Task title",
    }) as HTMLInputElement;
    fireEvent.change(input, { target: { value: "Review sources" } });
    fireEvent.click(screen.getByRole("button", { name: "Add task" }));
    expect(await screen.findByRole("alert")).toHaveProperty(
      "textContent",
      "Couldn’t create the task. Your draft is still here; try again.",
    );
    expect(input.value).toBe("Review sources");
    expect(invoke).toHaveBeenCalledWith("create_task", {
      projectId: "project-a",
      title: "Review sources",
      dueAt: null,
    });
  });

  it("keeps onboarding open after failed downloads so they can be retried", async () => {
    const complete = vi.fn();
    vi.mocked(invoke).mockImplementation(async (command) => {
      if (command === "pull_ollama_model") throw new Error("offline");
      return [];
    });
    render(<FirstRunSetup onComplete={complete} />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Download 3 models" }),
    );
    expect(
      await screen.findByRole("button", { name: "Retry downloads" }),
    ).toBeDefined();
    expect(complete).not.toHaveBeenCalled();
    expect(screen.getAllByRole("alert")).toHaveLength(3);
  });

  it("opens the downloader from settings even when models are already installed", async () => {
    const complete = vi.fn();
    vi.mocked(invoke).mockResolvedValue(["nomic-embed-text:latest"]);
    render(<FirstRunSetup forceShow onComplete={complete} />);
    expect(
      await screen.findByRole("heading", { name: "Download local models" }),
    ).toBeDefined();
    expect(
      screen.getByRole("button", { name: "Download 3 models" }),
    ).toBeDefined();
    expect(complete).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(complete).toHaveBeenCalledOnce();
  });

  it("reports a failed memory removal without hiding the memory", async () => {
    const remove = vi.fn().mockRejectedValue(new Error("offline"));
    render(
      <MemoryPage
        memories={[
          {
            id: "memory-a",
            content: "Prefers concise answers",
            created_at: "2026-01-01",
          },
        ]}
        links={[]}
        onDelete={remove}
        onDeleteLink={vi.fn()}
      />,
    );
    fireEvent.click(
      screen.getByRole("button", {
        name: "Delete memory: Prefers concise answers",
      }),
    );
    await waitFor(() =>
      expect(screen.getByRole("alert").textContent).toContain(
        "Couldn’t remove",
      ),
    );
    expect(screen.getByText("Prefers concise answers")).toBeDefined();
  });
});
