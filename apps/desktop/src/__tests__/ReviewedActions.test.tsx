import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { invoke } from "@tauri-apps/api/core";
import { TaskActions } from "../components/TaskActions";
import { ProjectDependencies } from "../components/ProjectDependencies";
import { RepairReview, type RepairOperation } from "../components/RepairReview";
import type { Project } from "../hooks/useProjects";

beforeEach(() => vi.clearAllMocks());
afterEach(cleanup);
const project: Project = { id: "p1", name: "Launch", path: "C:/fixture", summary: null, created_at: "2026-10-22", updated_at: "2026-10-22" };
const proposal = { id: "op1", project_id: "p1", project_name: "Launch", state: "proposed", arguments: { title: "Review launch", due_at: "2026-11-18" }, receipt: null };
const receipt = { ...proposal, state: "created", receipt: { task_id: "task1", title: "Review launch", created_at: "2026-10-22" } };
const source = { id: "s1", path: "source.md", content: "Launch November 18", sha256: "abc123", state: "unchanged", notice: null, checked_at: "2026-10-22" };
const link = { id: "link1", source_id: "s1", source_path: "source.md", fact: "Launch November 18", reason: "Announcement date", target_kind: "file", target_id: "draft.md", target_preview: "Launch draft", state: "proposed", target_state: "unchanged", source_state: "unchanged", needs_review: false, impact_reason: null, target_notice: null };
const graph = { root: { path: "C:/fixture" }, sources: [source], links: [link], tasks: [] };

it("shows proposed separately from created and retries a lost receipt with the same operation", async () => {
  let confirmed = false;
  let attempt = 0;
  vi.mocked(invoke).mockImplementation(async command => {
    if (command === "list_task_actions") return { actions: [confirmed ? receipt : proposal] };
    if (++attempt === 1) throw new Error("Reply lost");
    confirmed = true; return receipt;
  });
  render(<TaskActions conversationId="chat1" projectId="p1" isStreaming={false} onOpenTasks={vi.fn()} />);
  fireEvent.click(await screen.findByRole("button", { name: "Create task" }));
  await screen.findByRole("alert");
  expect(screen.queryByText("Task created.")).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Create task" }));
  await screen.findByText("Task created.");
  expect(screen.getByText("Task ID: task1")).not.toBeNull();
  const reviews = vi.mocked(invoke).mock.calls.filter(([name]) => name === "review_task_action");
  expect(reviews).toEqual(Array(2).fill(["review_task_action", { operationId: "op1", conversationId: "chat1", decision: "confirm" }]));
});

it("does not allow creation in a different project but permits dismissal", async () => {
  vi.mocked(invoke).mockResolvedValue({ actions: [proposal] });
  render(<TaskActions conversationId="chat1" projectId="p2" isStreaming={false} onOpenTasks={vi.fn()} />);
  expect((await screen.findByRole("button", { name: "Create task" }) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));
  await waitFor(() => expect(invoke).toHaveBeenCalledWith("review_task_action", { operationId: "op1", conversationId: "chat1", decision: "dismiss" }));
});

it("reloads saved receipts after a new component session without executing again", async () => {
  vi.mocked(invoke).mockResolvedValue({ actions: [{ ...receipt, task_exists: false }] });
  const open = vi.fn();
  render(<TaskActions conversationId="chat1" projectId="p1" isStreaming={false} onOpenTasks={open} />);
  await screen.findByText(/It has since been removed/);
  expect(screen.queryByRole("button", { name: "Create task" })).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "View tasks", hidden: true }));
  expect(open).toHaveBeenCalledOnce();
  expect(vi.mocked(invoke).mock.calls.every(([name]) => name === "list_task_actions")).toBe(true);
});

it("requires folder approval and does not scan on page load", async () => {
  vi.mocked(invoke).mockResolvedValue({ root: null, sources: [], links: [], tasks: [] });
  render(<ProjectDependencies project={project} />);
  const approve = await screen.findByRole("button", { name: "Approve folder for dependency tracking" });
  expect(vi.mocked(invoke).mock.calls).toEqual([["project_graph", { projectId: "p1", operation: "get", payload: null }]]);
  fireEvent.click(approve);
  await waitFor(() => expect(invoke).toHaveBeenCalledWith("project_graph", { projectId: "p1", operation: "approve", payload: null }));
});

it("shows quote and dependent preview before confirmation and exposes changed-source impact", async () => {
  let confirmed = false;
  let scanned = false;
  vi.mocked(invoke).mockImplementation(async (_command, args) => {
    const operation = (args as { operation: string }).operation;
    if (operation === "review") confirmed = true;
    if (operation === "rescan") scanned = true;
    return { ...graph, links: [{ ...link, state: confirmed ? "confirmed" : "proposed", source_state: scanned ? "changed" : "unchanged", needs_review: scanned, impact_reason: scanned ? "Source changed; check announcement" : null }] };
  });
  render(<ProjectDependencies project={project} />);
  const confirm = await screen.findByRole("button", { name: "Confirm dependency" });
  expect(screen.getByText("Announcement date")).not.toBeNull();
  expect(screen.getByText("Launch draft")).not.toBeNull();
  fireEvent.click(confirm);
  await waitFor(() => expect(screen.queryByRole("button", { name: "Confirm dependency" })).toBeNull());
  fireEvent.click(screen.getByRole("button", { name: "Rescan tracked files" }));
  await screen.findByText("Source changed; check announcement");
  expect(screen.getByText("1 item needs review")).not.toBeNull();
  expect(screen.queryByRole("button", { name: /Apply|Repair/ })).toBeNull();
});

it("keeps a stale preview unconfirmed when the service rejects it", async () => {
  vi.mocked(invoke).mockImplementation(async (_command, args) => {
    if ((args as { operation: string }).operation === "review") throw new Error("The source changed after this proposal.");
    return graph;
  });
  render(<ProjectDependencies project={project} />);
  fireEvent.click(await screen.findByRole("button", { name: "Confirm dependency" }));
  expect((await screen.findByRole("alert")).textContent).toContain("source changed");
  expect(screen.getByRole("button", { name: "Confirm dependency" })).not.toBeNull();
});

it("ignores a slow result from the previous project after navigation", async () => {
  let resolveOld!: (value: unknown) => void;
  vi.mocked(invoke).mockImplementation(async (_command, args) => {
    if ((args as { projectId: string }).projectId === "p1") return new Promise(resolve => { resolveOld = resolve; });
    return { root: null, sources: [], links: [], tasks: [] };
  });
  const view = render(<ProjectDependencies key="p1" project={project} />);
  view.rerender(<ProjectDependencies key="p2" project={{ ...project, id: "p2", name: "Other" }} />);
  await screen.findByRole("button", { name: "Approve folder for dependency tracking" });
  await act(async () => resolveOld(graph));
  expect(screen.queryByText("source.md")).toBeNull();
});

it("shows full repair differences and records selective review without applying", async () => {
  const run = vi.fn().mockResolvedValue(true);
  const repair: RepairOperation = { id: "repair1", source_id: "s1", state: "ready", error: null, old_value: "2026-11-18", new_value: "2026-11-25", items: [
    { id: "item1", target_kind: "file", target_id: "draft.md", reason: "Launch date", fact: "Launch 2026-11-18", before_text: "Launch 2026-11-18", after_text: "Launch 2026-11-25", diff: "-Launch 2026-11-18\n+Launch 2026-11-25", validation: "passed", checks: ["Expected replacement verified"], selection: "pending" },
    { id: "item2", target_kind: "file", target_id: "report.pdf", reason: "Launch date", fact: "Launch 2026-11-18", before_text: "", after_text: null, diff: "", validation: "blocked", checks: ["Unsupported format"], selection: "pending" },
  ] };
  render(<RepairReview sources={[]} repairs={[repair]} busy={false} run={run} />);
  expect(screen.getByLabelText("Proposed diff for draft.md").textContent).toContain("+Launch 2026-11-25");
  expect(screen.getByText("Unsupported format")).not.toBeNull();
  const accept = screen.getAllByRole("button", { name: "Accept proposal" }) as HTMLButtonElement[];
  expect(accept[1].disabled).toBe(true);
  fireEvent.click(accept[0]);
  expect(run).toHaveBeenCalledWith("selection", { operation_id: "repair1", item_id: "item1", selection: "accepted" });
  fireEvent.click(screen.getAllByRole("button", { name: "Reject proposal" })[1]);
  expect(run).toHaveBeenCalledWith("selection", { operation_id: "repair1", item_id: "item2", selection: "rejected" });
  expect(screen.queryByRole("button", { name: /Apply/ })).toBeNull();
});

it("retries preparation with the same operation identity until inputs change", () => {
  const run = vi.fn().mockResolvedValue(false);
  render(<RepairReview sources={[{ ...source, state: "changed" }]} repairs={[]} busy={false} run={run} />);
  fireEvent.change(screen.getByLabelText("Changed source"), { target: { value: "s1" } });
  fireEvent.change(screen.getByLabelText("Old value in the captured quote"), { target: { value: "2026-11-18" } });
  fireEvent.change(screen.getByLabelText("New value in the current source"), { target: { value: "2026-11-25" } });
  const prepare = screen.getByRole("button", { name: "Prepare copies for review" });
  fireEvent.click(prepare); fireEvent.click(prepare);
  expect(run.mock.calls[0]).toEqual(run.mock.calls[1]);
  fireEvent.change(screen.getByLabelText("New value in the current source"), { target: { value: "2026-12-01" } });
  fireEvent.click(prepare);
  expect(run.mock.calls[2][1].operation_id).not.toBe(run.mock.calls[0][1].operation_id);
});
