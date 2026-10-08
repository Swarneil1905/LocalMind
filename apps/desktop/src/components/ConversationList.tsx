import { useMemo, useState } from "react";
import { Check, MoreHorizontal, Pencil, Search, Trash2, X } from "lucide-react";
import type { Conversation } from "../hooks/useConversations";

interface Props {
  conversations: Conversation[];
  activeId: string | null;
  onSelect: (id: string) => void;
  onDelete: (id: string) => Promise<void>;
  onRename: (id: string, title: string) => Promise<void>;
}
function groupLabel(value: string) {
  const date = new Date(value);
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  if (date >= today) return "Today";
  const week = new Date(today);
  week.setDate(week.getDate() - 7);
  return date >= week ? "Previous 7 days" : "Earlier";
}
export function ConversationList({
  conversations,
  activeId,
  onSelect,
  onDelete,
  onRename,
}: Props) {
  const [query, setQuery] = useState("");
  const groups = useMemo(() => {
    const filtered = [...conversations]
      .filter((c) =>
        c.title.toLocaleLowerCase().includes(query.toLocaleLowerCase()),
      )
      .sort((a, b) => b.updated_at.localeCompare(a.updated_at));
    const result: Record<string, Conversation[]> = {};
    for (const conversation of filtered)
      (result[groupLabel(conversation.updated_at)] ??= []).push(conversation);
    return Object.entries(result);
  }, [conversations, query]);
  return (
    <section className="conversation-list" aria-label="Saved chats">
      <div className="history-heading">
        <h2>Recent chats</h2>
        <span>{conversations.length}</span>
      </div>
      <label className="history-search">
        <Search size={14} />
        <input
          aria-label="Search chats"
          placeholder="Search chats"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        {query && (
          <button aria-label="Clear chat search" onClick={() => setQuery("")}>
            <X size={13} />
          </button>
        )}
      </label>
      <div className="history-scroll">
        {groups.length === 0 ? (
          <p className="history-empty">
            {query
              ? "No chats match your search."
              : "Your conversations will appear here."}
          </p>
        ) : (
          groups.map(([label, rows]) => (
            <div key={label} className="history-group">
              <h3>{label}</h3>
              {rows.map((c) => (
                <ConversationRow
                  key={c.id}
                  conversation={c}
                  active={activeId === c.id}
                  onSelect={() => onSelect(c.id)}
                  onRename={(title) => onRename(c.id, title)}
                  onDelete={() => onDelete(c.id)}
                />
              ))}
            </div>
          ))
        )}
      </div>
    </section>
  );
}
function ConversationRow({
  conversation,
  active,
  onSelect,
  onRename,
  onDelete,
}: {
  conversation: Conversation;
  active: boolean;
  onSelect: () => void;
  onRename: (title: string) => Promise<void>;
  onDelete: () => Promise<void>;
}) {
  const [mode, setMode] = useState<"closed" | "actions" | "rename" | "delete">(
    "closed",
  );
  const [title, setTitle] = useState(conversation.title);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState("");
  async function commit(action: () => Promise<void>) {
    if (pending) return;
    setPending(true);
    setError("");
    try {
      await action();
      setMode("closed");
    } catch {
      setError("Couldn't save this change. Try again.");
    } finally {
      setPending(false);
    }
  }
  return (
    <div
      className={`conversation-row ${active ? "is-active" : ""}`}
      onKeyDown={(e) => {
        if (e.key === "Escape") {
          setMode("closed");
          setError("");
        }
      }}
    >
      <div className="conversation-line">
        <button
          className="conversation-title"
          aria-current={active ? "true" : undefined}
          title={conversation.title}
          onClick={onSelect}
        >
          {conversation.title}
        </button>
        <button
          className="conversation-more icon-button"
          aria-label={`Actions for ${conversation.title}`}
          aria-expanded={mode !== "closed"}
          onClick={() => {
            setMode(mode === "closed" ? "actions" : "closed");
            setError("");
          }}
        >
          <MoreHorizontal size={16} />
        </button>
      </div>
      {mode === "actions" && (
        <div className="conversation-actions">
          <button
            onClick={() => {
              setTitle(conversation.title);
              setMode("rename");
            }}
          >
            <Pencil size={13} />
            Rename
          </button>
          <button onClick={() => setMode("delete")}>
            <Trash2 size={13} />
            Delete
          </button>
        </div>
      )}
      {mode === "rename" && (
        <form
          className="conversation-rename"
          onSubmit={(e) => {
            e.preventDefault();
            if (title.trim()) void commit(() => onRename(title.trim()));
          }}
        >
          <input
            autoFocus
            aria-label="Chat name"
            value={title}
            maxLength={120}
            onChange={(e) => setTitle(e.target.value)}
          />
          <button
            className="icon-button"
            type="submit"
            disabled={pending || !title.trim()}
            aria-label="Save chat name"
          >
            <Check size={15} />
          </button>
          <button
            className="icon-button"
            type="button"
            aria-label="Cancel rename"
            onClick={() => setMode("closed")}
          >
            <X size={15} />
          </button>
        </form>
      )}
      {mode === "delete" && (
        <div className="conversation-confirm">
          <p>Delete this chat permanently?</p>
          <div>
            <button
              className="danger-text"
              disabled={pending}
              onClick={() => void commit(onDelete)}
            >
              {pending ? "Deleting…" : "Delete chat"}
            </button>
            <button disabled={pending} onClick={() => setMode("closed")}>
              Keep chat
            </button>
          </div>
        </div>
      )}
      {error && (
        <p className="inline-error" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}
