import { useEffect, type RefObject } from "react";
import { ArrowUp, BookOpen, Brain, Globe, Library, Square } from "lucide-react";
import type { ModelMode } from "../types";

interface ComposerProps {
  textareaRef: RefObject<HTMLTextAreaElement | null>;
  value: string;
  onChange: (value: string) => void;
  onSend: (text: string) => Promise<boolean>;
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
  onOpenKnowledge: () => void;
}

export function Composer(props: ComposerProps) {
  const {
    textareaRef,
    value,
    onChange,
    onSend,
    onStop,
    isStreaming,
    disabled,
    modelMode,
    onModelModeChange,
    modelName,
  } = props;
  useEffect(() => {
    const el = textareaRef.current;
    if (el) {
      el.style.height = "auto";
      el.style.height = `${Math.min(el.scrollHeight, 200)}px`;
    }
  }, [value, textareaRef]);
  const send = () => {
    if (value.trim() && !disabled && !isStreaming) void onSend(value);
  };
  const tools = [
    {
      label: "Memory",
      active: props.memoryEnabled,
      toggle: props.onMemoryToggle,
      Icon: Brain,
      description: "Use saved memories in this conversation",
    },
    {
      label: "Knowledge",
      active: props.knowledgeEnabled,
      toggle: props.onKnowledgeToggle,
      Icon: BookOpen,
      description: "Search your indexed documents",
    },
    {
      label: "Web",
      active: props.webSearchEnabled,
      toggle: props.onWebSearchToggle,
      Icon: Globe,
      description: "Search the web; queries leave this device",
    },
  ];
  return (
    <div className="composer-wrap">
      <div className="composer-box">
        <textarea
          ref={textareaRef}
          aria-label="Message LocalMind"
          aria-describedby="composer-help"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder="Message LocalMind…"
          rows={2}
          onKeyDown={(e) => {
            if (
              e.key === "Enter" &&
              !e.shiftKey &&
              !e.nativeEvent.isComposing
            ) {
              e.preventDefault();
              send();
            }
          }}
        />
        <div className="composer-toolbar">
          <div className="composer-tools">
            <button
              className="icon-button"
              onClick={props.onOpenKnowledge}
              title="Open knowledge library"
              aria-label="Open knowledge library"
            >
              <Library size={17} />
            </button>
            {tools.map(({ label, active, toggle, Icon, description }) => (
              <button
                key={label}
                className="composer-tool"
                aria-pressed={active}
                title={description}
                onClick={toggle}
              >
                <Icon size={15} strokeWidth={1.7} />
                <span>{label}</span>
              </button>
            ))}
          </div>
          <div className="composer-send">
            <select
              aria-label="Model mode"
              title={`Current model: ${modelName}`}
              value={modelMode}
              onChange={(e) => onModelModeChange(e.target.value as ModelMode)}
            >
              <option value="speed">Speed</option>
              <option value="balanced">Balanced</option>
            </select>
            {isStreaming ? (
              <button
                className="send-button"
                onClick={onStop}
                aria-label="Stop generation"
                title="Stop generation"
              >
                <Square size={14} fill="currentColor" />
              </button>
            ) : (
              <button
                className="send-button"
                onClick={send}
                disabled={disabled || !value.trim()}
                aria-label="Send message"
                title="Send message (Enter)"
              >
                <ArrowUp size={19} />
              </button>
            )}
          </div>
        </div>
      </div>
      <div className="composer-caption" id="composer-help">
        <span>
          {props.webSearchEnabled
            ? "Web search is on. Queries leave this device."
            : "Local model. Check important details."}
        </span>
        <span className="keyboard-hint">Shift + Enter for a new line</span>
      </div>
    </div>
  );
}
