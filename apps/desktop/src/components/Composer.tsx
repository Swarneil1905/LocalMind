import { useEffect, useId, useRef, type RefObject } from "react";
import { ArrowUp, BookOpen, Brain, Globe, Mic, Paperclip, ShieldCheck, Square } from "lucide-react";
import type { CloudProvider, ProviderId } from "../lib/providers";
import type { AttachmentDraft } from "../lib/attachments";
import { DraftAttachments } from "./ChatAttachments";
import { ChatTools } from "./ChatTools";
import { ModelPicker } from "./ModelPicker";
import type { Message, ModelMode } from "../types";
import { useVoice } from "../hooks/useVoice";
import { VoiceControls } from "./VoiceControls";
import "./composer-controls.css";
import { useSpotlight } from "../hooks/useSpotlight";

interface ComposerProps {
  textareaRef: RefObject<HTMLTextAreaElement | null>;
  value: string;
  onChange: (value: string) => void;
  onSend: (text: string, turnId?: string) => Promise<boolean>;
  messages?: Message[];
  voiceScope?: string;
  onStop: () => void;
  isStreaming: boolean;
  disabled: boolean;
  memoryEnabled: boolean;
  onMemoryToggle: () => void;
  knowledgeEnabled: boolean;
  onKnowledgeToggle: () => void;
  webSearchEnabled: boolean;
  onWebSearchToggle: () => void;
  modelMode: ModelMode;
  onModelModeChange: (mode: ModelMode) => void;
  modelName: string;
  modelNames?: Partial<Record<ModelMode, string>>;
  onOpenKnowledge: () => void;
  provider?: ProviderId;
  providers?: CloudProvider[];
  localOnly?: boolean;
  onProviderChange?: (provider: ProviderId) => void;
  onOpenProviderSettings?: () => void;
  attachments?: AttachmentDraft[];
  onAddAttachments?: (files: File[]) => void;
  onRemoveAttachment?: (id: string) => void;
  onRetryAttachment?: (id: string) => void;
  attachmentBusy?: boolean;
  attachmentValid?: boolean;
  imageInput?: boolean;
  installedModels?: string[];
  onLocalModelChange?: (model: string) => void;
  attachmentNotice?: string;
  attachmentMode?: "vision" | "ocr";
  onAttachmentModeChange?: (mode: "vision" | "ocr") => void;
}

export function Composer(props: ComposerProps) {
  const spotlight = useSpotlight<HTMLDivElement>();
  const helpId = useId();
  const voiceId = useId();
  const picker = useRef<HTMLInputElement>(null);
  const {
    textareaRef,
    value,
    onChange,
    onStop,
    isStreaming,
    disabled,
    modelMode,
    onModelModeChange,
    modelName,
  } = props;
  const voice = useVoice({ ...props, scope: props.voiceScope || "new", messages: props.messages || [] });
  useEffect(() => {
    const el = textareaRef.current;
    if (el) {
      el.style.height = "auto";
      el.style.height = `${Math.min(el.scrollHeight, 200)}px`;
    }
  }, [value, textareaRef]);
  const send = () => {
    if (value.trim() && !disabled && !isStreaming && !voice.busy && !props.attachmentBusy && props.attachmentValid !== false && !(props.attachments?.some(item => item.mime_type?.startsWith("image/")) && props.imageInput === false)) void voice.send(value);
  };
  const stop = () => {
    voice.cancel();
    onStop();
    textareaRef.current?.focus({ preventScroll: true });
  };
  const context = [
    { name: "Memory", enabled: props.memoryEnabled, Icon: Brain },
    { name: "Knowledge", enabled: props.knowledgeEnabled, Icon: BookOpen },
    { name: "Web", enabled: props.webSearchEnabled, Icon: Globe },
  ].filter(item => item.enabled);
  return (
    <div className="composer-wrap">
      <div className="composer-box spotlight-surface" {...spotlight}
        onDragOver={event => { if (props.onAddAttachments && event.dataTransfer.types.includes("Files")) event.preventDefault(); }}
        onDrop={event => {
          if (!props.onAddAttachments || !event.dataTransfer.files.length) return;
          event.preventDefault();
          if (!disabled && !isStreaming) props.onAddAttachments(Array.from(event.dataTransfer.files));
        }}>
        {context.length > 0 && <div className="composer-context" aria-label="Enabled context">
          <span className="context-label">Context</span>
          {context.map(({ name, Icon }) => <span className="context-chip" key={name}>
            <Icon size={12} aria-hidden="true" />{name}
          </span>)}
        </div>}
        {props.onAttachmentModeChange && !!props.attachments?.length && <details className="page-footnote"><summary>Attachment reading</summary><label>Images <select value={props.attachmentMode || "vision"} onChange={event => props.onAttachmentModeChange?.(event.target.value as "vision" | "ocr")}><option value="vision">Vision model</option><option value="ocr">OCR text only (requires Tesseract)</option></select></label><p>OCR reads text, not charts or visual meaning. This choice applies to the next images you attach.</p></details>}
        {!!props.attachments?.length && <DraftAttachments items={props.attachments} onRemove={props.onRemoveAttachment || (() => {})} onRetry={props.onRetryAttachment || (() => {})} />}
        {props.attachmentNotice && <p role="alert" className="page-footnote">{props.attachmentNotice}</p>}
        {!!props.attachments?.some(item => item.mime_type?.startsWith("image/")) && props.imageInput === false && <p role="note" className="page-footnote">This model cannot read images. Choose an image-capable model or remove the attachment. Your draft stays here.</p>}
        <textarea
          ref={textareaRef}
          aria-label="Message LocalMind"
          aria-describedby={helpId}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder="Ask a question, explore an idea, or start a draft…"
          rows={1}
          onPaste={event => {
            if (!props.onAddAttachments || disabled || isStreaming) return;
            // Clipboard items and files may refer to the same image; use one source only.
            const images = Array.from(event.clipboardData.items).filter(item => item.kind === "file" && item.type.startsWith("image/"))
              .map(item => item.getAsFile()).filter((file): file is File => file !== null);
            if (!images.length) return;
            event.preventDefault();
            props.onAddAttachments(images);
            const text = event.clipboardData.getData("text/plain");
            if (text) {
              const start = event.currentTarget.selectionStart, end = event.currentTarget.selectionEnd;
              onChange(value.slice(0, start) + text + value.slice(end));
            }
          }}
          onKeyDown={(e) => {
            if (e.key === "Escape" && isStreaming && !e.nativeEvent.isComposing) {
              e.preventDefault();
              e.stopPropagation();
              stop();
              return;
            }
            if (
              e.key === "Enter" &&
              !e.shiftKey &&
              !e.nativeEvent.isComposing &&
              e.keyCode !== 229
            ) {
              e.preventDefault();
              send();
            }
          }}
        />
        <div className="composer-toolbar">
          <div className="composer-options">
            {props.onAddAttachments && <><input ref={picker} type="file" hidden accept="image/png,image/jpeg,image/webp,.pdf,.txt,.md,.csv" multiple aria-label="Choose attachments" onChange={event => {
              if (event.target.files) props.onAddAttachments?.(Array.from(event.target.files)); event.target.value = "";
            }} /><button className="icon-button" type="button" aria-label="Attach files" title="Attach screenshots or PDF/text documents (up to 8 MiB each)" disabled={disabled || isStreaming || props.attachmentBusy} onClick={() => picker.current?.click()}><Paperclip size={17} /></button></>}
            <ChatTools {...props} />
            <span className="composer-divider" aria-hidden="true" />
            {(!props.provider || props.provider === "ollama") && <ModelPicker value={modelMode} onChange={onModelModeChange} modelName={modelName}
              models={props.modelNames} disabled={isStreaming || voice.busy} />}
            {props.onProviderChange && <select className="cloud-model-select" aria-label="Chat provider" value={props.provider || "ollama"} disabled={isStreaming || voice.busy}
              onChange={event => {
                if (event.target.value === "settings") props.onOpenProviderSettings?.();
                else if (event.target.value.startsWith("local:")) { props.onLocalModelChange?.(event.target.value.slice(6)); props.onProviderChange?.("ollama"); }
                else props.onProviderChange?.(event.target.value as ProviderId);
              }}>
              <optgroup label="On this device"><option value="ollama">Ollama · {props.provider === "ollama" && props.imageInput === true ? "Text + images" : "Local"}</option>
                {(props.installedModels || []).map(name => <option key={name} value={`local:${name}`}>{name} · Local · Capability checked on selection</option>)}
              </optgroup>
              <optgroup label={props.localOnly ? "Cloud blocked by local-only setting" : "Your API accounts"}>
                {(props.providers || []).filter(provider => provider.connected && provider.model).map(provider => <option key={provider.id} value={provider.id} disabled={props.localOnly}>{provider.label} · {provider.model} · {provider.vision ? "Text + images" : "Text"}</option>)}
              </optgroup><option value="settings">Set up cloud models…</option>
            </select>}
          </div>
          <div className="composer-send">
            <button type="button" className={`composer-voice ${voice.open ? "is-open" : ""}`}
              aria-label="Voice controls" aria-expanded={voice.open} aria-controls={voice.open ? voiceId : undefined}
              title="Voice controls" onClick={() => voice.setOpen(!voice.open)}><Mic size={18} aria-hidden="true" /></button>
            {isStreaming ? (
              <button
                type="button"
                className="send-button stop-button"
                onClick={stop}
                aria-label="Stop generation"
                title="Stop generation (Escape in message field)"
              >
                <Square size={14} fill="currentColor" aria-hidden="true" />
              </button>
            ) : (
              <button
                type="button"
                className="send-button"
                onClick={send}
                disabled={disabled || !value.trim() || voice.busy || props.attachmentBusy || props.attachmentValid === false || (!!props.attachments?.some(item => item.mime_type?.startsWith("image/")) && props.imageInput === false)}
                aria-label="Send message"
                title="Send message (Enter)"
              >
                <ArrowUp size={19} aria-hidden="true" />
              </button>
            )}
          </div>
        </div>
      </div>
      <VoiceControls voice={voice} disabled={disabled} hideTrigger panelId={voiceId} />
      <div className="composer-caption" id={helpId}>
        <span>
          <ShieldCheck size={12} aria-hidden="true" />
          {props.provider && props.provider !== "ollama"
            ? `Cloud model · ${props.providers?.find(provider => provider.id === props.provider)?.label || props.provider}. Approved context leaves this device.`
            : props.webSearchEnabled
            ? "Web is available when needed. Search queries leave this device."
            : "Local model. Check important details."}
        </span>
        <span className="keyboard-hint">{isStreaming ? "Esc to stop" : "Shift + Enter for a new line"}</span>
      </div>
    </div>
  );
}
