// Spec reference: Section 14 (Composer)
//
// - Auto-resizing textarea, min 48px max 240px
// - Enter submits, Shift+Enter adds a new line
// - Toolbar: model mode selector (left), feature toggles (left), Send/Stop (right)
// - Stop rendered as a filled circle (Copilot-style)
// - Model mode selector lives here, not in the header

import { useCallback, useEffect, useRef, useState } from "react";
import { BookOpen, Brain, ChevronDown, Globe, Send } from "lucide-react";
import type { ModelMode } from "../types";

const MODE_LABELS: Record<ModelMode, string> = {
  speed: "Speed",
  balanced: "Balanced",
  boost: "Boost",
};

interface ComposerProps {
  onSend: (text: string) => void;
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
  ollamaRunning: boolean | null;
  /** Pre-fill the textarea with this text and focus it. Clear after use. */
  draft?: string;
  onDraftApplied?: () => void;
}

export function Composer({
  onSend,
  onStop,
  isStreaming,
  disabled,
  memoryEnabled,
  onMemoryToggle,
  knowledgeEnabled,
  onKnowledgeToggle,
  webSearchEnabled,
  onWebSearchToggle,
  modelMode,
  onModelModeChange,
  ollamaRunning,
  draft,
  onDraftApplied,
}: ComposerProps) {
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const [modeOpen, setModeOpen] = useState(false);
  const modeRef = useRef<HTMLDivElement>(null);

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

  // Close mode dropdown on outside click
  useEffect(() => {
    if (!modeOpen) return;
    const handler = (e: MouseEvent) => {
      if (modeRef.current && !modeRef.current.contains(e.target as Node)) {
        setModeOpen(false);
      }
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [modeOpen]);

  // When a draft is injected from outside, set it and focus
  useEffect(() => {
    if (!draft) return;
    const el = textareaRef.current;
    if (el) {
      el.value = draft;
      el.focus();
      el.selectionStart = el.selectionEnd = draft.length;
      resize();
    }
    onDraftApplied?.();
  }, [draft, resize, onDraftApplied]);

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        if (isStreaming || disabled) return;
        const value = textareaRef.current?.value.trim();
        if (!value) return;
        onSend(value);
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

  const toolBtn = (
    active: boolean,
    onClick: () => void,
    icon: React.ReactNode,
    label: string,
    title: string
  ) => (
    <button
      onClick={onClick}
      title={title}
      style={{
        display: "flex",
        alignItems: "center",
        gap: 5,
        height: 26,
        padding: "0 9px",
        borderRadius: 6,
        backgroundColor: active ? "var(--accent-dim)" : "transparent",
        border: `1px solid ${active ? "var(--accent)" : "var(--border)"}`,
        color: active ? "var(--accent)" : "var(--text-3)",
        fontSize: 11,
        fontWeight: active ? 500 : 400,
        cursor: "pointer",
        transition: "border-color 0.12s, color 0.12s, background-color 0.12s",
        whiteSpace: "nowrap",
      }}
    >
      {icon}
      {label}
    </button>
  );

  return (
    <div
      style={{
        borderTop: "1px solid var(--border)",
        padding: "10px 16px 10px",
        flexShrink: 0,
        backgroundColor: "var(--bg)",
      }}
    >
      <div
        style={{
          backgroundColor: "var(--surface)",
          border: "1px solid var(--border)",
          borderRadius: 14,
          display: "flex",
          flexDirection: "column",
          transition: "border-color 0.15s",
        }}
      >
        {/* Textarea */}
        <textarea
          ref={textareaRef}
          onKeyDown={handleKeyDown}
          onInput={resize}
          placeholder="Message LocalMind..."
          disabled={disabled}
          style={{
            resize: "none",
            border: "none",
            outline: "none",
            backgroundColor: "transparent",
            color: "var(--text)",
            fontSize: 14,
            lineHeight: 1.6,
            padding: "14px 16px 6px",
            minHeight: 44,
            maxHeight: 220,
            fontFamily: "inherit",
            overflowY: "auto",
            borderRadius: "14px 14px 0 0",
          }}
          rows={1}
        />

        {/* Toolbar */}
        <div
          style={{
            display: "flex",
            alignItems: "center",
            justifyContent: "space-between",
            padding: "6px 10px 10px",
            gap: 6,
          }}
        >
          {/* Left side: model mode + feature toggles */}
          <div style={{ display: "flex", gap: 5, alignItems: "center", flexWrap: "wrap" }}>
            {/* Model mode dropdown */}
            <div ref={modeRef} style={{ position: "relative" }}>
              <button
                onClick={() => setModeOpen((v) => !v)}
                disabled={ollamaRunning === false}
                title="Switch model mode"
                style={{
                  display: "flex",
                  alignItems: "center",
                  gap: 4,
                  height: 26,
                  padding: "0 9px",
                  borderRadius: 6,
                  backgroundColor: "var(--surface-2)",
                  border: "1px solid var(--border)",
                  color: "var(--text-2)",
                  fontSize: 11,
                  fontWeight: 500,
                  cursor: ollamaRunning === false ? "not-allowed" : "pointer",
                  opacity: ollamaRunning === false ? 0.5 : 1,
                }}
              >
                {MODE_LABELS[modelMode]}
                <ChevronDown size={10} strokeWidth={2} />
              </button>

              {modeOpen && (
                <div
                  style={{
                    position: "absolute",
                    bottom: "calc(100% + 6px)",
                    left: 0,
                    backgroundColor: "var(--surface)",
                    border: "1px solid var(--border)",
                    borderRadius: 8,
                    padding: 4,
                    zIndex: 100,
                    minWidth: 120,
                    boxShadow: "0 4px 16px rgba(0,0,0,0.2)",
                  }}
                >
                  {(["speed", "balanced"] as ModelMode[]).map((m) => (
                    <button
                      key={m}
                      onClick={() => { onModelModeChange(m); setModeOpen(false); }}
                      style={{
                        display: "flex",
                        alignItems: "center",
                        justifyContent: "space-between",
                        width: "100%",
                        padding: "7px 10px",
                        borderRadius: 5,
                        backgroundColor: modelMode === m ? "var(--surface-2)" : "transparent",
                        border: "none",
                        color: modelMode === m ? "var(--text)" : "var(--text-2)",
                        fontSize: 12,
                        fontWeight: modelMode === m ? 600 : 400,
                        cursor: "pointer",
                        textAlign: "left",
                        gap: 8,
                      }}
                    >
                      <span>{MODE_LABELS[m]}</span>
                      {modelMode === m && (
                        <span style={{ fontSize: 10, color: "var(--accent)" }}>&#10003;</span>
                      )}
                    </button>
                  ))}
                </div>
              )}
            </div>

            <span style={{ width: 1, height: 16, backgroundColor: "var(--border)", flexShrink: 0 }} />

            {toolBtn(
              memoryEnabled,
              onMemoryToggle,
              <Brain size={11} strokeWidth={1.5} />,
              "Memory",
              memoryEnabled ? "Memory on" : "Memory off"
            )}
            {toolBtn(
              knowledgeEnabled,
              onKnowledgeToggle,
              <BookOpen size={11} strokeWidth={1.5} />,
              "Knowledge",
              knowledgeEnabled ? "Knowledge on" : "Knowledge off"
            )}
            {toolBtn(
              webSearchEnabled,
              onWebSearchToggle,
              <Globe size={11} strokeWidth={1.5} />,
              "Search",
              webSearchEnabled ? "Web search on" : "Web search off"
            )}
          </div>

          {/* Right side: Send / Stop */}
          <div style={{ flexShrink: 0 }}>
            {isStreaming ? (
              <button
                onClick={onStop}
                title="Stop generation"
                style={{
                  display: "flex",
                  alignItems: "center",
                  justifyContent: "center",
                  width: 30,
                  height: 30,
                  borderRadius: "50%",
                  backgroundColor: "var(--accent)",
                  border: "none",
                  cursor: "pointer",
                }}
              >
                {/* Filled square stop icon */}
                <svg width="10" height="10" viewBox="0 0 10 10" fill="white">
                  <rect x="1" y="1" width="8" height="8" rx="1.5" />
                </svg>
              </button>
            ) : (
              <button
                onClick={handleSendClick}
                disabled={disabled}
                title="Send message (Enter)"
                style={{
                  display: "flex",
                  alignItems: "center",
                  justifyContent: "center",
                  width: 30,
                  height: 30,
                  borderRadius: "50%",
                  backgroundColor: disabled ? "var(--surface-2)" : "var(--accent)",
                  border: "none",
                  color: disabled ? "var(--text-3)" : "#fff",
                  cursor: disabled ? "not-allowed" : "pointer",
                  transition: "background-color 0.12s",
                }}
              >
                <Send size={13} strokeWidth={2} />
              </button>
            )}
          </div>
        </div>
      </div>

      <p
        style={{
          marginTop: 5,
          fontSize: 11,
          color: "var(--text-3)",
          textAlign: "center",
          opacity: 0.7,
        }}
      >
        Enter to send &middot; Shift+Enter for new line
      </p>
    </div>
  );
}
