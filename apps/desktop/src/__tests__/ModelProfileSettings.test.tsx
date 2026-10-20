import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { invoke } from "@tauri-apps/api/core";
import { ModelProfileSettings } from "../components/ModelProfileSettings";

const profile = {
  model: "qwen3:8b",
  preset: "balanced",
  digest: "sha256:1234567890abcdef",
  context_limit: 32768,
  context_window: 8192,
  response_tokens: 1536,
  temperature: 0.6,
  top_p: 0.95,
  top_k: 20,
  think: null,
  thinking_values: [true, false],
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(invoke).mockResolvedValue(profile);
});
afterEach(cleanup);

async function openSettings() {
  render(<ModelProfileSettings speedModel="qwen2.5:1.5b" balancedModel="qwen3:8b" />);
  fireEvent.click(screen.getByText("Response settings"));
  return screen.findByRole("spinbutton", { name: "Context capacity (tokens)" });
}

describe("model response settings", () => {
  it("loads the selected local model profile and saves the changed controls", async () => {
    const context = await openSettings();
    fireEvent.change(context, { target: { value: "6144" } });
    fireEvent.change(screen.getByRole("combobox", { name: "Thinking" }), { target: { value: "false" } });
    fireEvent.click(screen.getByRole("button", { name: "Save response settings" }));
    await screen.findByText("Response settings saved.");
    expect(invoke).toHaveBeenCalledWith("model_profile", {
      model: "qwen3:8b",
      preset: "balanced",
      changes: {
        context_window: 6144,
        response_tokens: 1536,
        temperature: 0.6,
        top_p: 0.95,
        top_k: 20,
        think: false,
      },
    });
  });

  it("retains edits and offers an error when the save fails", async () => {
    const context = await openSettings();
    vi.mocked(invoke).mockRejectedValueOnce(new Error("The installed model changed. Reload its settings."));
    fireEvent.change(context, { target: { value: "6144" } });
    fireEvent.click(screen.getByRole("button", { name: "Save response settings" }));
    expect((await screen.findByRole("alert")).textContent).toContain("installed model changed");
    expect((context as HTMLInputElement).value).toBe("6144");
    expect(screen.queryByText("Response settings saved.")).toBeNull();
    expect(screen.getByRole("button", { name: "Save response settings" }).hasAttribute("disabled")).toBe(false);
  });

  it("can retry a failed profile lookup", async () => {
    vi.mocked(invoke).mockRejectedValueOnce(new Error("Model unavailable"));
    render(<ModelProfileSettings speedModel="qwen2.5:1.5b" balancedModel="qwen3:8b" />);
    fireEvent.click(screen.getByText("Response settings"));
    fireEvent.click(await screen.findByRole("button", { name: "Retry model settings" }));
    await screen.findByRole("spinbutton", { name: "Context capacity (tokens)" });
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("cannot overwrite the new mode with a late response from the previous mode", async () => {
    let resolveBalanced!: (value: typeof profile) => void;
    vi.mocked(invoke).mockImplementation(async (_command, args) => {
      if ((args as { preset: string }).preset === "balanced") {
        return new Promise((resolve) => { resolveBalanced = resolve; });
      }
      return { ...profile, model: "qwen2.5:1.5b", preset: "speed", context_window: 4096, response_tokens: 512 };
    });
    render(<ModelProfileSettings speedModel="qwen2.5:1.5b" balancedModel="qwen3:8b" />);
    fireEvent.click(screen.getByText("Response settings"));
    fireEvent.change(screen.getByRole("combobox", { name: "Chat mode" }), { target: { value: "speed" } });
    const context = await screen.findByRole("spinbutton", { name: "Context capacity (tokens)" });
    await act(async () => resolveBalanced(profile));
    expect((context as HTMLInputElement).value).toBe("4096");
    expect(screen.getByText(/qwen2.5:1.5b · Installed revision/)).toBeDefined();
  });

  it("restores saved settings through the restore command and reflects the returned values", async () => {
    await openSettings();
    vi.mocked(invoke).mockResolvedValueOnce({ ...profile, context_window: 4096, response_tokens: 512 });
    fireEvent.click(screen.getByRole("button", { name: "Restore previous settings" }));
    await screen.findByText("Previous settings restored.");
    expect(invoke).toHaveBeenCalledWith("restore_model_profile", { model: "qwen3:8b", preset: "balanced" });
    expect((screen.getByRole("spinbutton", { name: "Context capacity (tokens)" }) as HTMLInputElement).value).toBe("4096");
  });

  it("leaves unsupported thinking controls unavailable", async () => {
    vi.mocked(invoke).mockResolvedValue({ ...profile, thinking_values: [] });
    await openSettings();
    await waitFor(() => expect(screen.getByRole("combobox", { name: "Thinking" }).hasAttribute("disabled")).toBe(true));
  });

  it("blocks submission when the response budget leaves too little room for input", async () => {
    const context = await openSettings();
    expect(context.getAttribute("min")).toBe("3200");
    expect(screen.queryByRole("option", { name: "Boost" })).toBeNull();
    fireEvent.change(context, { target: { value: "4096" } });
    const response = screen.getByRole("spinbutton", { name: "Maximum response (tokens)" }) as HTMLInputElement;
    expect(response.max).toBe("1024");
    expect(response.validity.rangeOverflow).toBe(true);
    vi.mocked(invoke).mockClear();
    fireEvent.click(screen.getByRole("button", { name: "Save response settings" }));
    expect(invoke).not.toHaveBeenCalled();
  });

  it("uses the model's reported input reservation rather than the fallback", async () => {
    vi.mocked(invoke).mockResolvedValue({ ...profile, minimum_input_budget: 4096 });
    const context = await openSettings();
    expect(context.getAttribute("min")).toBe("4224");
    expect(screen.getByRole("spinbutton", { name: "Maximum response (tokens)" }).getAttribute("max")).toBe("4096");
  });

  it("preserves a cleared numeric draft without submitting it as zero", async () => {
    const context = await openSettings() as HTMLInputElement;
    fireEvent.change(context, { target: { value: "" } });
    expect(context.value).toBe("");
    expect(context.validity.valueMissing).toBe(true);
    vi.mocked(invoke).mockClear();
    fireEvent.click(screen.getByRole("button", { name: "Save response settings" }));
    expect(invoke).not.toHaveBeenCalled();
  });

  it("shows a recovery notice until a successful save returns a profile without it", async () => {
    const recoveryNotice = "Saved settings no longer fit this model. Safe defaults are active; your previous settings are retained.";
    vi.mocked(invoke).mockResolvedValueOnce({ ...profile, settings_notice: recoveryNotice });
    await openSettings();
    const notice = screen.getByText(recoveryNotice);
    expect(notice.getAttribute("role")).toBe("status");
    expect(screen.queryByRole("alert")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Save response settings" }));
    await screen.findByText("Response settings saved.");
    expect(screen.queryByText(recoveryNotice)).toBeNull();
  });
});
