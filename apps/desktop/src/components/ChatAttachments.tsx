import { useCallback, useEffect, useRef, useState } from "react";
import { Image, X } from "lucide-react";
import { attachmentRequest, type AttachmentDraft, type ChatAttachment } from "../lib/attachments";
import { providerError } from "../lib/providers";
import "./providers.css";
import { VisualCard } from "./VisualCard";
export function DraftAttachments({ items, onRemove, onRetry }: { items: AttachmentDraft[]; onRemove: (id: string) => void; onRetry: (id: string) => void }) {
  const [preview, setPreview] = useState<AttachmentDraft | null>(null);
  return <><div className="attachment-list" aria-label="Attachments">{items.map(item => <div className="attachment-item" key={item.localId}>
    {(item.data_url || item.kind === "document") ? <button type="button" aria-label={`Preview ${item.filename}`} onClick={() => setPreview(item)}>{item.data_url ? <img src={item.data_url} alt="" /> : "Document"}</button> : <Image aria-hidden="true" />}
    <span>{item.filename}<small role={item.status === "error" ? "alert" : "status"}>{item.status === "reading" ? "Preparing attachment…" : item.status === "error" ? item.error : item.kind === "document" ? `${item.pages?.length ?? 0} page(s) · extracted text` : `${item.width} × ${item.height}`}</small>
      {item.status === "error" && <button type="button" onClick={() => onRetry(item.localId)}>Retry attachment</button>}</span>
    <button type="button" aria-label={`Remove ${item.filename}`} onClick={() => onRemove(item.localId)}><X size={15} /></button>
  </div>)}</div>
    {preview && <AttachmentPreview item={preview} onClose={() => setPreview(null)} />}
  </>;
}
type SavedAttachmentsProps = { items: ChatAttachment[]; conversationId?: string };
type SavedPreview = { item: ChatAttachment; status: "loading" | "ready" } | { item: ChatAttachment; status: "error"; error: string };
export function SavedAttachments({ items, conversationId }: SavedAttachmentsProps) {
  // Each conversation owns its preview state and pending request lifetime.
  return <SavedAttachmentList key={conversationId} items={items} conversationId={conversationId} />;
}
function SavedAttachmentList({ items, conversationId }: SavedAttachmentsProps) {
  const [preview, setPreview] = useState<SavedPreview | null>(null);
  const request = useRef(0);
  const opener = useRef<HTMLElement | null>(null);
  useEffect(() => () => { request.current++; }, []);
  const returnFocus = useCallback(() => {
    if (opener.current?.isConnected) opener.current.focus();
  }, []);
  const close = () => {
    request.current++;
    setPreview(null);
    returnFocus();
  };
  const show = async (item: ChatAttachment, trigger?: HTMLElement) => {
    if (trigger) opener.current = trigger;
    else returnFocus();
    const current = ++request.current;
    if (item.data_url) { setPreview({ item, status: "ready" }); return; }
    setPreview({ item, status: "loading" });
    try {
      const result = await attachmentRequest<ChatAttachment>("POST", "/attachments/preview", { id: item.id, conversation_id: conversationId });
      if (current === request.current) setPreview({ item: result, status: "ready" });
    } catch (cause) {
      if (current === request.current) setPreview({ item, status: "error", error: providerError(cause) });
    }
  };
  return <div className="message-attachments" onKeyDown={event => {
    if (preview && preview.status !== "ready" && event.key === "Escape") close();
  }}>{items.map(item => <div key={item.id}><button type="button" onClick={event => void show(item, event.currentTarget)}>{item.filename}</button>{item.mime_type?.startsWith("image/") && <VisualCard source="" content="" conversationId={conversationId} attachmentId={item.id} />}</div>)}
    {preview?.status === "loading" && <p role="status">Loading preview for {preview.item.filename}… <button type="button" onClick={close}>Close preview</button></p>}
    {preview?.status === "error" && <p role="alert">Could not preview {preview.item.filename}: {preview.error} <button type="button" onClick={() => void show(preview.item)}>Retry preview for {preview.item.filename}</button></p>}
    {preview?.status === "ready" && <AttachmentPreview item={preview.item} onClose={close} onReturnFocus={returnFocus} />}
  </div>;
}
export function AttachmentPreview({ item, onClose, onReturnFocus }: { item: Partial<ChatAttachment> & { filename: string }; onClose: () => void; onReturnFocus?: () => void }) {
  const [zoom, setZoom] = useState(1);
  const modal = useRef<HTMLElement>(null);
  useEffect(() => {
    const prior = document.activeElement as HTMLElement | null;
    modal.current?.querySelector<HTMLButtonElement>("button")?.focus();
    return () => {
      if (onReturnFocus) onReturnFocus();
      else if (prior?.isConnected) prior.focus();
    };
  }, [onReturnFocus]);
  return <div className="cloud-review"><section ref={modal} role="dialog" aria-modal="true" aria-label={`Attachment preview: ${item.filename}`} onKeyDown={event => {
    if (event.key === "Escape") onClose();
    if (event.key === "Tab") {
      const elements = [...(modal.current?.querySelectorAll<HTMLElement>('button:not(:disabled),input,select,[tabindex="0"]') || [])];
      const first = elements[0], last = elements[elements.length-1];
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
    }
  }}>
    <button className="secondary-button" onClick={onClose}>Close preview</button>
    <h3>{item.filename}</h3>
    {item.kind === "document" ? <><p>{item.limitation}</p>{item.pages?.map(page => <section key={page.page}><h4>Page {page.page}{page.truncated ? " · excerpt truncated" : ""}{page.scanned ? " · no extractable text" : ""}</h4><pre style={{ whiteSpace: "pre-wrap", maxHeight: 260, overflow: "auto" }}>{page.text}</pre></section>)}</> : <>
      <label>Zoom {Math.round(zoom*100)}%<input type="range" min={0.5} max={4} step={0.25} value={zoom} onChange={event => setZoom(Number(event.target.value))} /></label>
      <div tabIndex={0} aria-label="Scrollable image preview" style={{ overflow: "auto", maxHeight: "70vh" }}><img src={item.data_url} alt={item.filename} style={{ width: `${zoom*100}%`, maxWidth: "none" }} /></div>
    </>}
  </section></div>;
}
