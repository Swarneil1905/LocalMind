import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { invoke } from "@tauri-apps/api/core";
import { ConnectionsPage } from "../components/ConnectionsPage";

vi.mock("../components/McpPanel", () => ({ McpPanel: () => null }));
vi.mock("../components/ExtensionsPanel", () => ({ ExtensionsPanel: () => null }));
const source = (status = "connected") => ({ id: "gmail", name: "Gmail", description: "Read recent snippets", status, available: true, location: "cloud", platform: "all", requires_browser: true,
  capabilities: { authorization: "oauth", read: true, sync: true, actions: [], range_days: 30, max_items: 100 }, evidence: "Synthetic provider fixtures" });
let state = "connected";
beforeEach(() => {
  state = "connected";
  vi.clearAllMocks();
  vi.spyOn(window, "confirm").mockReturnValue(true);
  vi.mocked(invoke).mockImplementation(async command => {
    if (command === "list_connectors") return [source(state)];
    if (command === "disconnect_connector") { state = "disconnected"; return {}; }
    if (command === "sync_connector") { state = "offline"; throw new Error("Offline"); }
    return [];
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it("does not resurrect connected state from an older poll after disconnect", async () => {
  let resolve!: (value: unknown) => void;
  let lists = 0;
  vi.mocked(invoke).mockImplementation(async command => {
    if (command === "list_connectors") {
      if (++lists === 2) return new Promise(done => { resolve = done; });
      return [source(state)];
    }
    if (command === "disconnect_connector") state = "disconnected";
    return [];
  });
  render(<ConnectionsPage />);
  await screen.findByText("Connected");
  fireEvent.click(screen.getByRole("button", { name: "Refresh" }));
  fireEvent.click(screen.getByRole("button", { name: "Disconnect" }));
  await screen.findByText("Not connected");
  await act(async () => resolve([source("connected")]));
  expect(screen.queryByText("Connected")).toBeNull();
  expect(screen.getByText("Not connected")).toBeTruthy();
});

it("shows permission limits and expiry, and prevents unavailable adapters from connecting", async () => {
  vi.mocked(invoke).mockImplementation(async command => command === "list_connectors" ? [source("expired"), { ...source("disabled"), id: "whatsapp_web", name: "WhatsApp", available: false }] : []);
  render(<ConnectionsPage />);
  await screen.findByText("Authorization expired");
  expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy();
  expect(screen.getAllByText(/Last 30 days, up to 100 items/).length).toBe(2);
  expect((screen.getByRole("button", { name: "Unavailable" }) as HTMLButtonElement).disabled).toBe(true);
  expect(screen.queryByRole("button", { name: "Sync WhatsApp" })).toBeNull();
});

it("refreshes a failed sync state and clears stale connection status", async () => {
  render(<ConnectionsPage />);
  fireEvent.click(await screen.findByRole("button", { name: "Sync Gmail" }));
  await screen.findByText("Couldn’t sync this app. Please try again.");
  await waitFor(() => expect(screen.getByText("Offline")).toBeTruthy());
  expect(screen.queryByText("Connected")).toBeNull();
});
