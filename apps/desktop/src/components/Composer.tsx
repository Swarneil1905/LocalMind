import { useEffect, useId, type RefObject } from "react";
import { ArrowUp, Square } from "lucide-react";
import { ChatTools } from "./ChatTools";
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
  const helpId = useId();
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
  const stop = () => {
    onStop();
    textareaRef.current?.focus({ preventScroll: true });
  };
  return (
    <div className="composer-wrap">
      <div className="composer-box">
        <textarea
          ref={textareaRef}
          aria-label="Message LocalMind"
          aria-describedby={helpId}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder="Message LocalMind…"
          rows={1}
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
          <ChatTools {...props} />
          <div className="composer-send">
            <select
              aria-label="Model mode"
              title={`Current model: ${modelName}`}
              value={modelMode}
              disabled={isStreaming}
              onChange={(e) => onModelModeChange(e.target.value as ModelMode)}
            >
              <option value="speed">Speed</option>
              <option value="balanced">Balanced</option>
            </select>
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
                disabled={disabled || !value.trim()}
                aria-label="Send message"
                title="Send message (Enter)"
              >
                <ArrowUp size={19} aria-hidden="true" />
              </button>
            )}
          </div>
        </div>
      </div>
      <div className="composer-caption" id={helpId}>
        <span>
          {props.webSearchEnabled
            ? "Web is available when needed. Search queries leave this device."
            : "Local model. Check important details."}
        </span>
        <span className="keyboard-hint">{isStreaming ? "Esc to stop" : "Shift + Enter for a new line"}</span>
      </div>
    </div>
  );
}
