/**
 * TodayPage — LocalMind Phase 2 landing page.
 * Shows greeting, connector stats, and quick actions.
 */
import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";

function getGreeting(): string {
  const h = new Date().getHours();
  if (h < 12) return "Good morning";
  if (h < 17) return "Good afternoon";
  return "Good evening";
}

function formatDate(): string {
  return new Date().toLocaleDateString("en-US", {
    weekday: "long",
    month: "long",
    day: "numeric",
  });
}

interface ConnectorStats {
  gmail: { connected: boolean; count: number };
  whatsapp: { connected: boolean; count: number };
}

interface QuickAction {
  label: string;
  icon: string;
  prompt: string;
}

const QUICK_ACTIONS: QuickAction[] = [
  { label: "Summarize my emails", icon: "📧", prompt: "Can you summarize my recent emails?" },
  { label: "What's on today?", icon: "📅", prompt: "What do I have going on today?" },
  { label: "Draft a reply", icon: "✍️", prompt: "Help me draft a reply to:" },
  { label: "Catch me up", icon: "⚡", prompt: "Give me a quick catch-up on what I missed." },
];

export function TodayPage({ onNavigateToChat }: { onNavigateToChat: (prompt?: string) => void }) {
  const [stats, setStats] = useState<ConnectorStats>({
    gmail: { connected: false, count: 0 },
    whatsapp: { connected: false, count: 0 },
  });
  const [loadingStats, setLoadingStats] = useState(true);

  useEffect(() => {
    async function fetchStats() {
      try {
        const connectors = await invoke<{ id: string; status: string }[]>("list_connectors");
        const gmailConn = connectors.find((c) => c.id === "gmail");
        const waConn = connectors.find((c) => c.id === "whatsapp_web");
        setStats({
          gmail: { connected: gmailConn?.status === "connected", count: 0 },
          whatsapp: { connected: waConn?.status === "connected", count: 0 },
        });
      } catch {
        // sidecar not ready yet
      } finally {
        setLoadingStats(false);
      }
    }
    fetchStats();
  }, []);

  const connectedCount = [stats.gmail.connected, stats.whatsapp.connected].filter(Boolean).length;
  const greeting = getGreeting();
  const dateStr = formatDate();

  return (
    <div
      style={{
        flex: 1,
        overflowY: "auto",
        padding: "32px 40px",
        display: "flex",
        flexDirection: "column",
        gap: 28,
        maxWidth: 860,
        margin: "0 auto",
        width: "100%",
      }}
    >
      {/* ── Greeting card ────────────────────────────────────── */}
      <div
        style={{
          background: "linear-gradient(135deg, rgba(99,102,241,0.15) 0%, rgba(99,102,241,0.04) 100%)",
          border: "1px solid rgba(99,102,241,0.25)",
          borderRadius: 16,
          padding: "24px 28px",
          display: "flex",
          alignItems: "flex-start",
          gap: 20,
        }}
      >
        <div style={{ fontSize: 40, lineHeight: 1 }}>🌅</div>
        <div style={{ flex: 1 }}>
          <p style={{ fontSize: 11, color: "var(--text-3)", fontWeight: 500, textTransform: "uppercase", letterSpacing: "0.08em", marginBottom: 4 }}>
            {dateStr}
          </p>
          <h1 style={{ fontSize: 22, fontWeight: 700, color: "var(--text)", margin: "0 0 10px", letterSpacing: "-0.02em" }}>
            {greeting}.
          </h1>
          {loadingStats ? (
            <p style={{ fontSize: 13, color: "var(--text-3)", margin: 0 }}>Checking your connections…</p>
          ) : connectedCount === 0 ? (
            <p style={{ fontSize: 13, color: "var(--text-3)", margin: 0, lineHeight: 1.5 }}>
              Connect your apps to see what's on your plate today. →{" "}
              <span
                onClick={() => onNavigateToChat()}
                style={{ color: "var(--accent)", cursor: "pointer", textDecoration: "underline" }}
              >
                Go to Connections
              </span>
            </p>
          ) : (
            <p style={{ fontSize: 13, color: "var(--text-2)", margin: 0, lineHeight: 1.6 }}>
              {stats.gmail.connected && "📧 Gmail connected. "}
              {stats.whatsapp.connected && "💬 WhatsApp connected. "}
              Buddy's ready to help you tackle the day.
            </p>
          )}
        </div>
      </div>

      {/* ── Stats row ─────────────────────────────────────────── */}
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(180px, 1fr))", gap: 12 }}>
        {[
          {
            icon: "📧",
            label: "Gmail",
            value: stats.gmail.connected ? "Connected" : "Not connected",
            sub: stats.gmail.connected ? "Sync enabled" : "Click Connections to set up",
            accent: stats.gmail.connected,
          },
          {
            icon: "💬",
            label: "WhatsApp",
            value: stats.whatsapp.connected ? "Connected" : "Not connected",
            sub: stats.whatsapp.connected ? "Sync enabled" : "Click Connections to set up",
            accent: stats.whatsapp.connected,
          },
          {
            icon: "🧠",
            label: "Memory",
            value: "Active",
            sub: "Extracting from conversations",
            accent: true,
          },
          {
            icon: "🔒",
            label: "Privacy",
            value: "100% Local",
            sub: "Nothing leaves this machine",
            accent: true,
          },
        ].map((stat) => (
          <div
            key={stat.label}
            style={{
              background: "var(--surface-1, var(--surface))",
              border: `1px solid ${stat.accent ? "rgba(99,102,241,0.2)" : "var(--border)"}`,
              borderRadius: 12,
              padding: "14px 16px",
            }}
          >
            <div style={{ fontSize: 20, marginBottom: 8 }}>{stat.icon}</div>
            <div style={{ fontSize: 11, color: "var(--text-3)", fontWeight: 500, textTransform: "uppercase", letterSpacing: "0.07em", marginBottom: 3 }}>
              {stat.label}
            </div>
            <div style={{ fontSize: 14, fontWeight: 600, color: stat.accent ? "var(--text)" : "var(--text-3)", marginBottom: 2 }}>
              {stat.value}
            </div>
            <div style={{ fontSize: 11, color: "var(--text-3)", lineHeight: 1.4 }}>{stat.sub}</div>
          </div>
        ))}
      </div>

      {/* ── Quick actions ─────────────────────────────────────── */}
      <div>
        <h2 style={{ fontSize: 11, fontWeight: 600, color: "var(--text-3)", textTransform: "uppercase", letterSpacing: "0.08em", marginBottom: 12 }}>
          Quick actions
        </h2>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(200px, 1fr))", gap: 10 }}>
          {QUICK_ACTIONS.map((action) => (
            <button
              key={action.label}
              onClick={() => onNavigateToChat(action.prompt)}
              style={{
                display: "flex",
                alignItems: "center",
                gap: 10,
                padding: "12px 14px",
                background: "var(--surface)",
                border: "1px solid var(--border)",
                borderRadius: 10,
                textAlign: "left",
                cursor: "pointer",
                transition: "border-color 150ms, background 150ms",
              }}
              onMouseEnter={(e) => {
                (e.currentTarget as HTMLButtonElement).style.borderColor = "var(--accent)";
                (e.currentTarget as HTMLButtonElement).style.background = "var(--surface-2)";
              }}
              onMouseLeave={(e) => {
                (e.currentTarget as HTMLButtonElement).style.borderColor = "var(--border)";
                (e.currentTarget as HTMLButtonElement).style.background = "var(--surface)";
              }}
            >
              <span style={{ fontSize: 20 }}>{action.icon}</span>
              <span style={{ fontSize: 12, fontWeight: 500, color: "var(--text-2)" }}>{action.label}</span>
            </button>
          ))}
        </div>
      </div>

      {/* ── Buddy tip ─────────────────────────────────────────── */}
      <div
        style={{
          display: "flex",
          alignItems: "flex-start",
          gap: 14,
          padding: "16px 18px",
          background: "var(--surface)",
          border: "1px solid var(--border)",
          borderRadius: 12,
        }}
      >
        <div
          style={{
            width: 36,
            height: 36,
            borderRadius: "50%",
            background: "rgba(99,102,241,0.12)",
            display: "flex",
            alignItems: "center",
            justifyContent: "center",
            fontSize: 18,
            flexShrink: 0,
          }}
        >
          🐾
        </div>
        <div>
          <p style={{ fontSize: 12, fontWeight: 600, color: "var(--text-2)", marginBottom: 3 }}>Buddy</p>
          <p style={{ fontSize: 13, color: "var(--text-2)", lineHeight: 1.55, margin: 0 }}>
            Hey! I'm running entirely on your machine — no cloud, no uploads, no tracking. Connect your Gmail or WhatsApp to give me real-time context, and I'll help you stay on top of everything.
          </p>
        </div>
      </div>
    </div>
  );
}
