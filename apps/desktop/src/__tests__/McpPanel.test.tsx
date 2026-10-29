import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { invoke } from "@tauri-apps/api/core";
import { McpPanel } from "../components/McpPanel";

const preview = { executable: "owned-python", arguments: ["-m", "app.mcp_server"], scope: "Built-in sample only", approval: "a".repeat(64), enabled: false,
  tools: [{ name: "read_fixture", description: "Read the built-in sample", inputSchema: { type: "object" } }] };
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(invoke).mockImplementation(async (_command, args) => {
    const operation = (args as { operation: string }).operation;
    if (operation === "call") return { text: "Synthetic result" };
    return { ...preview, enabled: operation === "enable" };
  });
});
afterEach(cleanup);

it("shows the exact command and requires review before enabling", async () => {
  render(<McpPanel />);
  await screen.findByText("owned-python");
  expect(screen.getByText('["-m","app.mcp_server"]')).toBeTruthy();
  const enable = screen.getByRole("button", { name: "Enable test server" }) as HTMLButtonElement;
  expect(enable.disabled).toBe(true);
  expect(vi.mocked(invoke).mock.calls.every(([, args]) => (args as { operation: string }).operation === "preview")).toBe(true);
  fireEvent.click(screen.getByRole("checkbox"));
  fireEvent.click(enable);
  fireEvent.click(await screen.findByRole("button", { name: "Read sample" }));
  await screen.findByText("Synthetic result");
  fireEvent.click(screen.getByRole("button", { name: "Stop and disable" }));
  await screen.findByText("Disabled. No server is running.");
});

it("ignores late output after stopping a pending tool", async () => {
  let resolve!: (value: unknown) => void;
  vi.mocked(invoke).mockImplementation(async (_command, args) => {
    const operation = (args as { operation: string }).operation;
    if (operation === "call") return new Promise(done => { resolve = done; });
    return { ...preview, enabled: operation === "enable" };
  });
  render(<McpPanel />);
  fireEvent.click(await screen.findByRole("checkbox"));
  fireEvent.click(screen.getByRole("button", { name: "Enable test server" }));
  fireEvent.click(await screen.findByRole("button", { name: "Read sample" }));
  fireEvent.click(screen.getByRole("button", { name: "Stop and disable" }));
  await screen.findByText("Disabled. No server is running.");
  await act(async () => resolve({ text: "Stale result" }));
  expect(screen.queryByText("Stale result")).toBeNull();
});

it("stops owned processes when the panel closes", async () => {
  const view = render(<McpPanel />);
  await screen.findByText("owned-python");
  view.unmount();
  await waitFor(() => expect(invoke).toHaveBeenCalledWith("mcp_request", { operation: "stop", payload: null }));
});
