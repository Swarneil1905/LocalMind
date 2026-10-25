import { useEffect, useId, useRef, useState } from "react";
import { BookOpen, Brain, Check, Globe, Library, SlidersHorizontal } from "lucide-react";

interface Props {
  memoryEnabled: boolean; onMemoryToggle: () => void;
  knowledgeEnabled: boolean; onKnowledgeToggle: () => void;
  webSearchEnabled: boolean; onWebSearchToggle: () => void;
  isStreaming: boolean; onOpenKnowledge: () => void;
}

export function ChatTools(props: Props) {
  const [open, setOpen] = useState(false);
  const id = useId();
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (!open) return;
    const dismiss = (event: PointerEvent) => {
      if (!root.current?.contains(event.target as Node)) setOpen(false);
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      event.preventDefault();
      event.stopPropagation();
      setOpen(false);
      trigger.current?.focus();
    };
    document.addEventListener("pointerdown", dismiss);
    document.addEventListener("keydown", escape, true);
    return () => {
      document.removeEventListener("pointerdown", dismiss);
      document.removeEventListener("keydown", escape, true);
    };
  }, [open]);
  const tools = [
    { name: "Memory", enabled: props.memoryEnabled, toggle: props.onMemoryToggle, Icon: Brain, detail: "Use your saved memories" },
    { name: "Knowledge", enabled: props.knowledgeEnabled, toggle: props.onKnowledgeToggle, Icon: BookOpen, detail: "Search your indexed documents" },
    { name: "Web", enabled: props.webSearchEnabled, toggle: props.onWebSearchToggle, Icon: Globe, detail: "Search when needed. Queries leave this device." },
  ];
  const active = tools.filter((tool) => tool.enabled).map((tool) => tool.name);
  return <div className="composer-tools">
    <div className="chat-tools" ref={root} onBlur={(event) => {
      if (!event.currentTarget.contains(event.relatedTarget as Node)) setOpen(false);
    }}>
      <button type="button" className="composer-tool" ref={trigger} aria-expanded={open} aria-controls={id}
        onClick={() => setOpen((value) => !value)}><SlidersHorizontal size={16} aria-hidden="true" /><span>Tools</span></button>
      {open && <section className="chat-tools-panel" id={id} aria-label="Chat tools">
        <h2>Context for this chat</h2>
        {tools.map(({ name, enabled, toggle, Icon, detail }) => <button type="button" key={name}
          className="context-option" aria-label={name} aria-pressed={enabled} disabled={props.isStreaming} onClick={toggle}>
          <Icon size={17} aria-hidden="true" /><span><strong>{name}</strong><small>{detail}</small></span>
          <span className="context-check" aria-hidden="true">{enabled && <Check size={13} />}</span>
        </button>)}
        <button type="button" className="library-option" onClick={() => { setOpen(false); props.onOpenKnowledge(); }}>
          <Library size={16} aria-hidden="true" />Open knowledge library
        </button>
      </section>}
    </div>
    <span className="context-summary" title={active.join(", ") || "No added context"} aria-label={`Active context: ${active.join(", ") || "none"}`}>
      {props.webSearchEnabled ? "Web on" : active.length > 1 ? `${active.length} sources of context` : active.length ? `${active[0]} on` : "No added context"}
    </span>
  </div>;
}
