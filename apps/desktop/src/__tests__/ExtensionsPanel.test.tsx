import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { invoke } from "@tauri-apps/api/core";
import { ExtensionsPanel } from "../components/ExtensionsPanel";

let enabled = false;
let registered = false;
const catalog = () => ({
  planner: { enabled, status: enabled ? "enabled" : "disabled", error: "", evidence: "Synthetic planner tests", manifest: { version: "1.0.0", permissions: ["task.preview"] } },
  recipe: { registered, permissions: ["synthetic.copy.read", "synthetic.copy.write"], evidence: "Synthetic repair tests" },
  images: { status: "not-installed", reason: "No image worker is installed.", evidence: "Hardware trial" },
});

beforeEach(() => {
  enabled = false; registered = false;
  vi.clearAllMocks();
  vi.mocked(invoke).mockImplementation(async (_command, args) => {
    const { operation, payload } = args as { operation: string; payload: Record<string, unknown> };
    if (operation === "planner-state") enabled = payload.enabled === true;
    if (operation === "planner-call") return { tasks: [{ title: "Review launch" }], saved: false };
    if (operation === "recipe-export") return { package: '{"synthetic":true}' };
    if (operation === "recipe-import") {
      if (payload.package !== '{"synthetic":true}') throw new Error("Invalid recipe");
      registered = true;
      return { synthetic: true, checks: { exactly_one_match: true, markdown_valid: true, original_unchanged: true, expected_output: true } };
    }
    return catalog();
  });
});
afterEach(cleanup);

it("requires permission, previews without saving and disables tools", async () => {
  render(<ExtensionsPanel />);
  const enable = await screen.findByRole("button", { name: "Enable planner" });
  expect((enable as HTMLButtonElement).disabled).toBe(true);
  expect(screen.queryByRole("button", { name: "Preview tasks" })).toBeNull();
  fireEvent.click(screen.getByRole("checkbox"));
  fireEvent.click(enable);
  fireEvent.change(await screen.findByLabelText("Tasks, one per line"), { target: { value: "Review launch" } });
  fireEvent.click(screen.getByRole("button", { name: "Preview tasks" }));
  await screen.findByText("Preview only · Not saved");
  await waitFor(() => expect((screen.getByRole("button", { name: "Disable planner" }) as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(screen.getByRole("button", { name: "Disable planner" }));
  await screen.findByText("Planner disabled");
  expect(screen.queryByText("Preview only · Not saved")).toBeNull();
  expect(screen.queryByRole("button", { name: "Preview tasks" })).toBeNull();
  expect((screen.getByRole("button", { name: "Not available in this beta" }) as HTMLButtonElement).disabled).toBe(true);
});

it("recovers from a rejected import without showing a false success", async () => {
  render(<ExtensionsPanel />);
  const input = await screen.findByLabelText("Recipe JSON");
  fireEvent.change(input, { target: { value: "corrupt" } });
  fireEvent.click(screen.getByRole("button", { name: "Import and run checks" }));
  await screen.findByRole("alert");
  expect(screen.queryByText(/All four synthetic checks passed/)).toBeNull();
  await waitFor(() => expect((screen.getByRole("button", { name: "Export synthetic recipe" }) as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(screen.getByRole("button", { name: "Export synthetic recipe" }));
  await waitFor(() => expect((input as HTMLTextAreaElement).value).toBe('{"synthetic":true}'));
  await waitFor(() => expect((screen.getByRole("button", { name: "Import and run checks" }) as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(screen.getByRole("button", { name: "Import and run checks" }));
  await screen.findByText(/All four synthetic checks passed/);
  await screen.findByText("Synthetic recipe registered");
  expect(screen.queryByRole("alert")).toBeNull();
});

it("does not download a late export after the panel closes", async () => {
  let resolve!: (value: unknown) => void;
  vi.mocked(invoke).mockImplementation(async (_command, args) => {
    if ((args as { operation: string }).operation === "recipe-export") return new Promise(done => { resolve = done; });
    return catalog();
  });
  const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
  const view = render(<ExtensionsPanel />);
  fireEvent.click(await screen.findByRole("button", { name: "Save synthetic recipe JSON" }));
  view.unmount();
  await act(async () => resolve({ package: "{}" }));
  expect(click).not.toHaveBeenCalled();
  click.mockRestore();
});

it("rejects an incomplete synthetic check receipt", async () => {
  vi.mocked(invoke).mockImplementation(async (_command, args) => {
    if ((args as { operation: string }).operation === "recipe-import") return { synthetic: true, checks: {} };
    return catalog();
  });
  render(<ExtensionsPanel />);
  fireEvent.change(await screen.findByLabelText("Recipe JSON"), { target: { value: "{}" } });
  fireEvent.click(screen.getByRole("button", { name: "Import and run checks" }));
  await screen.findByRole("alert");
  expect(screen.queryByText(/All four synthetic checks passed/)).toBeNull();
});
