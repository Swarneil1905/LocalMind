import { Children, isValidElement, memo, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import remarkMath from "remark-math";
import rehypeKatex from "rehype-katex";
import { Check, Copy } from "lucide-react";
import "katex/dist/katex.min.css";
import type { WebSource } from "../types";
import { KnowledgeCitation } from "./KnowledgeCitation";

/** Generated links may open web pages, but never local files or application protocols. */
export function safeWebUrl(value?: string): string | undefined {
  if (!value || [...value].some((character) => character.charCodeAt(0) <= 32 || character.charCodeAt(0) === 127)) return undefined;
  try {
    const url = new URL(value);
    if (!["https:", "http:"].includes(url.protocol) || url.username || url.password) return undefined;
    return url.href;
  } catch {
    return undefined;
  }
}

interface MarkdownNode {
  type: string;
  value?: string;
  url?: string;
  children?: MarkdownNode[];
}

// Only plain-text references with a matching source become citation links.
// Existing links, code and math are left intact.
function remarkCitations({ sources }: { sources: WebSource[] }) {
  return (tree: MarkdownNode) => {
    const visit = (node: MarkdownNode) => {
      if (["link", "linkReference", "code", "inlineCode", "math", "inlineMath"].includes(node.type)) return;
      if (!node.children) return;
      node.children = node.children.flatMap((child) => {
        if (child.type !== "text" || !child.value) {
          visit(child);
          return [child];
        }
        const parts: MarkdownNode[] = [];
        let cursor = 0;
        for (const match of child.value.matchAll(/\[(K?\d{1,3})\]/g)) {
          const index = Number(match[1]) - 1;
          const local = sources.find((source) => source.kind === "knowledge" && source.citation === match[1]);
          const url = local?.chunk_id ? `#knowledge-${local.chunk_id}` : safeWebUrl(sources[index]?.url);
          if (!url) continue;
          if (match.index > cursor) parts.push({ type: "text", value: child.value.slice(cursor, match.index) });
          parts.push({ type: "link", url, children: [{ type: "text", value: match[1] }] });
          cursor = match.index + match[0].length;
        }
        if (!cursor) return [child];
        if (cursor < child.value.length) parts.push({ type: "text", value: child.value.slice(cursor) });
        return parts;
      });
    };
    visit(tree);
  };
}

function plainText(children: ReactNode): string {
  return Children.toArray(children).map((child) => {
    if (typeof child === "string" || typeof child === "number") return String(child);
    return isValidElement<{ children?: ReactNode }>(child) ? plainText(child.props.children) : "";
  }).join("");
}

export function CopyButton({ text, label = "Copy response", compact = false }: {
  text: string;
  label?: string;
  compact?: boolean;
}) {
  const [status, setStatus] = useState("");
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);
  return (
    <span className="copy-control">
      <button
        type="button"
        aria-label={label}
        title={label}
        onClick={async () => {
          try {
            await navigator.clipboard.writeText(text);
            setStatus("Copied");
          } catch {
            setStatus("Copy failed. Select the text and copy it manually.");
          }
          clearTimeout(timer.current);
          timer.current = setTimeout(() => setStatus(""), 2500);
        }}
      >
        {status === "Copied" ? <Check size={14} aria-hidden="true" /> : <Copy size={14} aria-hidden="true" />}
        {status === "Copied" ? "Copied" : status ? "Copy failed" : compact ? "Copy" : "Copy response"}
      </button>
      <span className="sr-only" role="status">{status}</span>
    </span>
  );
}

const CodeBlock: NonNullable<Components["pre"]> = ({ children }) => {
  const child = Children.toArray(children)[0];
  const code = isValidElement<{ className?: string; children?: ReactNode }>(child) ? child : null;
  const language = /(?:^|\s)language-([a-z0-9_+-]+)/i.exec(code?.props.className || "")?.[1];
  const text = plainText(code ? code.props.children : children).replace(/\n$/, "");
  return (
    <div className="code-block">
      <div className="code-toolbar">
        <span>{language || "Code"}</span>
        <CopyButton text={text} label="Copy code" compact />
      </div>
      <pre tabIndex={0} aria-label={`${language || "Code"} block`}>{children}</pre>
    </div>
  );
};

const EMPTY_SOURCES: WebSource[] = [];

export const AnswerContent = memo(function AnswerContent({ content, sources = EMPTY_SOURCES }: {
  content: string;
  sources?: WebSource[];
}) {
  const components = useMemo<Components>(() => ({
    pre: CodeBlock,
    a: ({ href, children }) => {
      const local = sources.find((source) => source.kind === "knowledge" && source.chunk_id && href === `#knowledge-${source.chunk_id}`);
      if (local) return <KnowledgeCitation source={local} inline />;
      const url = safeWebUrl(href);
      if (!url) return <span>{children}</span>;
      const label = plainText(children);
      const source = /^\d{1,3}$/.test(label) ? sources[Number(label) - 1] : undefined;
      const matched = source && safeWebUrl(source.url) === url;
      return (
        <a
          href={url}
          target="_blank"
          rel="noopener noreferrer"
          className={matched ? "source-citation" : undefined}
          aria-label={matched ? `Source ${label}: ${source.title}` : undefined}
          title={matched ? source.title : undefined}
        >{children}</a>
      );
    },
    img: ({ src, alt }) => {
      const url = safeWebUrl(src);
      return url ? <a href={url} target="_blank" rel="noopener noreferrer">{alt || "Open image"}</a> : <span>{alt || "Image unavailable"}</span>;
    },
    table: ({ children }) => <div className="table-scroll" role="region" aria-label="Response table" tabIndex={0}><table>{children}</table></div>,
  }), [sources]);
  return (
    <div className="markdown-content">
      <ReactMarkdown
        remarkPlugins={[remarkGfm, remarkMath, [remarkCitations, { sources }]]}
        rehypePlugins={[[rehypeKatex, { trust: false, strict: "ignore", maxExpand: 500, maxSize: 20 }]]}
        components={components}
      >{content}</ReactMarkdown>
    </div>
  );
});
