import { act, cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { invoke } from "@tauri-apps/api/core";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { DraftAttachments, SavedAttachments } from "../components/ChatAttachments";
import type { ChatAttachment } from "../lib/attachments";

vi.mock("../components/VisualCard", () => ({ VisualCard: () => null }));

const a: ChatAttachment = { id: "a", filename: "first.txt", mime_type: "text/plain", width: 0, height: 0, bytes: 10, kind: "document" };
const b: ChatAttachment = { ...a, id: "b", filename: "second.txt" };
const preview = (item: ChatAttachment) => ({ ...item, pages: [{ page: 1, text: `Contents of ${item.filename}`, scanned: false, truncated: false }] });
const deferred = () => {
  let resolve!: (value: ChatAttachment) => void;
  let reject!: (cause: Error) => void;
  const promise = new Promise<ChatAttachment>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};
beforeEach(() => vi.mocked(invoke).mockReset());
afterEach(cleanup);

describe("saved attachment preview requests", () => {
  it.each(["first", "second"])("keeps the latest selection when the %s request finishes first", async (order) => {
    const requests = [deferred(), deferred()];
    vi.mocked(invoke).mockReturnValueOnce(requests[0].promise).mockReturnValueOnce(requests[1].promise);
    const user = userEvent.setup();
    render(<SavedAttachments items={[a, b]} conversationId="chat-a" />);
    await user.click(screen.getByRole("button", { name: a.filename }));
    await user.click(screen.getByRole("button", { name: b.filename }));
    expect(screen.getByRole("status").textContent).toContain(b.filename);
    if (order === "first") {
      await act(async () => requests[0].resolve(preview(a)));
      expect(screen.queryByRole("dialog")).toBeNull();
      expect(screen.getByRole("status").textContent).toContain(b.filename);
    }
    await act(async () => requests[1].resolve(preview(b)));
    if (order === "second") await act(async () => requests[0].resolve(preview(a)));
    expect(screen.getByRole("dialog", { name: `Attachment preview: ${b.filename}` })).toBeTruthy();
    expect(screen.getByText(`Contents of ${b.filename}`)).toBeTruthy();
    expect(screen.queryByText(`Contents of ${a.filename}`)).toBeNull();
    expect(invoke).toHaveBeenNthCalledWith(2, "attachment_request", { method: "POST", path: "/attachments/preview", body: { id: b.id, conversation_id: "chat-a" } });
  });

  it.each(["loading", "ready"])("ignores a stale rejection while the current preview is %s", async (phase) => {
    const first = deferred(), second = deferred();
    vi.mocked(invoke).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const user = userEvent.setup();
    render(<SavedAttachments items={[a, b]} conversationId="chat-a" />);
    await user.click(screen.getByRole("button", { name: a.filename }));
    await user.click(screen.getByRole("button", { name: b.filename }));
    if (phase === "ready") await act(async () => second.resolve(preview(b)));
    await act(async () => first.reject(new Error("Old attachment failed")));
    expect(screen.queryByRole("alert")).toBeNull();
    if (phase === "loading") {
      expect(screen.getByRole("status").textContent).toContain(b.filename);
      await act(async () => second.resolve(preview(b)));
    }
    expect(screen.getByRole("dialog", { name: `Attachment preview: ${b.filename}` })).toBeTruthy();
  });

  it("identifies a current failure and retries the same file and owning conversation", async () => {
    const retry = deferred();
    vi.mocked(invoke).mockRejectedValueOnce(new Error("Preview unavailable")).mockReturnValueOnce(retry.promise);
    const user = userEvent.setup();
    render(<SavedAttachments items={[a]} conversationId="chat-a" />);
    await user.click(screen.getByRole("button", { name: a.filename }));
    expect(screen.getByRole("alert").textContent).toContain(a.filename);
    expect(screen.getByRole("alert").textContent).toContain("Preview unavailable");
    await user.click(screen.getByRole("button", { name: `Retry preview for ${a.filename}` }));
    expect(document.activeElement).toBe(screen.getByRole("button", { name: a.filename }));
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.getByRole("status").textContent).toContain(a.filename);
    await act(async () => retry.resolve(preview(a)));
    expect(screen.getByText(`Contents of ${a.filename}`)).toBeTruthy();
    expect(invoke).toHaveBeenCalledTimes(2);
    expect(invoke).toHaveBeenLastCalledWith("attachment_request", { method: "POST", path: "/attachments/preview", body: { id: a.id, conversation_id: "chat-a" } });
  });

  it.each(["resolve", "reject"] as const)("does not reopen or show an error after closing a pending request that later %ss", async (outcome) => {
    const request = deferred();
    vi.mocked(invoke).mockReturnValueOnce(request.promise);
    const user = userEvent.setup();
    render(<SavedAttachments items={[a]} conversationId="chat-a" />);
    const opener = screen.getByRole("button", { name: a.filename });
    await user.click(opener);
    await user.click(screen.getByRole("button", { name: "Close preview" }));
    expect(document.activeElement).toBe(opener);
    await act(async () => outcome === "resolve" ? request.resolve(preview(a)) : request.reject(new Error("Cancelled request failed")));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.queryByRole("status")).toBeNull();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("does not reopen an older request after closing the current preview", async () => {
    const first = deferred(), second = deferred();
    vi.mocked(invoke).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const user = userEvent.setup();
    render(<SavedAttachments items={[a, b]} conversationId="chat-a" />);
    await user.click(screen.getByRole("button", { name: a.filename }));
    await user.click(screen.getByRole("button", { name: b.filename }));
    await act(async () => second.resolve(preview(b)));
    await user.click(screen.getByRole("button", { name: "Close preview" }));
    await act(async () => first.resolve(preview(a)));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("closes a pending preview with Escape and ignores its eventual response", async () => {
    const request = deferred();
    vi.mocked(invoke).mockReturnValueOnce(request.promise);
    const user = userEvent.setup();
    render(<SavedAttachments items={[a]} conversationId="chat-a" />);
    const opener = screen.getByRole("button", { name: a.filename });
    await user.click(opener);
    await user.keyboard("{Escape}");
    await act(async () => request.resolve(preview(a)));
    expect(screen.queryByRole("status")).toBeNull();
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(document.activeElement).toBe(opener);
  });

  it("returns focus to the newest opener after replacing an open preview", async () => {
    const second = deferred();
    vi.mocked(invoke).mockResolvedValueOnce(preview(a)).mockReturnValueOnce(second.promise);
    const user = userEvent.setup();
    render(<SavedAttachments items={[a, b]} conversationId="chat-a" />);
    await user.click(screen.getByRole("button", { name: a.filename }));
    expect(screen.getByRole("dialog")).toBeTruthy();
    const opener = screen.getByRole("button", { name: b.filename });
    await user.click(opener);
    await act(async () => second.resolve(preview(b)));
    await user.click(screen.getByRole("button", { name: "Close preview" }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(document.activeElement).toBe(opener);
  });

  it.each(["saved", "draft"])("keeps keyboard focus inside the %s preview and returns it when closed", async (kind) => {
    const image: ChatAttachment = { ...b, filename: "shot.png", mime_type: "image/png", kind: undefined, data_url: "data:image/png;base64,AA==" };
    const user = userEvent.setup();
    if (kind === "saved") render(<SavedAttachments items={[image]} conversationId="chat-a" />);
    else render(<DraftAttachments items={[{ ...image, localId: "draft", status: "ready" }]} onRemove={vi.fn()} onRetry={vi.fn()} />);
    const opener = screen.getByRole("button", { name: kind === "saved" ? image.filename : `Preview ${image.filename}` });
    await user.click(opener);
    const close = screen.getByRole("button", { name: "Close preview" });
    expect(document.activeElement).toBe(close);
    await user.tab({ shift: true });
    expect(document.activeElement).toBe(screen.getByLabelText("Scrollable image preview"));
    await user.tab();
    expect(document.activeElement).toBe(close);
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(document.activeElement).toBe(opener);
    expect(invoke).not.toHaveBeenCalled();
  });

  it.each(["resolve", "reject"] as const)("discards requests across conversation changes, including returning to the original chat (%s)", async (outcome) => {
    const old = deferred(), current = deferred();
    vi.mocked(invoke).mockReturnValueOnce(old.promise).mockReturnValueOnce(current.promise);
    const user = userEvent.setup();
    const view = render(<SavedAttachments items={[a]} conversationId="chat-a" />);
    await user.click(screen.getByRole("button", { name: a.filename }));
    view.rerender(<SavedAttachments items={[b]} conversationId="chat-b" />);
    expect(screen.queryByRole("status")).toBeNull();
    view.rerender(<SavedAttachments items={[a]} conversationId="chat-a" />);
    await user.click(screen.getByRole("button", { name: a.filename }));
    await act(async () => outcome === "resolve" ? old.resolve(preview(b)) : old.reject(new Error("Stale conversation")));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.getByRole("status").textContent).toContain(a.filename);
    await act(async () => current.resolve(preview(a)));
    expect(screen.getByRole("dialog", { name: `Attachment preview: ${a.filename}` })).toBeTruthy();
  });

  it("clears a completed preview when its owning conversation changes", async () => {
    vi.mocked(invoke).mockResolvedValue(preview(a));
    const user = userEvent.setup();
    const view = render(<SavedAttachments items={[a]} conversationId="chat-a" />);
    await user.click(screen.getByRole("button", { name: a.filename }));
    expect(screen.getByRole("dialog")).toBeTruthy();
    view.rerender(<SavedAttachments items={[b]} conversationId="chat-b" />);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("discards work after unmount and does not affect a later instance", async () => {
    const old = deferred();
    vi.mocked(invoke).mockReturnValueOnce(old.promise).mockResolvedValueOnce(preview(b));
    const user = userEvent.setup();
    const first = render(<SavedAttachments items={[a]} conversationId="chat-a" />);
    await user.click(screen.getByRole("button", { name: a.filename }));
    first.unmount();
    render(<SavedAttachments items={[b]} conversationId="chat-a" />);
    await user.click(screen.getByRole("button", { name: b.filename }));
    await act(async () => old.resolve(preview(a)));
    expect(screen.getByRole("dialog", { name: `Attachment preview: ${b.filename}` })).toBeTruthy();
    expect(screen.queryByText(`Contents of ${a.filename}`)).toBeNull();
  });

  it("lets an inline image invalidate an earlier remote preview and preserves dialog keyboard focus", async () => {
    const old = deferred();
    const image: ChatAttachment = { ...b, filename: "shot.png", mime_type: "image/png", kind: undefined, data_url: "data:image/png;base64,AA==" };
    vi.mocked(invoke).mockReturnValueOnce(old.promise);
    const user = userEvent.setup();
    render(<SavedAttachments items={[a, image]} conversationId="chat-a" />);
    await user.click(screen.getByRole("button", { name: a.filename }));
    const opener = screen.getByRole("button", { name: image.filename });
    await user.click(opener);
    const close = screen.getByRole("button", { name: "Close preview" });
    expect(document.activeElement).toBe(close);
    await user.tab({ shift: true });
    expect(document.activeElement).toBe(screen.getByLabelText("Scrollable image preview"));
    await user.tab();
    expect(document.activeElement).toBe(close);
    await act(async () => old.resolve(preview(a)));
    expect(screen.getByRole("dialog", { name: `Attachment preview: ${image.filename}` })).toBeTruthy();
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(document.activeElement).toBe(opener);
    expect(invoke).toHaveBeenCalledTimes(1);
  });
});
