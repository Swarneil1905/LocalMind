// Spec reference: Phase 2 - Memory page
// Shows all stored memories with delete controls and linked-memory chips.

import { Brain, Link, X } from "lucide-react";
import { Memory, MemoryLink } from "../hooks/useMemory";

interface MemoryPageProps {
  memories: Memory[];
  links: MemoryLink[];
  onDelete: (id: string) => void;
  onDeleteLink: (linkId: string) => void;
}

const RELATION_COLORS: Record<string, string> = {
  related_to: "var(--text-3)",
  part_of: "#8b7cf8",
  elaborates: "#3b82f6",
  contradicts: "#ef4444",
  follows_from: "#22c55e",
};

function LinkChip({
  link,
  memoryId,
  onDelete,
}: {
  link: MemoryLink;
  memoryId: string;
  onDelete: (id: string) => void;
}) {
  const isFrom = link.from_id === memoryId;
  const otherContent = isFrom ? link.to_content : link.from_content;
  const label = isFrom ? link.relation : `← ${link.relation}`;
  const color = RELATION_COLORS[link.relation] ?? "var(--text-3)";

  return (
    <div
      style={{
        display: "flex",
        alignItems: "center",
        gap: 6,
        backgroundColor: "var(--surface-2)",
        border: "1px solid var(--border)",
        borderRadius: 4,
        padding: "4px 8px",
        fontSize: 11,
        color: "var(--text-2)",
        maxWidth: "100%",
      }}
    >
      <Link size={10} strokeWidth={1.5} color={color} style={{ flexShrink: 0 }} />
      <span style={{ color, fontWeight: 500, flexShrink: 0 }}>{label}</span>
      <span
        style={{
          overflow: "hidden",
          textOverflow: "ellipsis",
          whiteSpace: "nowrap",
          flex: 1,
        }}
        title={otherContent}
      >
        {otherContent}
      </span>
      <button
        onClick={() => onDelete(link.id)}
        title="Remove link"
        style={{
          display: "flex",
          alignItems: "center",
          color: "var(--text-3)",
          flexShrink: 0,
          marginLeft: 2,
        }}
      >
        <X size={10} strokeWidth={1.5} />
      </button>
    </div>
  );
}

export function MemoryPage({
  memories,
  links,
  onDelete,
  onDeleteLink,
}: MemoryPageProps) {
  const linksByMemory = new Map<string, MemoryLink[]>();
  for (const link of links) {
    for (const mid of [link.from_id, link.to_id]) {
      if (!linksByMemory.has(mid)) linksByMemory.set(mid, []);
      linksByMemory.get(mid)!.push(link);
    }
  }

  return (
    <div style={{ flex: 1, overflowY: "auto", padding: "24px 32px" }}>
      <div style={{ maxWidth: 640, margin: "0 auto" }}>
        {/* Header */}
        <div
          style={{
            display: "flex",
            alignItems: "center",
            gap: 10,
            marginBottom: 24,
            paddingBottom: 16,
            borderBottom: "1px solid var(--border)",
          }}
        >
          <Brain size={18} strokeWidth={1.5} color="var(--accent)" />
          <div>
            <h2 style={{ fontSize: 15, fontWeight: 600, color: "var(--text)" }}>
              Memory
            </h2>
            <p style={{ fontSize: 12, color: "var(--text-3)", marginTop: 2 }}>
              Facts extracted from your conversations. Used to personalize responses.
            </p>
          </div>
          <span
            style={{
              marginLeft: "auto",
              fontSize: 11,
              color: "var(--text-3)",
              backgroundColor: "var(--surface-2)",
              border: "1px solid var(--border)",
              borderRadius: 10,
              padding: "2px 8px",
            }}
          >
            {memories.length} {memories.length === 1 ? "item" : "items"}
          </span>
        </div>

        {/* Empty state */}
        {memories.length === 0 && (
          <div
            style={{
              textAlign: "center",
              padding: "48px 0",
              color: "var(--text-3)",
            }}
          >
            <Brain
              size={32}
              strokeWidth={1}
              style={{ margin: "0 auto 12px", display: "block" }}
            />
            <p style={{ fontSize: 13 }}>No memories yet</p>
            <p style={{ fontSize: 12, marginTop: 4 }}>
              Send a few messages with Memory on - facts will appear here automatically.
            </p>
          </div>
        )}

        {/* Memory list */}
        {memories.length > 0 && (
          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            {memories.map((m) => {
              const memLinks = linksByMemory.get(m.id) ?? [];
              return (
                <div
                  key={m.id}
                  style={{
                    backgroundColor: "var(--surface)",
                    border: "1px solid var(--border)",
                    borderRadius: 6,
                    padding: "10px 14px",
                  }}
                >
                  <div
                    style={{
                      display: "flex",
                      alignItems: "flex-start",
                      gap: 12,
                    }}
                  >
                    <span
                      style={{
                        width: 6,
                        height: 6,
                        borderRadius: "50%",
                        backgroundColor: "var(--accent)",
                        flexShrink: 0,
                        marginTop: 5,
                        display: "block",
                      }}
                    />
                    <div style={{ flex: 1 }}>
                      <p
                        style={{
                          fontSize: 13,
                          color: "var(--text)",
                          lineHeight: 1.5,
                        }}
                      >
                        {m.content}
                      </p>
                      <p
                        style={{
                          fontSize: 11,
                          color: "var(--text-3)",
                          marginTop: 3,
                        }}
                      >
                        {new Date(m.created_at).toLocaleDateString(undefined, {
                          month: "short",
                          day: "numeric",
                          year: "numeric",
                        })}
                      </p>
                    </div>
                    <button
                      onClick={() => onDelete(m.id)}
                      title="Delete memory"
                      style={{
                        display: "flex",
                        alignItems: "center",
                        justifyContent: "center",
                        width: 20,
                        height: 20,
                        borderRadius: 4,
                        color: "var(--text-3)",
                        flexShrink: 0,
                        marginTop: 1,
                      }}
                    >
                      <X size={12} strokeWidth={1.5} />
                    </button>
                  </div>

                  {/* Linked memory chips */}
                  {memLinks.length > 0 && (
                    <div
                      style={{
                        marginTop: 8,
                        marginLeft: 18,
                        display: "flex",
                        flexDirection: "column",
                        gap: 4,
                      }}
                    >
                      {memLinks.map((link) => (
                        <LinkChip
                          key={link.id}
                          link={link}
                          memoryId={m.id}
                          onDelete={onDeleteLink}
                        />
                      ))}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
