import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { invoke } from "@tauri-apps/api/core";
import { TaskPrerequisites } from "../components/TaskPrerequisites";
import type { Task } from "../hooks/useProjects";

beforeEach(() => vi.clearAllMocks());
afterEach(cleanup);
const task = (id: string, title: string, project_id = "p1"): Task => ({ id, project_id, title, status: "open", due_at: null, created_at: "2026-11-17", updated_at: "2026-11-17" });
const tasks = [task("t1", "Ship release"), task("t2", "Write notes")];

it("resolves task IDs to current titles", async () => {
  vi.mocked(invoke).mockResolvedValue({ revision: 3, dependencies: [{ task_id: "t1", requires_id: "t2" }, { task_id: "t1", requires_id: "gone" }] });
  render(<TaskPrerequisites projectId="p1" tasks={tasks} tasksLoading={false} />);
  expect(screen.getByRole("status").textContent).toContain("Loading");
  expect(await screen.findByText("Write notes")).not.toBeNull();
  expect(screen.getAllByText("Ship release")).toHaveLength(2);
  expect(screen.getByText("Task no longer in this project").getAttribute("title")).toBe("gone");
  expect(invoke).toHaveBeenCalledWith("agent_hub_snapshot", { projectId: "p1" });
});

it("shows an empty state", async () => {
  vi.mocked(invoke).mockResolvedValue({ revision: 1, dependencies: [] });
  render(<TaskPrerequisites projectId="p1" tasks={tasks} tasksLoading={false} />);
  expect(await screen.findByText("No task prerequisites recorded for this project.")).not.toBeNull();
});

it("reports an unavailable service and retries", async () => {
  vi.mocked(invoke).mockRejectedValueOnce(new Error("not found")).mockResolvedValue({ revision: 1, dependencies: [] });
  render(<TaskPrerequisites projectId="p1" tasks={tasks} tasksLoading={false} />);
  expect((await screen.findByRole("alert")).textContent).toContain("unavailable");
  fireEvent.click(screen.getByRole("button", { name: "Retry" }));
  expect(await screen.findByText("No task prerequisites recorded for this project.")).not.toBeNull();
});

it.each([{ revision: 1 }, { revision: 1, dependencies: [{ task_id: "t1" }] }])("treats a malformed snapshot as unavailable", async (snapshot) => {
  vi.mocked(invoke).mockResolvedValue(snapshot);
  render(<TaskPrerequisites projectId="p1" tasks={tasks} tasksLoading={false} />);
  expect(await screen.findByRole("alert")).not.toBeNull();
});

it("does not show the previous project's prerequisites after switching", async () => {
  let resolveOld!: (value: unknown) => void;
  vi.mocked(invoke).mockImplementation(async (_command, args) => {
    if ((args as { projectId: string }).projectId === "p1") return new Promise(resolve => { resolveOld = resolve; });
    return { revision: 1, dependencies: [] };
  });
  const view = render(<TaskPrerequisites projectId="p1" tasks={tasks} tasksLoading={false} />);
  view.rerender(<TaskPrerequisites projectId="p2" tasks={[task("t9", "Other", "p2")]} tasksLoading={false} />);
  await screen.findByText("No task prerequisites recorded for this project.");
  await act(async () => resolveOld({ revision: 2, dependencies: [{ task_id: "t1", requires_id: "t2" }] }));
  expect(screen.queryByText("Ship release")).toBeNull();
  expect(screen.getByText("No task prerequisites recorded for this project.")).not.toBeNull();
});

it("waits for the project's tasks before resolving titles", async () => {
  vi.mocked(invoke).mockResolvedValue({ revision: 1, dependencies: [{ task_id: "t1", requires_id: "t2" }] });
  const view = render(<TaskPrerequisites projectId="p1" tasks={[]} tasksLoading />);
  await act(async () => {});
  expect(screen.getByRole("status").textContent).toContain("Loading");
  view.rerender(<TaskPrerequisites projectId="p1" tasks={tasks} tasksLoading={false} />);
  expect(screen.getByText("Write notes")).not.toBeNull();
});
