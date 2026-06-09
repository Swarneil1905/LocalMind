// Sidebar conversation list shown on the Chats page.
//
// Shows all saved conversations with:
//   - "New chat" button at the top
//   - Active conversation highlighted
//   - Delete button on hover
//   - Double-click to rename

import { useState } from "react";
import { MessageSquarePlus, Trash2 } from "lucide-react";
import { Conversation } from "../hooks/useConversations";

interface ConversationListProps {
  conversations: Conversation[];
  activeId: string | null;
  onNew: () => void;
  onSelect: (id: string) => void;
  onDelete: (id: string) => void;
  onRename: (id: string, title: string) => void;
}

export function ConversationList({
  conversations,
  activeId,
  onNew,
  onSelect,
  onDelete,
  onRename,
}: ConversationListProps) {
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editingTitle, setEditingTitle] = useState("");

  function startRename(conv: Conversation) {
    setEditingId(conv.id);
    setEditingTitle(conv.title);
  }

  function commitRename(id: string) {
    const title = editingTitle.trim();
    if (title) {
      onRename(id, title);
    }
    setEditingId(null);
  }

  return (
    <div
      style={{
        width: 220,
        minWidth: 220,
        display: "flex",
        flexDirection: "column",
        borderRight: "1px solid var(--border)",
        backgroundColor: "var(--surface)",
        overflow: "hidden",
        flexShrink: 0,
      }}
    >
      {/* New chat button */}
      <div style={{ padding: "10px 10px 6px" }}>
        <button
          onClick={onNew}
          style={{
            display: "flex",
            alignItems: "center",
            gap: 7,
            width: "100%",
            padding: "7px 10px",
            borderRadius: 5,
            backgroundColor: "var(--accent)",
            color: "#fff",
            fontSize: 12,
            fontWeight: 500,
            justifyContent: "center",
          }}
        >
          <MessageSquarePlus size={14} strokeWidth={1.5} />
          New chat
        </button>
      </div>

      {/* List */}
      <div style={{ flex: 1, overflowY: "auto", padding: "0 6px 8px" }}>
        {conversations.length === 0 ? (
          <p
            style={{
              fontSize: 12,
              color: "var(--text-3)",
              textAlign: "center",
              marginTop: 16,
              padding: "0 8px",
            }}
          >
            No saved chats yet
          </p>
        ) : (
          conversations.map((conv) => {
            const active = conv.id === activeId;
            const editing = editingId === conv.id;

            return (
              <ConversationRow
                key={conv.id}
                conv={conv}
                active={active}
                editing={editing}
                editingTitle={editingTitle}
                onSelect={() => onSelect(conv.id)}
                onDelete={() => onDelete(conv.id)}
                onDoubleClick={() => startRename(conv)}
                onEditChange={setEditingTitle}
                onEditCommit={() => commitRename(conv.id)}
                onEditCancel={() => setEditingId(null)}
              />
            );
          })
        )}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Single row
// ---------------------------------------------------------------------------

interface ConversationRowProps {
  conv: Conversation;
  active: boolean;
  editing: boolean;
  editingTitle: string;
  onSelect: () => void;
  onDelete: () => void;
  onDoubleClick: () => void;
  onEditChange: (v: string) => void;
  onEditCommit: () => void;
  onEditCancel: () => void;
}

function ConversationRow({
  conv,
  active,
  editing,
  editingTitle,
  onSelect,
  onDelete,
  onDoubleClick,
  onEditChange,
  onEditCommit,
  onEditCancel,
}: ConversationRowProps) {
  const [hovered, setHovered] = useState(false);

  return (
    <div
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      style={{
        display: "flex",
        alignItems: "center",
        gap: 4,
        padding: "2px 4px",
        borderRadius: 4,
        backgroundColor: active ? "var(--surface-2)" : "transparent",
        cursor: "pointer",
        marginBottom: 1,
      }}
    >
      {editing ? (
        <input
          autoFocus
          value={editingTitle}
          onChange={(e) => onEditChange(e.target.value)}
          onBlur={onEditCommit}
          onKeyDown={(e) => {
            if (e.key === "Enter") onEditCommit();
            if (e.key === "Escape") onEditCancel();
          }}
          style={{
            flex: 1,
            fontSize: 12,
            padding: "4px 6px",
            borderRadius: 3,
            border: "1px solid var(--accent)",
            backgroundColor: "var(--bg)",
            color: "var(--text)",
            outline: "none",
          }}
        />
      ) : (
        <button
          onClick={onSelect}
          onDoubleClick={onDoubleClick}
          style={{
            flex: 1,
            textAlign: "left",
            fontSize: 12,
            color: active ? "var(--text)" : "var(--text-2)",
            padding: "6px 6px",
            borderRadius: 3,
            whiteSpace: "nowrap",
            overflow: "hidden",
            textOverflow: "ellipsis",
            minWidth: 0,
          }}
        >
          {conv.title}
        </button>
      )}

      {/* Delete button - only visible on hover or when active */}
      {!editing && (hovered || active) && (
        <button
          onClick={(e) => {
            e.stopPropagation();
            onDelete();
          }}
          title="Delete conversation"
          style={{
            display: "flex",
            alignItems: "center",
            justifyContent: "center",
            width: 20,
            height: 20,
            borderRadius: 3,
            color: "var(--text-3)",
            flexShrink: 0,
          }}
        >
          <Trash2 size={12} strokeWidth={1.5} />
        </button>
      )}
    </div>
  );
}
