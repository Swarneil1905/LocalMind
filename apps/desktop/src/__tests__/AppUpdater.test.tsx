import { StrictMode } from "react";
import { act, cleanup, fireEvent, render, renderHook, screen } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { invoke } from "@tauri-apps/api/core";
import { openUrl } from "@tauri-apps/plugin-opener";
import { useAppUpdater } from "../hooks/useAppUpdater";
import { UpdateSettings } from "../components/UpdateSettings";
import { UpdateBanner } from "../components/UpdateBanner";

vi.mock("@tauri-apps/api/app", () => ({ getVersion: vi.fn(async () => "0.8.0") }));
vi.mock("@tauri-apps/plugin-opener", () => ({ openUrl: vi.fn(async () => {}) }));
const available = { version: "0.8.1", currentVersion: "0.8.0", body: "Fix startup.\n<script>untrusted()</script>" };
function Harness() {
  const updater = useAppUpdater();
  return <><UpdateBanner updater={updater} /><UpdateSettings updater={updater} /></>;
}
beforeEach(() => { vi.clearAllMocks(); vi.mocked(invoke).mockResolvedValue(null); });
afterEach(() => { cleanup(); vi.useRealTimers(); });

it("shows the installed version and reports no newer release without needing the sidecar", async () => {
  render(<Harness />);
  await screen.findByText("Installed version: 0.8.0");
  fireEvent.click(screen.getByRole("button", { name: "Check for updates" }));
  await screen.findByText("You’re using the latest published version.");
  expect(invoke).toHaveBeenCalledExactlyOnceWith("check_for_updates");
});

it("checks automatically once in StrictMode and exposes an offline error and retry", async () => {
  vi.useFakeTimers();
  vi.mocked(invoke).mockRejectedValueOnce(new Error("offline"));
  render(<StrictMode><Harness /></StrictMode>);
  await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
  expect(screen.getAllByRole("alert")[0].textContent).toContain("Couldn’t check");
  fireEvent.click(screen.getByRole("button", { name: "Dismiss update" }));
  fireEvent.click(screen.getByRole("button", { name: "Check for updates" }));
  await act(async () => {});
  expect(screen.getByText("You’re using the latest published version.")).toBeTruthy();
  expect(invoke).toHaveBeenCalledTimes(2);
});

it("does not start another automatic check after a manual check", async () => {
  vi.useFakeTimers();
  const { result } = renderHook(() => useAppUpdater());
  await act(async () => { await result.current.check(); });
  await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
  expect(invoke).toHaveBeenCalledTimes(1);
});

it("keeps a dismissed update accessible in Settings and requires restart confirmation", async () => {
  vi.mocked(invoke).mockResolvedValue(available);
  render(<Harness />);
  fireEvent.click(screen.getByRole("button", { name: "Check for updates" }));
  await screen.findByText("Available version: 0.8.1");
  expect(document.querySelector("script")).toBeNull();
  expect(screen.getByText(/<script>untrusted/).textContent).toBe(available.body);
  fireEvent.click(screen.getByRole("button", { name: "Dismiss update" }));
  fireEvent.click(screen.getByRole("button", { name: "Update LocalMind" }));
  expect(invoke).not.toHaveBeenCalledWith("install_update", expect.anything());
  fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
  expect(screen.queryByRole("button", { name: "Install and restart" })).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Update LocalMind" }));
  fireEvent.click(screen.getByRole("button", { name: "Install and restart" }));
  await screen.findByText("Downloading and installing. LocalMind will restart when ready.");
  expect(invoke).toHaveBeenCalledWith("install_update", { expectedVersion: "0.8.1" });
  expect(screen.queryByRole("button", { name: "Check for updates" })).toBeNull();
});

it("recovers after an install or signature failure without claiming success", async () => {
  vi.mocked(invoke).mockImplementation(async command => {
    if (command === "install_update") throw new Error("invalid signature");
    return available;
  });
  render(<Harness />);
  fireEvent.click(screen.getByRole("button", { name: "Check for updates" }));
  await screen.findByText("Available version: 0.8.1");
  fireEvent.click(screen.getByRole("button", { name: "Dismiss update" }));
  fireEvent.click(screen.getByRole("button", { name: "Update LocalMind" }));
  fireEvent.click(screen.getByRole("button", { name: "Install and restart" }));
  expect((await screen.findByRole("alert")).textContent).toContain("Couldn’t install");
  expect(screen.getByRole("link", { name: "Open Releases for a manual installer" }).getAttribute("href")).toBe("https://github.com/Swarneil1905/LocalMind/releases/latest");
  expect(screen.queryByRole("button", { name: "Update LocalMind" })).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Check for updates" }));
  await screen.findAllByRole("button", { name: "Update LocalMind" });
});

it("deduplicates pending checks and ignores late results after unmount", async () => {
  let resolve!: (value: null) => void;
  vi.mocked(invoke).mockImplementation(() => new Promise<null>(done => { resolve = done; }));
  const { result, unmount } = renderHook(() => useAppUpdater());
  act(() => { void result.current.check(); void result.current.check(); });
  expect(invoke).toHaveBeenCalledTimes(1);
  unmount();
  await act(async () => { resolve(null); });
  expect(invoke).toHaveBeenCalledTimes(1);
});

it("offers a copyable release address when opening the browser fails", async () => {
  vi.mocked(openUrl).mockRejectedValueOnce(new Error("browser unavailable"));
  render(<Harness />);
  fireEvent.click(screen.getByRole("link", { name: "Open Releases for a manual installer" }));
  expect((await screen.findByRole("alert")).textContent).toContain("Copy this address: https://github.com/Swarneil1905/LocalMind/releases/latest");
  expect(openUrl).toHaveBeenCalledExactlyOnceWith("https://github.com/Swarneil1905/LocalMind/releases/latest");
});
