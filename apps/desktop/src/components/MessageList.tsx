import { memo, useEffect, useLayoutEffect, useRef, useState } from "react";
import { ArrowDown, ExternalLink, RefreshCw } from "lucide-react";
import type { Message } from "../types";
import { Brand } from "./Brand";
import { AnswerContent, CopyButton, safeWebUrl } from "./AnswerContent";
import { ResponseDetails } from "./ResponseDetails";
import { ResponseFeedback } from "./ResponseFeedback";
import { KnowledgeCitation } from "./KnowledgeCitation";
import { SavedAttachments } from "./ChatAttachments";

interface MessageListProps {
  conversationId?: string;
  messages: Message[];
  isStreaming: boolean;
  isSearching?: boolean;
  followUpQuestions?: string[];
  onSendFollowUp?: (question: string) => void;
  onRegenerate?: () => void;
}

export function MessageList({
  conversationId,
  messages,
  isStreaming,
  isSearching,
  followUpQuestions = [],
  onSendFollowUp,
  onRegenerate,
}: MessageListProps) {
  const scroll = useRef<HTMLDivElement>(null);
  const content = useRef<HTMLDivElement>(null);
  const nearBottom = useRef(true);
  const [showJump, setShowJump] = useState(false);
  const lastUserId = [...messages].reverse().find((message) => message.role === "user")?.id;
  useLayoutEffect(() => {
    nearBottom.current = true;
    if (scroll.current) scroll.current.scrollTop = scroll.current.scrollHeight;
  }, [lastUserId]);
  useLayoutEffect(() => {
    const element = scroll.current;
    if (element && nearBottom.current) element.scrollTop = element.scrollHeight;
  }, [messages, isSearching, followUpQuestions, isStreaming]);
  useEffect(() => {
    if (!content.current || typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(() => {
      if (scroll.current && nearBottom.current) scroll.current.scrollTop = scroll.current.scrollHeight;
    });
    observer.observe(content.current);
    return () => observer.disconnect();
  }, []);
  const last = messages[messages.length - 1];
  const status = isStreaming
    ? isSearching ? "Finding sources" : "Response in progress"
    : last?.cancelled ? "Response stopped"
      : last?.error ? "Response could not be completed. You can try again."
        : last?.role === "assistant" ? "Response complete" : "";
  return (
    <div className="transcript-wrap">
      <div
        className="transcript"
        ref={scroll}
        role="log"
        aria-label="Conversation"
        aria-live="off"
        tabIndex={0}
        onScroll={() => {
          const element = scroll.current;
          if (!element) return;
          nearBottom.current = element.scrollHeight - element.scrollTop - element.clientHeight < 80;
          setShowJump(!nearBottom.current);
        }}
      >
        <div className="transcript-content" ref={content}>
          {messages.map((message, index) => (
            <MessageRow
              key={message.id}
              message={message}
              conversationId={conversationId}
              streaming={isStreaming && index === messages.length - 1}
              searching={!!isSearching}
              onRegenerate={index === messages.length - 1 ? onRegenerate : undefined}
            />
          ))}
          {!isStreaming && !last?.error && !last?.cancelled && followUpQuestions.length > 0 && onSendFollowUp && (
            <div className="followups">
              <span>Continue exploring</span>
              {followUpQuestions.map((question) => (
                <button type="button" key={question} onClick={() => onSendFollowUp(question)}>
                  <span aria-hidden="true">→</span>{question}
                </button>
              ))}
            </div>
          )}
        </div>
      </div>
      <span className="sr-only" role="status" aria-atomic="true">{status}</span>
      {showJump && (
        <button
          type="button"
          className="jump-latest"
          onClick={() => {
            nearBottom.current = true;
            if (scroll.current) scroll.current.scrollTop = scroll.current.scrollHeight;
            setShowJump(false);
          }}
        >
          <ArrowDown size={14} aria-hidden="true" /> Latest response
        </button>
      )}
    </div>
  );
}

const MessageRow = memo(function MessageRow({
  conversationId,
  message,
  streaming,
  searching,
  onRegenerate,
}: {
  message: Message;
  conversationId?: string;
  streaming: boolean;
  searching: boolean;
  onRegenerate?: () => void;
}) {
  const user = message.role === "user";
  const sources = (message.sources || []).map((source, index) => ({ ...source, index, safeUrl: safeWebUrl(source.url) })).filter((source) => source.safeUrl);
  return (
    <article
      className={`message-row ${user ? "message-user" : "message-assistant"} ${message.error ? "message-error" : ""}`}
      aria-label={user ? "Your message" : "LocalMind response"}
    >
      <div className="message-author">
        {!user && <Brand size={24} />}
        {user ? "You" : "LocalMind"}
        {streaming && <span className="activity-dot" aria-hidden="true" />}
      </div>
      {message.thinking && (
        <details className="reasoning">
          <summary>{message.isThinking ? "Thinking…" : "Model notes"}</summary>
          <p>{message.thinking}</p>
        </details>
      )}
      {user ? (
        <><div className="user-text">{message.content}</div>{!!message.attachments?.length && <SavedAttachments items={message.attachments} conversationId={message.conversation_id} />}</>
      ) : message.content ? (
        <AnswerContent conversationId={streaming ? undefined : conversationId} content={message.content} sources={message.sources} />
      ) : streaming ? (
        <p className="response-status">
          {searching ? "Finding sources…" : message.isThinking ? "Thinking…" : "Preparing a response…"}
        </p>
      ) : null}
      {sources.length > 0 && (
        <details className="message-sources">
          <summary>{sources.length} {sources.length === 1 ? "source" : "sources"}</summary>
          <div>
            {sources.map((source) => (
              <a key={`${source.index}-${source.url}`} href={source.safeUrl} target="_blank" rel="noopener noreferrer">
                <span><span className="source-number">{source.index + 1}</span>{source.title}<ExternalLink size={12} aria-hidden="true" /></span>
                <small>{sourceDomain(source.url)} · {publicationDate(source.published_at)}</small>
              </a>
            ))}
          </div>
        </details>
      )}
      {message.errorMessage && <p className="response-status" role="note">{message.errorMessage}</p>}
      {(message.sources || []).some(source => source.kind === "document") && <details className="message-sources"><summary>Document excerpts sent with this response</summary>{message.sources?.filter(source => source.kind === "document").map(source => <section key={source.title}><h4>{source.title}</h4><pre style={{ whiteSpace: "pre-wrap" }}>{source.snippet}</pre></section>)}</details>}
      {message.cancelled && <p className="response-status">Response stopped</p>}
      {!user && message.diagnostics && <ResponseDetails data={message.diagnostics} />}
      {!user && !streaming && !message.error && !message.cancelled && conversationId && message.content && <ResponseFeedback key={`${conversationId}:${message.id}`} conversationId={conversationId} content={message.content} />}
      {!user && message.usage && <p className="page-footnote">API usage: {message.usage.input_tokens ?? "unknown"} input tokens · {message.usage.output_tokens ?? "unknown"} output tokens · {message.usage.cost_usd == null ? "Cost unknown" : `Estimated $${message.usage.cost_usd.toFixed(4)}`}</p>}
      {(message.sources || []).some((source) => source.kind === "knowledge") && (
        <details className="message-sources">
          <summary>Local file passages</summary>
          {(message.sources || []).filter((source) => source.kind === "knowledge").map((source) =>
            <KnowledgeCitation key={source.chunk_id} source={source} />)}
        </details>
      )}
      {!user && !streaming && (message.content || onRegenerate) && (
        <div className="message-actions">
          {message.content && <CopyButton text={message.content} compact />}
          {onRegenerate && (
            <button type="button" onClick={onRegenerate}>
              <RefreshCw size={14} aria-hidden="true" /> Try again
            </button>
          )}
        </div>
      )}
    </article>
  );
});

function sourceDomain(url: string) {
  try { return new URL(url).hostname.replace(/^www\./, ""); } catch { return "Source"; }
}

function publicationDate(value?: string | null) {
  if (!value) return "Publication date unavailable";
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? "Publication date unavailable"
    : `Published ${date.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric", timeZone: "UTC" })}`;
}
