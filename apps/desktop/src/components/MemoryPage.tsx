// Spec reference: Phase 2 - Memory page
// Shows all stored memories with delete controls.

import { Brain, X } from "lucide-react";
import { Memory } from "../hooks/useMemory";

interface MemoryPageProps {
  memories: Memory[];
  onDelete: (id: string) => void;
}

export function MemoryPage({ memories, onDelete }: MemoryPageProps) {
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
            <Brain size={32} strokeWidth={1} style={{ margin: "0 auto 12px", display: "block" }} />
            <p style={{ fontSize: 13 }}>No memories yet</p>
            <p style={{ fontSize: 12, marginTop: 4 }}>
              Send a few messages with Memory on - facts will appear here automatically.
            </p>
          </div>
        )}

        {/* Memory list */}
        {memories.length > 0 && (
          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            {memories.map((m) => (
              <div
                key={m.id}
                style={{
                  display: "flex",
                  alignItems: "flex-start",
                  gap: 12,
                  backgroundColor: "var(--surface)",
                  border: "1px solid var(--border)",
                  borderRadius: 6,
                  padding: "10px 14px",
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
                  <p style={{ fontSize: 13, color: "var(--text)", lineHeight: 1.5 }}>
                    {m.content}
                  </p>
                  <p style={{ fontSize: 11, color: "var(--text-3)", marginTop: 3 }}>
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
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
