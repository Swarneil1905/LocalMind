// Spec reference: Section 14 (Main Chat Area - Message list) + Phase 5.5 Reasoning UI
//
// User messages:   right-aligned, surface-2 background, 14px
// Assistant msgs:  left-aligned, no background, full Markdown rendering
// Reasoning block: collapsible panel showing <think> content above the answer
// Follow-up chips: 3-4 clickable question suggestions after last assistant turn
//
// Markdown component overrides cover: p, h1-h6, ul, ol, li, blockquote,
// a, strong, em, hr, code (inline + block), table/th/td.

import { useCallback, useEffect, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { BrainCircuit, ChevronDown, ChevronRight, Copy, Check } from "lucide-react";
import { Message } from "../types";

interface MessageListProps {
  messages: Message[];
  isStreaming: boolean;
  followUpQuestions?: string[];
  onSendFollowUp?: (question: string) => void;
}

export function MessageList({
  messages,
  isStreaming,
  followUpQuestions = [],
  onSendFollowUp,
}: MessageListProps) {
  const bottomRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages, followUpQuestions]);

  if (messages.length === 0) {
    return <div style={{ flex: 1 }} />;
  }

  const showChips =
    !isStreaming &&
    followUpQuestions.length > 0 &&
    messages.length > 0 &&
    messages[messages.length - 1].role === "assistant";

  return (
    <div style={{ flex: 1, overflowY: "auto", padding: "16px 0" }}>
      <div
        style={{
          maxWidth: 740,
          margin: "0 auto",
          padding: "0 16px",
          display: "flex",
          flexDirection: "column",
          gap: 16,
        }}
      >
        {messages.map((msg, i) => (
          <MessageBubble
            key={msg.id}
            message={msg}
            streaming={
              isStreaming &&
              i === messages.length - 1 &&
              msg.role === "assistant"
            }
          />
        ))}

        {showChips && onSendFollowUp && (
          <FollowUpChips
            questions={followUpQuestions}
            onSelect={onSendFollowUp}
          />
        )}

        <div ref={bottomRef} />
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Follow-up question chips
// ---------------------------------------------------------------------------

interface FollowUpChipsProps {
  questions: string[];
  onSelect: (question: string) => void;
}

function FollowUpChips({ questions, onSelect }: FollowUpChipsProps) {
  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        gap: 6,
        marginTop: 4,
        paddingLeft: 0,
      }}
    >
      <p
        style={{
          fontSize: 11,
          color: "var(--text-3)",
          fontWeight: 500,
          letterSpacing: "0.03em",
          textTransform: "uppercase",
          marginBottom: 2,
        }}
      >
        Follow-up
      </p>
      {questions.map((q, i) => (
        <button
          key={i}
          onClick={() => onSelect(q)}
          style={{
            display: "flex",
            alignItems: "center",
            gap: 8,
            padding: "8px 12px",
            backgroundColor: "var(--surface)",
            border: "1px solid var(--border)",
            borderRadius: 6,
            fontSize: 13,
            color: "var(--text-2)",
            textAlign: "left",
            cursor: "pointer",
            transition: "border-color 0.15s, color 0.15s",
            width: "100%",
          }}
          onMouseEnter={(e) => {
            (e.currentTarget as HTMLButtonElement).style.borderColor =
              "var(--accent)";
            (e.currentTarget as HTMLButtonElement).style.color = "var(--text)";
          }}
          onMouseLeave={(e) => {
            (e.currentTarget as HTMLButtonElement).style.borderColor =
              "var(--border)";
            (e.currentTarget as HTMLButtonElement).style.color =
              "var(--text-2)";
          }}
        >
          <span
            style={{
              fontSize: 10,
              color: "var(--accent)",
              fontWeight: 600,
              flexShrink: 0,
              minWidth: 14,
            }}
          >
            {i + 1}
          </span>
          {q}
        </button>
      ))}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Markdown component overrides
// ---------------------------------------------------------------------------

const mdComponents: React.ComponentProps<typeof ReactMarkdown>["components"] =
  {
    p({ children }) {
      return (
        <p
          style={{
            fontSize: 14,
            lineHeight: 1.75,
            marginBottom: 12,
            marginTop: 0,
            color: "var(--text)",
          }}
        >
          {children}
        </p>
      );
    },
    h1({ children }) {
      return (
        <h1
          style={{
            fontSize: 20,
            fontWeight: 700,
            color: "var(--text)",
            marginTop: 20,
            marginBottom: 12,
            paddingBottom: 8,
            borderBottom: "1px solid var(--border)",
            lineHeight: 1.3,
          }}
        >
          {children}
        </h1>
      );
    },
    h2({ children }) {
      return (
        <h2
          style={{
            fontSize: 17,
            fontWeight: 600,
            color: "var(--text)",
            marginTop: 18,
            marginBottom: 10,
            lineHeight: 1.4,
          }}
        >
          {children}
        </h2>
      );
    },
    h3({ children }) {
      return (
        <h3
          style={{
            fontSize: 15,
            fontWeight: 600,
            color: "var(--text)",
            marginTop: 14,
            marginBottom: 8,
            lineHeight: 1.4,
          }}
        >
          {children}
        </h3>
      );
    },
    h4({ children }) {
      return (
        <h4
          style={{
            fontSize: 14,
            fontWeight: 600,
            color: "var(--text)",
            marginTop: 12,
            marginBottom: 6,
          }}
        >
          {children}
        </h4>
      );
    },
    ul({ children }) {
      return (
        <ul
          style={{
            paddingLeft: 22,
            marginBottom: 12,
            marginTop: 0,
            listStyleType: "disc",
          }}
        >
          {children}
        </ul>
      );
    },
    ol({ children }) {
      return (
        <ol
          style={{
            paddingLeft: 22,
            marginBottom: 12,
            marginTop: 0,
            listStyleType: "decimal",
          }}
        >
          {children}
        </ol>
      );
    },
    li({ children }) {
      return (
        <li
          style={{
            fontSize: 14,
            lineHeight: 1.7,
            marginBottom: 4,
            color: "var(--text)",
          }}
        >
          {children}
        </li>
      );
    },
    blockquote({ children }) {
      return (
        <blockquote
          style={{
            borderLeft: "3px solid var(--accent)",
            paddingLeft: 12,
            marginLeft: 0,
            marginRight: 0,
            marginTop: 0,
            marginBottom: 12,
            color: "var(--text-2)",
            fontStyle: "italic",
          }}
        >
          {children}
        </blockquote>
      );
    },
    a({ href, children }) {
      return (
        <a
          href={href}
          target="_blank"
          rel="noopener noreferrer"
          style={{
            color: "var(--accent)",
            textDecoration: "underline",
            textDecorationColor: "var(--accent-dim)",
          }}
        >
          {children}
        </a>
      );
    },
    strong({ children }) {
      return (
        <strong style={{ fontWeight: 600, color: "var(--text)" }}>
          {children}
        </strong>
      );
    },
    em({ children }) {
      return (
        <em style={{ fontStyle: "italic", color: "var(--text-2)" }}>
          {children}
        </em>
      );
    },
    hr() {
      return (
        <hr
          style={{
            border: "none",
            borderTop: "1px solid var(--border)",
            margin: "16px 0",
          }}
        />
      );
    },
    code({ className, children, ...props }) {
      const isBlock = className?.startsWith("language-");
      return isBlock ? (
        <pre
          style={{
            backgroundColor: "var(--surface-2)",
            borderRadius: 4,
            padding: "10px 14px",
            overflowX: "auto",
            fontSize: 13,
            margin: "8px 0",
          }}
        >
          <code
            style={{
              fontFamily: "monospace",
              color: "var(--text)",
            }}
          >
            {children}
          </code>
        </pre>
      ) : (
        <code
          style={{
            backgroundColor: "var(--surface-2)",
            borderRadius: 3,
            padding: "1px 5px",
            fontSize: 13,
            fontFamily: "monospace",
            color: "var(--text)",
          }}
          {...props}
        >
          {children}
        </code>
      );
    },
    table({ children }) {
      return (
        <div style={{ overflowX: "auto", margin: "8px 0" }}>
          <table
            style={{
              borderCollapse: "collapse",
              fontSize: 13,
              width: "100%",
            }}
          >
            {children}
          </table>
        </div>
      );
    },
    th({ children }) {
      return (
        <th
          style={{
            border: "1px solid var(--border)",
            padding: "6px 12px",
            backgroundColor: "var(--surface-2)",
            textAlign: "left",
            fontWeight: 600,
          }}
        >
          {children}
        </th>
      );
    },
    td({ children }) {
      return (
        <td
          style={{
            border: "1px solid var(--border)",
            padding: "6px 12px",
          }}
        >
          {children}
        </td>
      );
    },
  };

// ---------------------------------------------------------------------------
// Reasoning block
// ---------------------------------------------------------------------------

interface ReasoningBlockProps {
  thinking: string;
  isThinking: boolean;
}

function ReasoningBlock({ thinking, isThinking }: ReasoningBlockProps) {
  const [expanded, setExpanded] = useState(false);
  const [elapsedSec, setElapsedSec] = useState<number | null>(null);

  const startTimeRef = useRef<number | null>(null);
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const hasAutoExpandedRef = useRef(false);

  useEffect(() => {
    if (isThinking && !hasAutoExpandedRef.current) {
      setExpanded(true);
      hasAutoExpandedRef.current = true;
      startTimeRef.current = Date.now();
      timerRef.current = setInterval(() => {
        if (startTimeRef.current !== null) {
          setElapsedSec(Math.floor((Date.now() - startTimeRef.current) / 1000));
        }
      }, 500);
    }

    if (!isThinking && startTimeRef.current !== null) {
      setElapsedSec(Math.round((Date.now() - startTimeRef.current) / 1000));
      startTimeRef.current = null;
      if (timerRef.current) {
        clearInterval(timerRef.current);
        timerRef.current = null;
      }
    }
  }, [isThinking]);

  useEffect(() => {
    return () => {
      if (timerRef.current) clearInterval(timerRef.current);
    };
  }, []);

  const toggle = useCallback(() => setExpanded((v) => !v), []);

  if (!thinking && !isThinking) return null;

  const approxTokens = thinking
    ? thinking.split(/\s+/).filter(Boolean).length
    : 0;

  return (
    <div
      style={{
        marginBottom: 10,
        borderRadius: 6,
        border: "1px solid var(--border)",
        overflow: "hidden",
        fontSize: 12,
      }}
    >
      <button
        onClick={toggle}
        style={{
          display: "flex",
          alignItems: "center",
          gap: 6,
          width: "100%",
          padding: "6px 10px",
          background: "var(--surface-2)",
          color: "var(--text-3)",
          fontSize: 11,
          fontWeight: 500,
          cursor: "pointer",
          textAlign: "left",
          border: "none",
        }}
      >
        <BrainCircuit
          size={13}
          strokeWidth={1.5}
          style={{
            flexShrink: 0,
            color: isThinking ? "var(--accent)" : "var(--text-3)",
            animation: isThinking ? "spin 2s linear infinite" : "none",
          }}
        />
        <span style={{ flex: 1 }}>
          {isThinking ? "Reasoning..." : "Reasoning"}
        </span>

        {approxTokens > 0 && (
          <span
            style={{
              fontSize: 10,
              color: "var(--text-3)",
              marginRight: 4,
              fontVariantNumeric: "tabular-nums",
            }}
          >
            ~{approxTokens} tokens
            {elapsedSec !== null && <> &middot; {elapsedSec}s</>}
          </span>
        )}

        {expanded ? (
          <ChevronDown size={12} strokeWidth={1.5} />
        ) : (
          <ChevronRight size={12} strokeWidth={1.5} />
        )}
      </button>

      {expanded && thinking && (
        <div
          style={{
            padding: "10px 12px",
            background: "var(--bg)",
            color: "var(--text-3)",
            fontFamily: "monospace",
            fontSize: 12,
            lineHeight: 1.6,
            whiteSpace: "pre-wrap",
            wordBreak: "break-word",
            maxHeight: 320,
            overflowY: "auto",
          }}
        >
          {thinking}
          {isThinking && <StreamingCursor />}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Individual message bubble
// ---------------------------------------------------------------------------

interface MessageBubbleProps {
  message: Message;
  streaming?: boolean;
}

function MessageBubble({ message, streaming }: MessageBubbleProps) {
  const isUser = message.role === "user";

  if (isUser) {
    return (
      <div style={{ display: "flex", justifyContent: "flex-end" }}>
        <div
          style={{
            backgroundColor: "var(--surface-2)",
            borderRadius: 6,
            padding: "10px 14px",
            fontSize: 14,
            color: message.error ? "var(--text-3)" : "var(--text)",
            maxWidth: "80%",
            whiteSpace: "pre-wrap",
            wordBreak: "break-word",
          }}
        >
          {message.content}
        </div>
      </div>
    );
  }

  const hasThinking = !!(message.thinking || message.isThinking);
  const stillThinking = !!message.isThinking;
  const [hovered, setHovered] = useState(false);
  const [copied, setCopied] = useState(false);

  const handleCopy = useCallback(() => {
    navigator.clipboard.writeText(message.content).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    });
  }, [message.content]);

  return (
    <div
      style={{ display: "flex", justifyContent: "flex-start", flexDirection: "column" }}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
    >
      <div
        style={{
          fontSize: 14,
          color: message.error ? "var(--text-3)" : "var(--text)",
          maxWidth: "100%",
          lineHeight: 1.75,
          width: "100%",
        }}
        className="assistant-message"
      >
        {hasThinking && (
          <ReasoningBlock
            thinking={message.thinking ?? ""}
            isThinking={stillThinking}
          />
        )}

        {message.content ? (
          <>
            <ReactMarkdown remarkPlugins={[remarkGfm]} components={mdComponents}>
              {message.content}
            </ReactMarkdown>
            {streaming && !stillThinking && <StreamingCursor />}
          </>
        ) : (
          <span style={{ color: "var(--text-3)" }}>
            {streaming && !stillThinking ? <StreamingCursor /> : null}
          </span>
        )}
      </div>

      {/* Action bar — shown on hover, hidden while streaming */}
      {!streaming && (
        <div
          style={{
            display: "flex",
            gap: 4,
            marginTop: 4,
            opacity: hovered ? 1 : 0,
            transition: "opacity 0.15s",
            pointerEvents: hovered ? "auto" : "none",
          }}
        >
          <button
            onClick={handleCopy}
            title={copied ? "Copied!" : "Copy response"}
            style={{
              display: "flex",
              alignItems: "center",
              gap: 5,
              fontSize: 11,
              padding: "3px 8px",
              borderRadius: 4,
              backgroundColor: "var(--surface-2)",
              border: "1px solid var(--border)",
              color: copied ? "var(--accent)" : "var(--text-3)",
              cursor: "pointer",
              transition: "color 0.15s",
            }}
          >
            {copied
              ? <><Check size={11} strokeWidth={2} /> Copied</>
              : <><Copy size={11} strokeWidth={1.5} /> Copy</>
            }
          </button>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Streaming cursor
// ---------------------------------------------------------------------------

function StreamingCursor() {
  return (
    <span
      style={{
        display: "inline-block",
        width: 7,
        height: 14,
        backgroundColor: "var(--accent)",
        borderRadius: 1,
        verticalAlign: "text-bottom",
        marginLeft: 2,
        animation: "blink 1s step-end infinite",
      }}
    />
  );
}
