// Spec reference: Section 14 (Composer)
//
// - Auto-resizing textarea, min 48px max 240px
// - Enter submits, Shift+Enter adds a new line
// - Toolbar with Send / Stop button
// - Stop button appears only while streaming

import { useCallback, useEffect, useRef } from "react";
import { Brain, Send, Square } from "lucide-react";

interface ComposerProps {
  onSend: (text: string) => void;
  onStop: () => void;
  isStreaming: boolean;
  disabled: boolean;
  memoryEnabled: boolean;
  onMemoryToggle: () => void;
}

export function Composer({ onSend, onStop, isStreaming, disabled, memoryEnabled, onMemoryToggle }: ComposerProps) {
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  // Auto-resize the textarea as the user types
  const resize = useCallback(() => {
    const el = textareaRef.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${Math.min(el.scrollHeight, 240)}px`;
  }, []);

  useEffect(() => {
    resize();
  }, [resize]);

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        if (isStreaming || disabled) return;
        const value = textareaRef.current?.value.trim();
        if (!value) return;
        onSend(value);
        // Clear and reset height
        if (textareaRef.current) {
          textareaRef.current.value = "";
          textareaRef.current.style.height = "auto";
        }
      }
    },
    [isStreaming, disabled, onSend]
  );

  const handleSendClick = useCallback(() => {
    const value = textareaRef.current?.value.trim();
    if (!value || isStreaming || disabled) return;
    onSend(value);
    if (textareaRef.current) {
      textareaRef.current.value = "";
      textareaRef.current.style.height = "auto";
    }
  }, [isStreaming, disabled, onSend]);

  return (
    <div
      style={{
        borderTop: "1px solid var(--border)",
        padding: "10px 16px 12px",
        flexShrink: 0,
        backgroundColor: "var(--bg)",
      }}
    >
      <div
        style={{
          backgroundColor: "var(--surface)",
          border: "1px solid var(--border)",
          borderRadius: 6,
          display: "flex",
          flexDirection: "column",
        }}
      >
        {/* Textarea */}
        <textarea
          ref={textareaRef}
          onKeyDown={handleKeyDown}
          onInput={resize}
          placeholder="Message LocalMind"
          disabled={disabled}
          style={{
            resize: "none",
            border: "none",
            outline: "none",
            backgroundColor: "transparent",
            color: "var(--text)",
            fontSize: 13,
            lineHeight: 1.5,
            padding: "10px 14px 6px",
            minHeight: 40,
            maxHeight: 200,
            fontFamily: "inherit",
            overflowY: "auto",
          }}
          rows={1}
        />

        {/* Toolbar */}
        <div
          style={{
            display: "flex",
            alignItems: "center",
            justifyContent: "space-between",
            padding: "4px 8px 8px",
            gap: 6,
          }}
        >
          {/* Memory toggle — left side */}
          <button
            onClick={onMemoryToggle}
            title={memoryEnabled ? "Memory on — click to disable" : "Memory off — click to enable"}
            style={{
              display: "flex",
              alignItems: "center",
              gap: 5,
              height: 24,
              padding: "0 8px",
              borderRadius: 4,
              backgroundColor: memoryEnabled ? "var(--accent-dim)" : "transparent",
              border: `1px solid ${memoryEnabled ? "var(--accent)" : "var(--border)"}`,
              color: memoryEnabled ? "var(--accent)" : "var(--text-3)",
              fontSize: 11,
              cursor: "pointer",
            }}
          >
            <Brain size={12} strokeWidth={1.5} />
            Memory
          </button>

          {/* Send / Stop — right side */}
          <div style={{ display: "flex", gap: 6 }}>
          {isStreaming ? (
            // Stop button — square icon, shown only while streaming
            <button
              onClick={onStop}
              title="Stop generation"
              style={{
                display: "flex",
                alignItems: "center",
                justifyContent: "center",
                width: 28,
                height: 28,
                borderRadius: 4,
                backgroundColor: "var(--surface-2)",
                color: "var(--text-2)",
                cursor: "pointer",
              }}
            >
              <Square size={14} strokeWidth={1.5} />
            </button>
          ) : (
            // Send button
            <button
              onClick={handleSendClick}
              disabled={disabled}
              title="Send message (Enter)"
              style={{
                display: "flex",
                alignItems: "center",
                justifyContent: "center",
                width: 28,
                height: 28,
                borderRadius: 4,
                backgroundColor: disabled ? "var(--surface-2)" : "var(--accent)",
                color: disabled ? "var(--text-3)" : "#fff",
                cursor: disabled ? "not-allowed" : "pointer",
              }}
            >
              <Send size={14} strokeWidth={1.5} />
            </button>
          )}
          </div>
        </div>
      </div>

      <p
        style={{
          marginTop: 6,
          fontSize: 11,
          color: "var(--text-3)",
          textAlign: "center",
        }}
      >
        Enter to send · Shift+Enter for new line
      </p>
    </div>
  );
}
