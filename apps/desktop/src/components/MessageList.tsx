// Spec reference: Section 14 (Main Chat Area — Message list)
//
// User messages:   right-aligned, surface-2 background, 14px
// Assistant msgs:  left-aligned, no background, Markdown rendered

import { useEffect, useRef } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
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
          alignItems: "center",
          justifyContent: "center",
          color: "var(--text-3)",
          fontSize: 13,
        }}
      >
        Select a conversation to begin
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
        {messages.map((msg) => (
          <MessageBubble key={msg.id} message={msg} />
        ))}

        {/* Streaming cursor — shown while the last assistant message is being generated */}
        {isStreaming && messages[messages.length - 1]?.role === "assistant" && (
          <StreamingCursor />
        )}

        <div ref={bottomRef} />
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Individual message
// ---------------------------------------------------------------------------

function MessageBubble({ message }: { message: Message }) {
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

  // Assistant message
  return (
    <div style={{ display: "flex", justifyContent: "flex-start" }}>
      <div
        style={{
          fontSize: 14,
          color: message.error ? "var(--text-3)" : "var(--text)",
          maxWidth: "100%",
          // Markdown rendering resets some browser defaults
          lineHeight: 1.6,
        }}
        className="assistant-message"
      >
        {message.content ? (
          <ReactMarkdown
            remarkPlugins={[remarkGfm]}
            components={{
              // Code blocks — dark surface, monospace font
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
                    <code style={{ fontFamily: "monospace", color: "var(--text)" }}>
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
              // Tables
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
        ) : (
          // Empty content while first token hasn't arrived yet
          <span style={{ color: "var(--text-3)" }}>...</span>
        )}
      </div>
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
        width: 8,
        height: 14,
        backgroundColor: "var(--accent)",
        borderRadius: 1,
        animation: "blink 1s step-end infinite",
      }}
    />
  );
}
