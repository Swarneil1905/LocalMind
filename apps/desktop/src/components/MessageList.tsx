/**
 * MessageList — Phase 2 iMessage-style chat UI.
 *
 * User messages:  right-aligned, indigo accent bubble
 * AI messages:    left-aligned, surface card + 🐾 avatar dot
 * Typing state:   animated 3-dot indicator
 * Tool chips:     inline connector-activity pills (from [[tool:...]] syntax)
 * Reasoning:      collapsible think block
 * Actions:        copy / regenerate on hover
 */

import { useCallback, useEffect, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  BrainCircuit, ChevronDown, ChevronRight, Copy, RefreshCw,
} from "lucide-react";
import { Message } from "../types";

// ─── Types ────────────────────────────────────────────────────────────────────

interface MessageListProps {
  messages: Message[];
  isStreaming: boolean;
  isSearching?: boolean;
  followUpQuestions?: string[];
  onSendFollowUp?: (question: string) => void;
  onRegenerate?: () => void;
}

// ─── Main component ───────────────────────────────────────────────────────────

export function MessageList({
  messages,
  isStreaming,
  isSearching = false,
  followUpQuestions = [],
  onSendFollowUp,
  onRegenerate,
}: MessageListProps) {
  const bottomRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages, followUpQuestions, isSearching]);

  if (messages.length === 0) return <div style={{ flex: 1 }} />;

  const lastMsg = messages[messages.length - 1];
  const showDots =
    isStreaming && lastMsg.role === "assistant" && !lastMsg.content && !lastMsg.isThinking;
  const showChips =
    !isStreaming && followUpQuestions.length > 0 && lastMsg.role === "assistant";

  return (
    <div style={{ flex: 1, overflowY: "auto", padding: "20px 0 8px" }}>
      <div style={{ maxWidth: 760, margin: "0 auto", padding: "0 20px", display: "flex", flexDirection: "column", gap: 4 }}>

        {messages.map((msg, i) => (
          <MessageBubble
            key={msg.id}
            message={msg}
            streaming={isStreaming && i === messages.length - 1 && msg.role === "assistant"}
            isLast={i === messages.length - 1}
            onRegenerate={onRegenerate}
          />
        ))}

        {/* Web search indicator */}
        {isSearching && (
          <div style={{ display: "flex", alignItems: "center", gap: 8, padding: "8px 0 4px 48px" }}>
            <ToolChip icon="🌐" label="Searching the web" status="running" />
          </div>
        )}

        {/* Typing dots */}
        {showDots && (
          <div style={{ display: "flex", alignItems: "flex-end", gap: 10, padding: "6px 0" }}>
            <BuddyAvatar />
            <div style={{
              background: "var(--surface)",
              border: "1px solid var(--border)",
              borderRadius: "4px 18px 18px 18px",
              padding: "14px 18px",
              display: "flex",
              gap: 5,
              alignItems: "center",
            }}>
              {[0, 1, 2].map(n => (
                <span key={n} style={{
                  width: 7, height: 7, borderRadius: "50%",
                  background: "var(--text-3)",
                  animation: `typingDot 1.2s ease-in-out ${n * 0.2}s infinite`,
                }} />
              ))}
            </div>
          </div>
        )}

        {/* Follow-up chips */}
        {showChips && onSendFollowUp && (
          <FollowUpChips questions={followUpQuestions} onSelect={onSendFollowUp} />
        )}

        <div ref={bottomRef} />
      </div>

      <style>{`
        @keyframes typingDot {
          0%, 60%, 100% { transform: translateY(0); opacity: 0.4; }
          30% { transform: translateY(-6px); opacity: 1; }
        }
        @keyframes fadeSlideIn {
          from { opacity: 0; transform: translateY(6px); }
          to   { opacity: 1; transform: translateY(0); }
        }
      `}</style>
    </div>
  );
}

// ─── Buddy avatar dot ─────────────────────────────────────────────────────────

function BuddyAvatar() {
  return (
    <div style={{
      width: 32, height: 32, borderRadius: "50%", flexShrink: 0,
      background: "linear-gradient(135deg, #6366f1 0%, #818cf8 100%)",
      display: "flex", alignItems: "center", justifyContent: "center",
      fontSize: 16, userSelect: "none",
    }}>
      🐾
    </div>
  );
}

// ─── Tool call chip ───────────────────────────────────────────────────────────

function ToolChip({ icon, label, status }: { icon: string; label: string; status: "running" | "success" | "error" }) {
  const colors = {
    running: { bg: "rgba(99,102,241,0.1)", text: "#818cf8", border: "rgba(99,102,241,0.25)" },
    success: { bg: "rgba(34,197,94,0.1)",  text: "#22c55e", border: "rgba(34,197,94,0.25)"  },
    error:   { bg: "rgba(239,68,68,0.1)",  text: "#ef4444", border: "rgba(239,68,68,0.25)"  },
  }[status];

  return (
    <div style={{
      display: "inline-flex", alignItems: "center", gap: 6,
      padding: "3px 10px", borderRadius: 20,
      background: colors.bg, border: `1px solid ${colors.border}`,
      fontSize: 11, fontWeight: 500, color: colors.text,
      animation: "fadeSlideIn 0.2s ease",
    }}>
      <span>{icon}</span>
      <span>{label}</span>
      {status === "running" && (
        <span style={{ width: 6, height: 6, borderRadius: "50%", background: colors.text, animation: "blink 1s step-end infinite" }} />
      )}
      {status === "success" && <span>✓</span>}
      {status === "error" && <span>✗</span>}
    </div>
  );
}

// ─── Individual message bubble ────────────────────────────────────────────────

interface MessageBubbleProps {
  message: Message;
  streaming?: boolean;
  isLast?: boolean;
  onRegenerate?: () => void;
}

function MessageBubble({ message, streaming, isLast, onRegenerate }: MessageBubbleProps) {
  const isUser = message.role === "user";
  const [hovered, setHovered] = useState(false);
  const [copied, setCopied] = useState(false);

  const handleCopy = useCallback(() => {
    navigator.clipboard.writeText(message.content).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    });
  }, [message.content]);

  if (isUser) {
    return (
      <div style={{ display: "flex", justifyContent: "flex-end", padding: "3px 0", animation: "fadeSlideIn 0.18s ease" }}>
        <div style={{
          background: "var(--accent)",
          borderRadius: "18px 18px 4px 18px",
          padding: "10px 16px",
          fontSize: 14, lineHeight: 1.6,
          color: "white",
          maxWidth: "70%",
          whiteSpace: "pre-wrap",
          wordBreak: "break-word",
          boxShadow: "0 2px 8px rgba(99,102,241,0.25)",
        }}>
          {message.content}
        </div>
      </div>
    );
  }

  // ── AI message ──
  const hasThinking = !!(message.thinking || message.isThinking);
  const stillThinking = !!message.isThinking;

  // If we're streaming but have no content yet (and no thinking block),
  // the typing dots outside handle this state — skip the empty bubble.
  if (streaming && !message.content && !hasThinking && !message.error) {
    return null;
  }

  return (
    <div
      style={{ display: "flex", flexDirection: "column", gap: 2, padding: "3px 0", animation: "fadeSlideIn 0.18s ease" }}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
    >
      <div style={{ display: "flex", alignItems: "flex-end", gap: 10 }}>
        <BuddyAvatar />
        <div style={{
          background: "var(--surface)",
          border: "1px solid var(--border)",
          borderRadius: "4px 18px 18px 18px",
          padding: "12px 16px",
          fontSize: 14,
          color: message.error ? "var(--text-3)" : "var(--text)",
          maxWidth: "calc(100% - 48px)",
          lineHeight: 1.7,
          boxShadow: "0 1px 4px rgba(0,0,0,0.12)",
        }}>
          {hasThinking && (
            <ReasoningBlock thinking={message.thinking ?? ""} isThinking={stillThinking} />
          )}

          {message.content ? (
            <>
              <ReactMarkdown remarkPlugins={[remarkGfm]} components={mdComponents}>
                {preprocessContent(message.content)}
              </ReactMarkdown>
              {streaming && !stillThinking && <StreamingCursor />}
            </>
          ) : (
            streaming && !stillThinking && <StreamingCursor />
          )}
        </div>
      </div>

      {/* Hover actions */}
      {!streaming && (
        <div style={{
          display: "flex", gap: 4, paddingLeft: 42,
          opacity: hovered ? 1 : 0,
          transition: "opacity 0.15s",
          pointerEvents: hovered ? "auto" : "none",
        }}>
          <ActionBtn icon={<Copy size={11} strokeWidth={1.5} />} label={copied ? "Copied" : "Copy"} onClick={handleCopy} active={copied} />
          {isLast && onRegenerate && (
            <ActionBtn icon={<RefreshCw size={11} strokeWidth={1.5} />} label="Regenerate" onClick={onRegenerate} />
          )}
        </div>
      )}
    </div>
  );
}

function ActionBtn({ icon, label, onClick, active }: { icon: React.ReactNode; label: string; onClick: () => void; active?: boolean }) {
  return (
    <button
      onClick={onClick}
      style={{
        display: "flex", alignItems: "center", gap: 4,
        fontSize: 11, padding: "3px 8px", borderRadius: 4,
        background: "var(--surface-2)", border: "1px solid var(--border)",
        color: active ? "var(--accent)" : "var(--text-3)", cursor: "pointer",
      }}
    >
      {icon}{label}
    </button>
  );
}

// ─── Follow-up chips ──────────────────────────────────────────────────────────

function FollowUpChips({ questions, onSelect }: { questions: string[]; onSelect: (q: string) => void }) {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 6, marginTop: 8, paddingLeft: 42 }}>
      <p style={{ fontSize: 11, color: "var(--text-3)", fontWeight: 500, textTransform: "uppercase", letterSpacing: "0.05em", margin: 0 }}>
        Ask a follow-up
      </p>
      {questions.map((q, i) => (
        <button
          key={i}
          onClick={() => onSelect(q)}
          style={{
            display: "flex", alignItems: "center", gap: 8,
            padding: "8px 14px",
            background: "var(--surface)", border: "1px solid var(--border)",
            borderRadius: 10, fontSize: 13, color: "var(--text-2)",
            textAlign: "left", cursor: "pointer",
            transition: "border-color 0.12s, color 0.12s",
          }}
          onMouseEnter={(e) => {
            (e.currentTarget as HTMLButtonElement).style.borderColor = "var(--accent)";
            (e.currentTarget as HTMLButtonElement).style.color = "var(--text)";
          }}
          onMouseLeave={(e) => {
            (e.currentTarget as HTMLButtonElement).style.borderColor = "var(--border)";
            (e.currentTarget as HTMLButtonElement).style.color = "var(--text-2)";
          }}
        >
          <span style={{ fontSize: 10, color: "var(--accent)", fontWeight: 600 }}>{i + 1}</span>
          {q}
        </button>
      ))}
    </div>
  );
}

// ─── Streaming cursor ─────────────────────────────────────────────────────────

function StreamingCursor() {
  return (
    <span style={{
      display: "inline-block", width: 7, height: 14,
      background: "var(--accent)", borderRadius: 1,
      verticalAlign: "text-bottom", marginLeft: 2,
      animation: "blink 1s step-end infinite",
    }} />
  );
}

// ─── Reasoning block ──────────────────────────────────────────────────────────

function ReasoningBlock({ thinking, isThinking }: { thinking: string; isThinking: boolean }) {
  const [expanded, setExpanded] = useState(false);
  const [elapsedSec, setElapsedSec] = useState<number | null>(null);
  const startRef = useRef<number | null>(null);
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const didAutoExpand = useRef(false);

  useEffect(() => {
    if (isThinking && !didAutoExpand.current) {
      setExpanded(true);
      didAutoExpand.current = true;
      startRef.current = Date.now();
      timerRef.current = setInterval(() => {
        if (startRef.current) setElapsedSec(Math.floor((Date.now() - startRef.current) / 1000));
      }, 500);
    }
    if (!isThinking && startRef.current) {
      setElapsedSec(Math.round((Date.now() - startRef.current) / 1000));
      startRef.current = null;
      if (timerRef.current) { clearInterval(timerRef.current); timerRef.current = null; }
    }
  }, [isThinking]);

  useEffect(() => () => { if (timerRef.current) clearInterval(timerRef.current); }, []);

  if (!thinking && !isThinking) return null;
  const tokens = thinking ? thinking.split(/\s+/).filter(Boolean).length : 0;

  return (
    <div style={{ marginBottom: 10, borderRadius: 6, border: "1px solid var(--border)", overflow: "hidden", fontSize: 12 }}>
      <button
        onClick={() => setExpanded(v => !v)}
        style={{
          display: "flex", alignItems: "center", gap: 6, width: "100%",
          padding: "6px 10px", background: "var(--surface-2)", color: "var(--text-3)",
          fontSize: 11, fontWeight: 500, cursor: "pointer", textAlign: "left", border: "none",
        }}
      >
        <BrainCircuit size={13} strokeWidth={1.5} style={{ flexShrink: 0, color: isThinking ? "var(--accent)" : "var(--text-3)", animation: isThinking ? "spin 2s linear infinite" : "none" }} />
        <span style={{ flex: 1 }}>{isThinking ? "Reasoning…" : "Reasoning"}</span>
        {tokens > 0 && <span style={{ fontSize: 10, color: "var(--text-3)", marginRight: 4 }}>~{tokens} tokens{elapsedSec !== null && ` · ${elapsedSec}s`}</span>}
        {expanded ? <ChevronDown size={12} strokeWidth={1.5} /> : <ChevronRight size={12} strokeWidth={1.5} />}
      </button>
      {expanded && thinking && (
        <div style={{ padding: "10px 12px", background: "var(--bg)", color: "var(--text-3)", fontFamily: "monospace", fontSize: 12, lineHeight: 1.6, whiteSpace: "pre-wrap", wordBreak: "break-word", maxHeight: 320, overflowY: "auto" }}>
          {thinking}
          {isThinking && <StreamingCursor />}
        </div>
      )}
    </div>
  );
}

// ─── Content preprocessor ────────────────────────────────────────────────────

function preprocessContent(content: string): string {
  return content
    .replace(/^(\s*[-*+])\s+[□☐]\s*/gm, "$1 [ ] ")
    .replace(/^(\s*[-*+])\s+[☑■✓✔]\s*/gm, "$1 [x] ")
    .replace(/^[□☐]\s+/gm, "- [ ] ")
    .replace(/^[☑■]\s+/gm, "- [x] ");
}

// ─── Markdown components ──────────────────────────────────────────────────────

const mdComponents: React.ComponentProps<typeof ReactMarkdown>["components"] = {
  input({ type, checked }) {
    if (type !== "checkbox") return null;
    return (
      <span style={{ display: "inline-flex", alignItems: "center", justifyContent: "center", width: 14, height: 14, border: `1.5px solid ${checked ? "var(--accent)" : "var(--text-3)"}`, borderRadius: 3, marginRight: 6, marginBottom: -2, backgroundColor: checked ? "var(--accent)" : "transparent", flexShrink: 0, verticalAlign: "middle" }}>
        {checked && <svg width="8" height="6" viewBox="0 0 8 6" fill="none"><path d="M1 3L3 5L7 1" stroke="white" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" /></svg>}
      </span>
    );
  },
  p({ children }) { return <p style={{ fontSize: 14, lineHeight: 1.75, marginBottom: 10, marginTop: 0, color: "var(--text)" }}>{children}</p>; },
  h1({ children }) { return <h1 style={{ fontSize: 19, fontWeight: 700, color: "var(--text)", marginTop: 18, marginBottom: 10, paddingBottom: 6, borderBottom: "1px solid var(--border)" }}>{children}</h1>; },
  h2({ children }) { return <h2 style={{ fontSize: 16, fontWeight: 600, color: "var(--text)", marginTop: 16, marginBottom: 8 }}>{children}</h2>; },
  h3({ children }) { return <h3 style={{ fontSize: 14, fontWeight: 600, color: "var(--text)", marginTop: 12, marginBottom: 6 }}>{children}</h3>; },
  h4({ children }) { return <h4 style={{ fontSize: 13, fontWeight: 600, color: "var(--text)", marginTop: 10, marginBottom: 4 }}>{children}</h4>; },
  ul({ children }) { return <ul style={{ paddingLeft: 20, marginBottom: 10, marginTop: 0, listStyleType: "disc" }}>{children}</ul>; },
  ol({ children }) { return <ol style={{ paddingLeft: 20, marginBottom: 10, marginTop: 0, listStyleType: "decimal" }}>{children}</ol>; },
  li({ children }) { return <li style={{ fontSize: 14, lineHeight: 1.7, marginBottom: 3, color: "var(--text)" }}>{children}</li>; },
  blockquote({ children }) { return <blockquote style={{ borderLeft: "3px solid var(--accent)", paddingLeft: 12, marginLeft: 0, marginBottom: 10, color: "var(--text-2)", fontStyle: "italic" }}>{children}</blockquote>; },
  a({ href, children }) { return <a href={href} target="_blank" rel="noopener noreferrer" style={{ color: "var(--accent)", textDecoration: "underline", textDecorationColor: "var(--accent-dim)" }}>{children}</a>; },
  strong({ children }) { return <strong style={{ fontWeight: 600, color: "var(--text)" }}>{children}</strong>; },
  em({ children }) { return <em style={{ fontStyle: "italic", color: "var(--text-2)" }}>{children}</em>; },
  hr() { return <hr style={{ border: "none", borderTop: "1px solid var(--border)", margin: "14px 0" }} />; },
  code({ className, children, ...props }) {
    const isBlock = className?.startsWith("language-");
    return isBlock ? (
      <pre style={{ background: "var(--surface-2)", borderRadius: 6, padding: "10px 14px", overflowX: "auto", fontSize: 13, margin: "8px 0", border: "1px solid var(--border)" }}>
        <code style={{ fontFamily: "monospace", color: "var(--text)" }}>{children}</code>
      </pre>
    ) : (
      <code style={{ background: "var(--surface-2)", borderRadius: 3, padding: "1px 5px", fontSize: 13, fontFamily: "monospace", color: "var(--text)" }} {...props}>{children}</code>
    );
  },
  table({ children }) { return <div style={{ overflowX: "auto", margin: "8px 0" }}><table style={{ borderCollapse: "collapse", fontSize: 13, width: "100%" }}>{children}</table></div>; },
  th({ children }) { return <th style={{ border: "1px solid var(--border)", padding: "6px 8px", background: "var(--surface-2)", fontWeight: 600, textAlign: "left" }}>{children}</th>; },
  td({ children }) { return <td style={{ border: "1px solid var(--border)", padding: "6px 8px" }}>{children}</td>; },
};

export { ToolChip };