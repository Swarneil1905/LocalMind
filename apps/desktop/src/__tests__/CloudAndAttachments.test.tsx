import { act, cleanup, fireEvent, render, renderHook, screen, waitFor, within } from "@testing-library/react";
import { createRef } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { invoke } from "@tauri-apps/api/core";
import { Composer } from "../components/Composer";
import { ProviderSettings } from "../components/ProviderSettings";
import { CloudConsent } from "../components/CloudConsent";
import { useAttachments } from "../hooks/useAttachments";
import { readImage } from "../lib/attachments";
import type { CloudProvider, ProviderStatus } from "../lib/providers";

const cloud: CloudProvider = { id: "openai", label: "OpenAI", connected: true, model: "test-vision",
  vision: true, input_price_per_million: 1, output_price_per_million: 2, approved_scopes: [] };
const status: ProviderStatus = { local_only: true, monthly_limit_usd: 50, providers: [cloud],
  usage: { month: "2026-11", spent_usd: 1, reserved_usd: 2, unknown_requests: 0, account_wide_spend: null } };
const image = { id: "image-id", filename: "shot.png", mime_type: "image/png", width: 1, height: 1, bytes: 8, data_url: "data:image/png;base64,AA==" };
const props = () => ({ textareaRef: createRef<HTMLTextAreaElement>(), value: "Explain this", onChange: vi.fn(),
  onSend: vi.fn().mockResolvedValue(true), onStop: vi.fn(), isStreaming: false, disabled: false,
  memoryEnabled: false, onMemoryToggle: vi.fn(), knowledgeEnabled: false, onKnowledgeToggle: vi.fn(),
  webSearchEnabled: false, onWebSearchToggle: vi.fn(), modelMode: "balanced" as const,
  onModelModeChange: vi.fn(), modelName: "local", onOpenKnowledge: vi.fn(), onAddAttachments: vi.fn() });
beforeEach(() => { vi.clearAllMocks(); localStorage.clear(); vi.mocked(invoke).mockResolvedValue(null); });
afterEach(cleanup);

describe("image composer", () => {
  it("adds each clipboard image once and preserves mixed pasted text", () => {
    const p = props(); render(<Composer {...p} />);
    const input = screen.getByRole("textbox", { name: "Message LocalMind" }) as HTMLTextAreaElement;
    input.setSelectionRange(8, 12);
    const file = new File(["image"], "shot.png", { type: "image/png" });
    fireEvent.paste(input, { clipboardData: { items: [{ kind: "file", type: "image/png", getAsFile: () => file }], files: [file], getData: () => "chart" } });
    expect(p.onAddAttachments).toHaveBeenCalledExactlyOnceWith([file]);
    expect(p.onChange).toHaveBeenCalledExactlyOnceWith("Explain chart");
  });
  it("leaves ordinary text paste to the browser", () => {
    const p = props(); render(<Composer {...p} />);
    const event = new Event("paste", { bubbles: true, cancelable: true });
    Object.defineProperty(event, "clipboardData", { value: { items: [], getData: () => "ordinary text" } });
    screen.getByRole("textbox", { name: "Message LocalMind" }).dispatchEvent(event);
    expect(event.defaultPrevented).toBe(false);
    expect(p.onAddAttachments).not.toHaveBeenCalled();
  });
  it("uses the same attachment path for picker and drop", () => {
    const p = props(); const view = render(<Composer {...p} />);
    const file = new File(["image"], "shot.png", { type: "image/png" });
    fireEvent.change(screen.getByLabelText("Choose attachments"), { target: { files: [file] } });
    fireEvent.drop(view.container.querySelector(".composer-box")!, { dataTransfer: { files: [file], types: ["Files"] } });
    expect(p.onAddAttachments).toHaveBeenNthCalledWith(1, [file]); expect(p.onAddAttachments).toHaveBeenNthCalledWith(2, [file]);
  });
  it("blocks text-only model image sends for button and Enter without changing provider", () => {
    const p = props(); render(<Composer {...p} attachments={[{ ...image, localId: "draft", status: "ready" }]} imageInput={false} />);
    expect((screen.getByRole("button", { name: "Send message" }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.keyDown(screen.getByRole("textbox", { name: "Message LocalMind" }), { key: "Enter" });
    expect(p.onSend).not.toHaveBeenCalled(); expect(screen.getByText(/This model cannot read images/)).toBeDefined();
  });
  it("rejects unsupported formats and size before reading", async () => {
    await expect(readImage(new File(["svg"], "bad.svg", { type: "image/svg+xml" }))).rejects.toThrow("PNG");
    await expect(readImage(new File([new Uint8Array(8 * 1024 * 1024 + 1)], "large.png", { type: "image/png" }))).rejects.toThrow("8 MiB");
  });
  it("discards an upload that finishes after switching chats", async () => {
    let finish!: (value: unknown) => void;
    vi.mocked(invoke).mockImplementation(async (name, args) => {
      if (name === "attachment_request" && (args as { method: string }).method === "POST") return new Promise(resolve => { finish = resolve; });
      return null;
    });
    const { result, rerender } = renderHook(({ scope }) => useAttachments(scope), { initialProps: { scope: "new" } });
    act(() => result.current.add([new File(["image"], "shot.png", { type: "image/png" })]));
    await waitFor(() => expect(finish).toBeDefined());
    rerender({ scope: "other-chat" });
    await act(async () => finish(image));
    expect(result.current.items).toEqual([]);
    expect(invoke).toHaveBeenCalledWith("attachment_request", expect.objectContaining({ method: "DELETE", path: "/attachments/image-id" }));
    rerender({ scope: "new" }); expect(result.current.items).toEqual([]);
  });
  it("does not reinsert an attachment removed while uploading", async () => {
    let finish!: (value: unknown) => void;
    vi.mocked(invoke).mockImplementation(async (_name, args) => (args as { method: string }).method === "POST" ? new Promise(resolve => { finish = resolve; }) : null);
    const { result } = renderHook(() => useAttachments("new"));
    act(() => result.current.add([new File(["image"], "shot.png", { type: "image/png" })]));
    await waitFor(() => expect(finish).toBeDefined());
    act(() => result.current.remove(result.current.items[0].localId));
    await act(async () => finish(image));
    expect(result.current.items).toEqual([]);
    expect(invoke).toHaveBeenCalledWith("attachment_request", expect.objectContaining({ method: "DELETE" }));
  });
  it("restores a failed draft's images without overwriting another draft owner", async () => {
    vi.mocked(invoke).mockResolvedValue(image);
    const { result } = renderHook(() => useAttachments("chat-a"));
    act(() => result.current.restore("chat-a", "original-draft", [image]));
    expect(result.current.draftId).toBe("original-draft");
    expect(result.current.attachments.map(item => item.id)).toEqual(["image-id"]);
    act(() => result.current.restore("chat-a", "other-draft", [{ ...image, id: "other-image" }]));
    expect(result.current.attachments.map(item => item.id)).toEqual(["image-id"]);
  });
});

describe("cloud setup and permission", () => {
  it("stores a key through the credential bridge, clears the input, and never persists it in localStorage", async () => {
    vi.mocked(invoke).mockResolvedValue(status);
    render(<ProviderSettings />);
    const key = await screen.findByLabelText("OpenAI API key");
    fireEvent.change(key, { target: { value: "secret-test-key" } });
    fireEvent.click(screen.getByRole("button", { name: "Save key" }));
    await waitFor(() => expect(invoke).toHaveBeenCalledWith("provider_request", { method: "PUT", path: "/providers/openai/credential", body: { key: "secret-test-key" } }));
    expect((key as HTMLInputElement).value).toBe(""); expect(localStorage.length).toBe(0);
  });
  it("connects a new provider from its logo tile in one step", async () => {
    const groq: CloudProvider = { id: "groq", label: "Groq", connected: false, model: "", vision: false,
      input_price_per_million: null, output_price_per_million: null, approved_scopes: [] };
    vi.mocked(invoke).mockResolvedValue({ ...status, providers: [cloud, groq] });
    render(<ProviderSettings />);
    fireEvent.click(await screen.findByRole("button", { name: "Connect Groq" }));
    const dialog = screen.getByRole("dialog", { name: "Connect Groq" });
    fireEvent.change(screen.getByLabelText("Groq API key"), { target: { value: "gsk-test" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save key" }));
    await waitFor(() => expect(invoke).toHaveBeenCalledWith("provider_request", { method: "PUT", path: "/providers/groq/credential", body: { key: "gsk-test" } }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
  it("blocks remote checks and model listing in local-only mode", async () => {
    vi.mocked(invoke).mockResolvedValue(status); render(<ProviderSettings />);
    await screen.findByText("Ready · test-vision");
    expect((screen.getByRole("button", { name: "Check connection" }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: "Load available models" }) as HTMLButtonElement).disabled).toBe(true);
  });
  it("previews local request data and grants only its returned scopes after explicit approval", async () => {
    const approve = vi.fn(), cancel = vi.fn();
    vi.mocked(invoke).mockImplementation(async (_name, args) => {
      const request = args as { path: string; method: string };
      if (request.path === "/chat/preview") return { provider: "openai", model: cloud.model, scopes: ["messages", "attachments"], messages: [{ role: "user", content: "Explain screenshot" }] };
      if (request.path === "/providers") return { ...status, providers: [{ ...cloud, consent_id: "approved", consent_model: cloud.model }] };
      return { consent_id: "approved" };
    });
    render(<CloudConsent provider={cloud} request={{ message: "Explain screenshot" }} onCancel={cancel} onApprove={approve} />);
    await screen.findByText("Images attached to this chat");
    expect(invoke).not.toHaveBeenCalledWith("provider_request", expect.objectContaining({ method: "PUT" }));
    fireEvent.click(screen.getByRole("button", { name: "Allow and send" }));
    await waitFor(() => expect(approve).toHaveBeenCalledWith("approved"));
    expect(invoke).toHaveBeenCalledWith("provider_request", { method: "PUT", path: "/providers/openai/consent", body: { scopes: ["messages", "attachments"] } });
    expect(invoke).not.toHaveBeenCalledWith("chat_stream", expect.anything());
  });
  it("does not grant anything when preview fails or review is cancelled", async () => {
    const cancel = vi.fn(), approve = vi.fn(); vi.mocked(invoke).mockRejectedValue(new Error("Service unavailable"));
    render(<CloudConsent provider={cloud} request={{}} onCancel={cancel} onApprove={approve} />);
    await screen.findByRole("alert");
    expect((screen.getByRole("button", { name: "Allow and send" }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "Keep my draft" }));
    expect(cancel).toHaveBeenCalledOnce(); expect(approve).not.toHaveBeenCalled();
  });
  it("shows an image and a cost estimate without dumping its encoded bytes", async () => {
    vi.mocked(invoke).mockResolvedValue({ provider: "openai", model: cloud.model, scopes: ["messages", "attachments"],
      messages: [{ role: "user", content: "Read chart", images: [{ mime_type: "image/png", data: "base64-secret-fixture" }] }],
      cost_estimate: { upper_bound_usd: 0.05, input_bound: 1000, output_limit: 500, pricing_known: true } });
    render(<CloudConsent provider={cloud} request={{}} onCancel={vi.fn()} onApprove={vi.fn()} />);
    await screen.findByText(/Conservative cost estimate: up to \$0.0500/);
    fireEvent.click(screen.getByText("Review the prepared messages and images"));
    expect(screen.getByAltText("Image 1 included in this request").getAttribute("src")).toBe("data:image/png;base64,base64-secret-fixture");
    expect(screen.queryByText(/"data": "base64-secret-fixture"/)).toBeNull();
  });
});
