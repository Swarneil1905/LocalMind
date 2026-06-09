// Spec reference: Section 14 (Main Chat Area - Message list)
//
// User messages:   right-aligned, surface-2 background, 14px
// Assistant msgs:  left-aligned, no background, Markdown rendered
// Reasoning block: collapsible <think> content shown above the answer
// Streaming cursor is rendered inline inside the last assistant message.

import { useEffect, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { BrainCircuit, ChevronDown, ChevronRight } from "lucide-react";
import { Message } from "../types";

interface MessageListProps {
  messages: Message[];
  isStreaming: boolean;
}

export function MessageList({ messages, isStreaming }: MessageListProps) {
  const bottomRef = useRef<HTMLDivElement>(null);

  // Auto-scroll to the latest token as it arrives
  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  if (messages.length === 0) {
    return (
      <div
        style={{
          flex: 1,
          display: "flex",
          flexDirection: "column",
          alignItems: "center",
          justifyContent: "center",
          gap: 12,
          padding: 32,
        }}
      >
        <p
          style={{
            fontSize: 20,
            fontWeight: 600,
            color: "var(--text-2)",
            letterSpacing: "-0.02em",
          }}
        >
          LocalMind
        </p>
        <p style={{ fontSize: 13, color: "var(--text-3)" }}>
          Private, local AI - no cloud, no tracking.
        </p>
      </div>
    );
  }

  return (
    <div
      style={{
        flex: 1,
        overflowY: "auto",
        padding: "16px 0",
      }}
    >
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

        <div ref={bottomRef} />
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Reasoning block - collapsible, shown above the answer when thinking exists
// ---------------------------------------------------------------------------

interface ReasoningBlockProps {
  thinking: string;
  isThinking: boolean;
}

function ReasoningBlock({ thinking, isThinking }: ReasoningBlockProps) {
  const [expanded, setExpanded] = useState(false);

  // Auto-expand while the model is actively thinking so the user can watch
  // it reason. Collapses automatically when reasoning finishes.
  const wasThinkingRef = useRef(false);
  useEffect(() => {
    if (isThinking && !wasThinkingRef.current) {
      setExpanded(true);
      wasThinkingRef.current = true;
    }
    if (!isThinking && wasThinkingRef.current) {
      setExpanded(false);
      wasThinkingRef.current = false;
    }
  }, [isThinking]);

  if (!thinking && !isThinking) return null;

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
      {/* Header row */}
      <button
        onClick={() => setExpanded((v) => !v)}
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
        {expanded ? (
          <ChevronDown size={12} strokeWidth={1.5} />
        ) : (
          <ChevronRight size={12} strokeWidth={1.5} />
        )}
      </button>

      {/* Thinking content */}
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
// Individual message
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

  // Assistant message
  return (
    <div style={{ display: "flex", justifyContent: "flex-start" }}>
      <div
        style={{
          fontSize: 14,
          color: message.error ? "var(--text-3)" : "var(--text)",
          maxWidth: "100%",
          lineHeight: 1.6,
          width: "100%",
        }}
        className="assistant-message"
      >
        {/* Reasoning block - shown above the answer for R1 models */}
        {hasThinking && (
          <ReasoningBlock
            thinking={message.thinking ?? ""}
            isThinking={stillThinking}
          />
        )}

        {/* Answer */}
        {message.content ? (
          <>
            <ReactMarkdown
              remarkPlugins={[remarkGfm]}
              components={{
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
                        style={{ fontFamily: "monospace", color: "var(--text)" }}
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
              }}
            >
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
    </div>
  );
}

// ---------------------------------------------------------------------------
// Streaming cursor - inline, blinks after the last token
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
