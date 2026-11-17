import { memo, useEffect, useLayoutEffect, useRef, useState } from "react";
import { ArrowDown, ChevronsRight, Cpu, ExternalLink, Globe, Info, RefreshCw } from "lucide-react";
import type { Message } from "../types";
import type { ProviderId } from "../lib/providers";
import { agentFor } from "../lib/agents";
import { BrandLogo } from "./BrandLogo";
import { AgentStatus } from "./AgentStatus";
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
  /** Provider and model that answer new messages; they pick each answer's logo and name. */
  provider?: ProviderId;
  modelName?: string;
  followUpQuestions?: string[];
  onSendFollowUp?: (question: string) => void;
  onRegenerate?: () => void;
  onContinue?: () => void;
  onEnableWeb?: () => void;
  onFallbackLocal?: () => void;
}

export function MessageList({
  conversationId,
  messages,
  isStreaming,
  isSearching,
  provider = "ollama",
  modelName,
  followUpQuestions = [],
  onSendFollowUp,
  onRegenerate,
  onContinue,
  onEnableWeb,
  onFallbackLocal,
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
              provider={provider}
              modelName={modelName}
              onRegenerate={index === messages.length - 1 ? onRegenerate : undefined}
              onContinue={index === messages.length - 1 ? onContinue : undefined}
              onEnableWeb={index === messages.length - 1 ? onEnableWeb : undefined}
              onFallbackLocal={index === messages.length - 1 ? onFallbackLocal : undefined}
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
  provider,
  modelName,
  onRegenerate,
  onContinue,
  onEnableWeb,
  onFallbackLocal,
}: {
  message: Message;
  conversationId?: string;
  streaming: boolean;
  searching: boolean;
  provider: ProviderId;
  modelName?: string;
  onRegenerate?: () => void;
  onContinue?: () => void;
  onEnableWeb?: () => void;
  onFallbackLocal?: () => void;
}) {
  const user = message.role === "user";
  // Saved answers carry the model that wrote them; a streaming one uses the active model.
  const model = message.diagnostics?.model || modelName;
  // A reply cut off by a length limit keeps its text; it is not a failure.
  const cutOff = !!message.error && !!message.content && /length limit|stopped before finishing/.test(message.errorMessage || "");
  const sources = (message.sources || []).map((source, index) => ({ ...source, index, safeUrl: safeWebUrl(source.url) })).filter((source) => source.safeUrl);
  return (
    <article
      className={`message-row ${user ? "message-user" : "message-assistant"} ${message.error && !cutOff ? "message-error" : ""}`}
      aria-label={user ? "Your message" : "LocalMind response"}
    >
      {!user && (
        <div className="message-author">
          <BrandLogo id={agentFor(model, provider)} label={model || "LocalMind"} size={18} />
          <span>{model || "LocalMind"}</span>
        </div>
      )}
      {message.thinking && (
        <details className="reasoning">
          <summary className={message.isThinking ? "shimmer-text" : undefined}>{message.isThinking ? "Thinking" : "Reasoning"}</summary>
          <p>{message.thinking}</p>
        </details>
      )}
      {user ? (
        <><div className="user-text">{message.content}</div>{!!message.attachments?.length && <SavedAttachments items={message.attachments} conversationId={message.conversation_id} />}</>
      ) : message.content ? (
        <AnswerContent conversationId={streaming ? undefined : conversationId} content={message.content} sources={message.sources} />
      ) : streaming ? (
        <AgentStatus phase={searching ? "searching" : sources.length ? "reading" : "thinking"} sources={sources.length} web={searching || sources.length > 0} />
      ) : null}
      {sources.length > 0 && !(streaming && !message.content) && (
        <section className="source-cards" aria-label="Sources">
          <h4>Sources</h4>
          <ol>
            {sources.map((source) => (
              <li key={`${source.index}-${source.url}`}>
                <a href={source.safeUrl} target="_blank" rel="noopener noreferrer">
                  <span className="source-card-title"><span className="source-number">{source.index + 1}</span>{source.title}<ExternalLink size={12} aria-hidden="true" /></span>
                  <small>{sourceLabel(source)} · {publicationDate(source.published_at)}</small>
                </a>
              </li>
            ))}
          </ol>
        </section>
      )}
      {message.errorMessage && <p className={`response-note ${cutOff ? "is-info" : ""}`} role="note"><Info size={14} aria-hidden="true" />{message.errorMessage}</p>}
      {(message.sources || []).some(source => source.kind === "document") && <details className="message-sources"><summary>Document excerpts sent with this response</summary>{message.sources?.filter(source => source.kind === "document").map(source => <section key={source.title}><h4>{source.title}</h4><pre style={{ whiteSpace: "pre-wrap" }}>{source.snippet}</pre></section>)}</details>}
      {message.cancelled && <p className="response-status">Response stopped</p>}
      {!user && message.usage && <p className="page-footnote">API usage: {message.usage.input_tokens ?? "unknown"} input tokens · {message.usage.output_tokens ?? "unknown"} output tokens · {message.usage.cost_usd == null ? "Cost unknown" : `Estimated $${message.usage.cost_usd.toFixed(4)}`}</p>}
      {(message.sources || []).filter((source) => source.kind === "memory").map((source) => source.memories?.length ? (
        <details className="message-sources memory-used" key="memory-used">
          <summary>Memories used ({source.memories.length})</summary>
          <ul>
            {source.memories.map((memory) => (
              <li key={memory.id}>{memory.content}{memory.matched.length > 0 && <small> · matched: {memory.matched.join(", ")}</small>}</li>
            ))}
          </ul>
        </details>
      ) : null)}
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
          {onEnableWeb && (
            <button type="button" onClick={onEnableWeb}>
              <Globe size={14} aria-hidden="true" /> Turn on Web and retry
            </button>
          )}
          {cutOff && onContinue && (
            <button type="button" className="action-primary" onClick={onContinue}>
              <ChevronsRight size={14} aria-hidden="true" /> Continue
            </button>
          )}
          {onRegenerate && (
            <button type="button" onClick={onRegenerate}>
              <RefreshCw size={14} aria-hidden="true" /> Try again
            </button>
          )}
          {onFallbackLocal && (
            <button type="button" onClick={onFallbackLocal}>
              <Cpu size={14} aria-hidden="true" /> Try on this computer
            </button>
          )}
        </div>
      )}
      {!user && (message.diagnostics || (!streaming && !message.error && !message.cancelled && conversationId && message.content)) && (
        <div className="message-meta">
          {message.diagnostics && <ResponseDetails data={message.diagnostics} />}
          {!streaming && !message.error && !message.cancelled && conversationId && message.content && <ResponseFeedback key={`${conversationId}:${message.id}`} conversationId={conversationId} content={message.content} />}
        </div>
      )}
    </article>
  );
});

function sourceDomain(url: string) {
  try { return new URL(url).hostname.replace(/^www\./, ""); } catch { return "Source"; }
}

// News-feed links all point at news.google.com; the publisher travels in the snippet.
function sourceLabel(source: { url: string; snippet?: string }) {
  const domain = sourceDomain(source.url);
  return domain === "news.google.com" && source.snippet ? source.snippet : domain;
}

function publicationDate(value?: string | null) {
  if (!value) return "Publication date unavailable";
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? "Publication date unavailable"
    : `Published ${date.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric", timeZone: "UTC" })}`;
}
