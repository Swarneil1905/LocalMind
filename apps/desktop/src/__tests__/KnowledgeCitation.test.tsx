import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { invoke } from "@tauri-apps/api/core";
import { AnswerContent } from "../components/AnswerContent";
import { KnowledgePage } from "../components/KnowledgePage";
import type { WebSource } from "../types";

const source: WebSource = {
  kind: "knowledge", citation: "K1", chunk_id: "passage-a", source_id: "folder-a",
  project_id: "project-a", title: "plan.md", url: "", snippet: "Launch November 12.",
  location: "Lines 3-4", file_path: "C:/fixtures/plan.md",
};
beforeEach(() => {
  vi.clearAllMocks();
  // jsdom does not implement the browser's modal lifecycle.
  HTMLDialogElement.prototype.showModal = function () { this.setAttribute("open", ""); };
  HTMLDialogElement.prototype.close = function () { this.removeAttribute("open"); };
});
afterEach(cleanup);

it("opens only supplied citations in the right project and restores keyboard focus", async () => {
  vi.mocked(invoke).mockResolvedValue({ file_path: source.file_path, location: source.location,
    content: "Launch November 12.", file_state: "current", notice: "Matches the indexed file." });
  const { container } = render(<AnswerContent sources={[source]} content="Launch is November 12 [K1]. Unknown [K2]. Code: `[K1]`." />);
  const button = screen.getByRole("button", { name: "Open plan.md, Lines 3-4" });
  expect(screen.getAllByRole("button")).toHaveLength(1);
  expect(container.textContent).toContain("[K2]");
  expect(container.querySelector("code")?.textContent).toBe("[K1]");
  button.focus();
  fireEvent.click(button);
  expect(await screen.findByLabelText("Source passage")).toHaveProperty("textContent", "Launch November 12.");
  expect(invoke).toHaveBeenCalledWith("knowledge_passage", { chunkId: "passage-a", projectId: "project-a" });
  fireEvent.click(screen.getByRole("button", { name: "Close source" }));
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(document.activeElement).toBe(button);
});

it("recovers from an opening error and explains a missing original file", async () => {
  vi.mocked(invoke).mockRejectedValueOnce("Source unavailable").mockResolvedValueOnce({
    file_path: source.file_path, location: source.location, content: source.snippet,
    file_state: "missing", notice: "The original file is missing. Restore it at the displayed path.",
  });
  render(<AnswerContent content="Evidence [K1]." sources={[source]} />);
  fireEvent.click(screen.getByRole("button", { name: "Open plan.md, Lines 3-4" }));
  expect((await screen.findByRole("alert")).textContent).toContain("Source unavailable");
  fireEvent.click(screen.getByRole("button", { name: "Retry opening source" }));
  await screen.findByLabelText("Source passage");
  expect(screen.getByRole("status").textContent).toContain("original file is missing");
  expect(screen.getByText(/open Knowledge and choose Retry indexing/)).not.toBeNull();
  fireEvent(screen.getByRole("dialog"), new Event("cancel", { bubbles: true }));
  expect(screen.queryByRole("dialog")).toBeNull();
});

it("keeps partial indexes searchable and allows an explicit retry", async () => {
  const folder = { id: "folder-a", name: "Notes", path: "C:/fixtures", project_id: null,
    file_count: 1, chunk_count: 1, status: "partial", created_at: "2026-10-21",
    error_message: "1 passage without semantic search. Text search is available." };
  vi.mocked(invoke).mockImplementation(async (command) => {
    if (command === "list_knowledge_sources") return [folder];
    return [];
  });
  render(<KnowledgePage embedModel="nomic-embed-text" />);
  await screen.findByText(folder.error_message);
  fireEvent.change(screen.getByRole("textbox", { name: "Search your files" }), { target: { value: "LAUNCH-424" } });
  fireEvent.click(screen.getByRole("button", { name: "Search" }));
  await waitFor(() => expect(invoke).toHaveBeenCalledWith("search_knowledge", {
    query: "LAUNCH-424", embedModel: "nomic-embed-text", limit: 5, projectId: null,
  }));
  fireEvent.click(screen.getByRole("button", { name: /Retry indexing/ }));
  await waitFor(() => expect(invoke).toHaveBeenCalledWith("retry_knowledge", {
    sourceId: "folder-a", embedModel: "nomic-embed-text",
  }));
});
