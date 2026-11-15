import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { VisualCard } from "../components/VisualCard";
import { invoke } from "@tauri-apps/api/core";

vi.mock("@tauri-apps/api/core", () => ({ invoke: vi.fn() }));
vi.mock("../components/VisualRenderers", () => ({
  Diagram: () => <div>Rendered validated diagram</div>, Chart: () => <div>Chart</div>,
  MermaidView: () => <div>Mermaid</div>, LearningWidget: () => <div>Widget</div>,
}));
const spec = { schema_version: 1, type: "diagram", title: "Process", nodes: [{ id: "a", label: "Start", x: 0, y: 0 }], edges: [] };
const source = JSON.stringify(spec), content = `\`\`\`localmind-visual\n${source}\n\`\`\``;
const document = { id: "document", conversation_id: "chat", source_message_id: "message", versions: [{ version: 1, parent: null, created_at: "2026-11-08", spec }] };
beforeEach(() => { vi.mocked(invoke).mockReset(); });
afterEach(cleanup);

it("does not render generated data until the service validates the saved source", async () => {
  vi.mocked(invoke).mockRejectedValue("Source has not been saved");
  render(<VisualCard source={source} content={content} conversationId="chat" />);
  fireEvent.click(screen.getByRole("button", { name: "Open visual workspace" }));
  expect(await screen.findByRole("alert")).toHaveProperty("textContent", "Source has not been saved");
  expect(screen.queryByRole("dialog")).toBeNull();
});

it("opens a validated document and keeps the source visible after closing", async () => {
  vi.mocked(invoke).mockResolvedValue(document);
  render(<VisualCard source={source} content={content} conversationId="chat" />);
  fireEvent.click(screen.getByRole("button", { name: "Open visual workspace" }));
  expect(await screen.findByText("Rendered validated diagram")).toBeTruthy();
  expect(invoke).toHaveBeenCalledWith("visual_request", { operation: "capture", body: { conversation_id: "chat", source_content: content, source_key: source, spec } });
  fireEvent.click(screen.getByRole("button", { name: "Close" }));
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(screen.getByText(source)).toBeTruthy();
});

it("uses the expected version and surfaces save conflicts without closing the editor", async () => {
  vi.mocked(invoke).mockResolvedValueOnce(document).mockRejectedValue("Changed in another window");
  render(<VisualCard source={source} content={content} conversationId="chat" />);
  fireEvent.click(screen.getByRole("button", { name: "Open visual workspace" }));
  await screen.findByRole("dialog");
  fireEvent.click(screen.getByRole("button", { name: "Save revision" }));
  await waitFor(() => expect(screen.getByRole("alert").textContent).toContain("another window"));
  expect(screen.getByRole("dialog")).toBeTruthy();
  expect(invoke).toHaveBeenLastCalledWith("visual_request", { operation: "revise", body: { id: "document", conversation_id: "chat", expected_version: 1, parent: 1, spec } });
});

it("draws Mermaid diagrams inline and opens a full-screen view", async () => {
  render(<VisualCard source={"flowchart TD\n  A --> B"} content="answer" conversationId="chat" mermaid />);
  expect(await screen.findAllByText("Mermaid")).toHaveLength(1);
  expect(invoke).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Expand" }));
  expect(screen.getByRole("dialog", { name: "Diagram" })).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Close" }));
  expect(screen.queryByRole("dialog")).toBeNull();
});

it("removes styling lines and escaped newlines that break rendering", async () => {
  const { cleanMermaid } = await import("../lib/visuals");
  const cleaned = cleanMermaid("flowchart TD\n  A[One\\n two] --> B:::hot\n  style A fill:#f9f\n  classDef hot fill:#f00\n  class B hot\n  linkStyle 0 stroke:#000");
  expect(cleaned).toBe("flowchart TD\n  A[One  two] --> B");
});
