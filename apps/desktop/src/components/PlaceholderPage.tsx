// Placeholder for pages not yet built.
// Shown for Today, Projects, Knowledge, and Tasks until their phases land.

import type { LucideIcon } from "lucide-react";

interface PlaceholderPageProps {
  Icon: LucideIcon;
  title: string;
  description: string;
  phase: string;
}

export function PlaceholderPage({
  Icon,
  title,
  description,
  phase,
}: PlaceholderPageProps) {
  return (
    <div
      style={{
        flex: 1,
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
        padding: 32,
        gap: 12,
      }}
    >
      <Icon
        size={40}
        strokeWidth={1}
        style={{ color: "var(--text-3)", opacity: 0.4 }}
      />
      <div style={{ textAlign: "center", maxWidth: 300 }}>
        <p
          style={{
            fontSize: 15,
            fontWeight: 600,
            color: "var(--text-2)",
            marginBottom: 8,
          }}
        >
          {title}
        </p>
        <p
          style={{
            fontSize: 12,
            color: "var(--text-3)",
            lineHeight: 1.6,
            marginBottom: 12,
          }}
        >
          {description}
        </p>
        <span
          style={{
            display: "inline-block",
            fontSize: 11,
            color: "var(--text-3)",
            backgroundColor: "var(--surface-2)",
            border: "1px solid var(--border)",
            borderRadius: 10,
            padding: "2px 10px",
          }}
        >
          {phase}
        </span>
      </div>
    </div>
  );
}
