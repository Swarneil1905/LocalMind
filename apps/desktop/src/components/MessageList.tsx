import { useEffect, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { ArrowDown, Check, Copy, ExternalLink, RefreshCw } from "lucide-react";
import { Message } from "../types";
import { Brand } from "./Brand";

interface MessageListProps {
  messages: Message[];
  isStreaming: boolean;
  isSearching?: boolean;
  followUpQuestions?: string[];
  onSendFollowUp?: (question: string) => void;
  onRegenerate?: () => void;
}

export function MessageList({
  messages,
  isStreaming,
  isSearching,
  followUpQuestions = [],
  onSendFollowUp,
  onRegenerate,
}: MessageListProps) {
  const scroll = useRef<HTMLDivElement>(null);
  const nearBottom = useRef(true);
  const [showJump, setShowJump] = useState(false);
  const lastUserId = [...messages].reverse().find((m) => m.role === "user")?.id;
  useEffect(() => {
    nearBottom.current = true;
  }, [lastUserId]);
  useEffect(() => {
    const el = scroll.current;
    if (el && nearBottom.current) el.scrollTop = el.scrollHeight;
  }, [messages, isSearching, followUpQuestions]);
  const last = messages[messages.length - 1];
  return (
    <div className="transcript-wrap">
      <div
        className="transcript"
        ref={scroll}
        role="log"
        aria-label="Conversation"
        aria-busy={isStreaming}
        onScroll={() => {
          const el = scroll.current;
          if (!el) return;
          nearBottom.current =
            el.scrollHeight - el.scrollTop - el.clientHeight < 100;
          setShowJump(!nearBottom.current);
        }}
      >
        <div className="transcript-content">
          {messages.map((message, i) => (
            <MessageRow
              key={message.id}
              message={message}
              streaming={isStreaming && i === messages.length - 1}
              searching={!!isSearching}
              onRegenerate={
                i === messages.length - 1 ? onRegenerate : undefined
              }
            />
          ))}
          {!isStreaming &&
            !last?.error &&
            !last?.cancelled &&
            followUpQuestions.length > 0 && (
              <div className="followups">
                <span>Continue exploring</span>
                {followUpQuestions.map((q) => (
                  <button key={q} onClick={() => onSendFollowUp?.(q)}>
                    <MessageSquareIcon />
                    {q}
                  </button>
                ))}
              </div>
            )}
        </div>
      </div>
      {showJump && (
        <button
          className="jump-latest"
          onClick={() => {
            nearBottom.current = true;
            scroll.current?.scrollTo({
              top: scroll.current.scrollHeight,
              behavior: "smooth",
            });
          }}
        >
          <ArrowDown size={14} /> Latest response
        </button>
      )}
    </div>
  );
}

function MessageRow({
  message,
  streaming,
  searching,
  onRegenerate,
}: {
  message: Message;
  streaming: boolean;
  searching: boolean;
  onRegenerate?: () => void;
}) {
  const [copyState, setCopyState] = useState("");
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);
  const user = message.role === "user";
  return (
    <article
      className={`message-row ${user ? "message-user" : "message-assistant"} ${message.error ? "message-error" : ""}`}
    >
      <div className="message-author">
        {!user && <Brand size={23} />}
        {user ? "You" : "LocalMind"}
        {streaming && <span className="activity-dot" />}
      </div>
      {message.thinking && (
        <details className="reasoning">
          <summary>
            {message.isThinking ? "Thinking…" : "Model reasoning"}
          </summary>
          <p>{message.thinking}</p>
        </details>
      )}
      {user ? (
        <div className="user-text">{message.content}</div>
      ) : (
        <div className="markdown-content">
          {message.content ? (
            <ReactMarkdown
              remarkPlugins={[remarkGfm]}
              components={{
                a: ({ href, children }) => (
                  <a href={href} target="_blank" rel="noopener noreferrer">
                    {children}
                  </a>
                ),
                img: ({ src, alt }) => (
                  <a href={src} target="_blank" rel="noopener noreferrer">
                    {alt || "Open image"}
                  </a>
                ),
              }}
            >
              {message.content}
            </ReactMarkdown>
          ) : streaming ? (
            <p className="response-status" role="status">
              {searching
                ? "Finding sources…"
                : message.isThinking
                  ? "Thinking…"
                  : "Preparing a response…"}
            </p>
          ) : null}
        </div>
      )}
      {message.sources && message.sources.length > 0 && (
        <details className="message-sources">
          <summary>
            {message.sources.length}{" "}
            {message.sources.length === 1 ? "source" : "sources"}
          </summary>
          <div>
            {message.sources.map((source) => (
              <a
                key={source.url}
                href={source.url}
                target="_blank"
                rel="noopener noreferrer"
              >
                <span>
                  {source.title}
                  <ExternalLink size={12} />
                </span>
                <small>
                  {sourceDomain(source.url)} —{" "}
                  {publicationDate(source.published_at)}
                </small>
              </a>
            ))}
          </div>
        </details>
      )}
      {message.cancelled && <p className="response-status">Response stopped</p>}
      {!user && !streaming && (message.content || onRegenerate) && (
        <div className="message-actions">
          {message.content && (
            <button
              aria-label={copyState || "Copy response"}
              onClick={async () => {
                try {
                  await navigator.clipboard.writeText(message.content);
                  setCopyState("Copied");
                } catch {
                  setCopyState("Copy failed");
                }
                clearTimeout(timer.current);
                timer.current = setTimeout(() => setCopyState(""), 2000);
              }}
            >
              {copyState === "Copied" ? (
                <Check size={13} />
              ) : (
                <Copy size={13} />
              )}
              {copyState || "Copy"}
            </button>
          )}
          {onRegenerate && (
            <button onClick={onRegenerate}>
              <RefreshCw size={13} />
              Try again
            </button>
          )}
        </div>
      )}
    </article>
  );
}

function MessageSquareIcon() {
  return <span aria-hidden="true">→</span>;
}
function sourceDomain(url: string) {
  try {
    return new URL(url).hostname.replace(/^www\./, "");
  } catch {
    return "Source";
  }
}
function publicationDate(value?: string | null) {
  if (!value) return "Publication date unavailable";
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? "Publication date unavailable"
    : `Published ${date.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric", timeZone: "UTC" })}`;
}
