import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { invoke } from "@tauri-apps/api/core";
import { AgentStatus } from "../components/AgentStatus";
import { MessageList } from "../components/MessageList";
import { ModelPicker } from "../components/ModelPicker";
import { agentFor } from "../lib/agents";
import type { CloudProvider } from "../lib/providers";
import type { Message } from "../types";

afterEach(cleanup);
const openai: CloudProvider = { id: "openai", label: "OpenAI", connected: true, model: "gpt-x", vision: true,
  input_price_per_million: null, output_price_per_million: null, approved_scopes: [] };
const pickerProps = () => ({ value: "balanced" as const, onChange: vi.fn(), modelName: "qwen3:8b", disabled: false,
  models: { speed: "qwen2.5:1.5b", balanced: "qwen3:8b" }, providers: [openai], installed: ["qwen3:8b", "gemma3:4b"],
  onSelectLocal: vi.fn(), onSelectProvider: vi.fn() });

it("shows finished web steps above the active one", () => {
  render(<AgentStatus phase="reading" sources={3} web />);
  expect(screen.getByText("Searched the web")).toBeTruthy();
  expect(screen.getByText("Reading 3 sources")).toBeTruthy();
});

it("shows a thinking status while a local answer has no text", () => {
  const empty: Message = { id: "a", role: "assistant", content: "" };
  render(<MessageList messages={[empty]} isStreaming modelName="qwen3:8b" />);
  expect(screen.getAllByRole("status").some(node => node.textContent?.includes("Thinking"))).toBe(true);
  expect(screen.getByText("qwen3:8b")).toBeTruthy();
});

it("picks the logo from the model's maker for cloud answers only", () => {
  expect(agentFor("gpt-5", "openai")).toBe("openai");
  expect(agentFor("claude-x", "openrouter")).toBe("anthropic");
  expect(agentFor("gemma3:4b", "ollama")).toBe("ollama");
});

it("lists other installed models and cloud accounts in one menu", () => {
  const props = pickerProps();
  render(<ModelPicker {...props} />);
  fireEvent.click(screen.getByRole("button", { name: /Model mode/ }));
  fireEvent.click(screen.getByText("Other installed models"));
  fireEvent.click(screen.getByRole("button", { name: "gemma3:4b" }));
  expect(props.onSelectLocal).toHaveBeenCalledWith("gemma3:4b");
  expect(props.onSelectProvider).toHaveBeenCalledWith("ollama");
});

it("browses a provider's live model list and saves the chosen model", async () => {
  vi.mocked(invoke).mockImplementation(async (_name, args) => {
    const { method, path } = args as { method: string; path: string };
    if (method === "GET" && path === "/providers/openai/models") return { models: [{ id: "gpt-x", name: "gpt-x", vision: true }, { id: "gpt-mini", name: "gpt-mini", vision: false }] };
    return {};
  });
  const props = pickerProps();
  render(<ModelPicker {...props} />);
  fireEvent.click(screen.getByRole("button", { name: /Model mode/ }));
  fireEvent.click(screen.getByRole("button", { name: "Browse OpenAI models" }));
  fireEvent.change(await screen.findByLabelText("Filter OpenAI models"), { target: { value: "mini" } });
  expect(screen.queryByRole("button", { name: "gpt-x" })).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "gpt-mini" }));
  await waitFor(() => expect(invoke).toHaveBeenCalledWith("provider_request", expect.objectContaining({ method: "PATCH", path: "/providers/openai/config", body: { model: "gpt-mini", vision: false } })));
  expect(props.onSelectProvider).toHaveBeenCalledWith("openai");
});

it("blocks cloud rows and browsing while chats stay on this device", () => {
  render(<ModelPicker {...pickerProps()} localOnly />);
  fireEvent.click(screen.getByRole("button", { name: /Model mode/ }));
  expect((screen.getByRole("button", { name: "OpenAI, gpt-x" }) as HTMLButtonElement).disabled).toBe(true);
  expect(screen.queryByRole("button", { name: "Browse OpenAI models" })).toBeNull();
});

it("treats a length-limited reply as a note with Continue, not a failure", () => {
  const onContinue = vi.fn();
  const cut: Message = { id: "a", role: "assistant", content: "Step 1 and Step 2.", error: true,
    errorMessage: "The reply reached its length limit. Your partial answer is kept." };
  const { container } = render(<MessageList messages={[cut]} isStreaming={false} onContinue={onContinue} />);
  expect(container.querySelector(".message-error")).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Continue" }));
  expect(onContinue).toHaveBeenCalledOnce();
});
