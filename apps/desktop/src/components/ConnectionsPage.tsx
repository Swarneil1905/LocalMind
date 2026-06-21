/**
 * Connections page — 🔌 in the sidebar.
 * Shows every connector (Gmail, WhatsApp, iMessage) as a card.
 * User can connect / disconnect / manually sync.
 * People Profiles tab shows extracted personas from synced data.
 */

import { useCallback, useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { RefreshCw, Plug, PlugZap, Users, CheckCircle, Loader, Bug } from "lucide-react";

// ── Types ─────────────────────────────────────────────────────────────────────

interface Connector {
  id: string;
  name: string;
  description: string;
  icon: string;
  status: "disconnected" | "connecting" | "connected" | "error" | "syncing";
  requires_browser: boolean;
  platform: string;
}

interface Person {
  id: number;
  canonical_name: string;
  relationship: string | null;
  bio: string | null;
  tags: string;
  sources: string;
  last_contact: string | null;
  communication_style: string | null;
}

type Notice = { text: string; kind: "info" | "error" | "success" };

// ── Status chip ───────────────────────────────────────────────────────────────

function StatusChip({ status }: { status: Connector["status"] }) {
  const map: Record<Connector["status"], { label: string; color: string; bg: string }> = {
    connected:    { label: "Connected",     color: "#22c55e", bg: "rgba(34,197,94,0.12)"  },
    syncing:      { label: "Syncing…",      color: "#3b82f6", bg: "rgba(59,130,246,0.12)" },
    connecting:   { label: "Connecting…",   color: "#f59e0b", bg: "rgba(245,158,11,0.12)" },
    disconnected: { label: "Not connected", color: "var(--text-3)", bg: "var(--surface-2)" },
    error:        { label: "Error",         color: "#ef4444", bg: "rgba(239,68,68,0.12)"  },
  };
  const { label, color, bg } = map[status] ?? map.disconnected;
  return (
    <span style={{ fontSize: 11, fontWeight: 600, color, background: bg, borderRadius: 10, padding: "2px 9px" }}>
      {label}
    </span>
  );
}

// ── Credential form definitions ───────────────────────────────────────────────

// Gmail uses OAuth — no static form. The backend returns credential_required
// dynamically when the Google Cloud client_id/secret haven't been saved yet.
const CREDENTIAL_FORMS: Record<string, { key: string; label: string; type: string; placeholder: string; help?: string }[]> = {};

// ── Field definition (static or dynamic from backend) ────────────────────────

type FieldDef = { key: string; label: string; type: string; placeholder: string; help?: string };

// ── Connector card ────────────────────────────────────────────────────────────

function ConnectorCard({
  connector, onConnect, onDisconnect, onSync, loading,
  expanded, onToggleExpand, credentials, onCredentialChange, dynamicFields,
}: {
  connector: Connector;
  onConnect: (id: string, creds?: Record<string, string>) => void;
  onDisconnect: (id: string) => void;
  onSync: (id: string) => void;
  loading: string | null;
  expanded: boolean;
  onToggleExpand: (id: string) => void;
  credentials: Record<string, string>;
  onCredentialChange: (id: string, key: string, value: string) => void;
  dynamicFields?: FieldDef[];
}) {
  const isConnected = connector.status === "connected" || connector.status === "syncing";
  const isBusy = loading === connector.id;
  // Form fields come either from static definitions or dynamically from backend
  const formFields: FieldDef[] = dynamicFields ?? CREDENTIAL_FORMS[connector.id] ?? [];
  const hasForm = formFields.length > 0;

  return (
    <div style={{
      background: "var(--surface-1)", border: "1px solid var(--border)",
      borderRadius: 12, padding: "16px 18px", display: "flex", flexDirection: "column",
    }}>
      {/* Header row */}
      <div style={{ display: "flex", alignItems: "center", gap: 14 }}>
        {/* Icon */}
        <div style={{
          fontSize: 26, width: 44, height: 44, flexShrink: 0,
          display: "flex", alignItems: "center", justifyContent: "center",
          background: "var(--surface-2)", borderRadius: 10,
        }}>
          {connector.icon}
        </div>

        {/* Info */}
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: 3 }}>
            <span style={{ fontSize: 14, fontWeight: 600, color: "var(--text-1)" }}>
              {connector.name}
            </span>
            <StatusChip status={connector.status} />
            {connector.platform !== "all" && (
              <span style={{ fontSize: 10, color: "var(--text-3)", background: "var(--surface-2)",
                borderRadius: 6, padding: "1px 6px", border: "1px solid var(--border)" }}>
                {connector.platform === "macos" ? "macOS only" : connector.platform}
              </span>
            )}
          </div>
          <p style={{ fontSize: 12, color: "var(--text-3)", margin: 0, lineHeight: 1.4 }}>
            {connector.description}
          </p>
        </div>

        {/* Actions */}
        <div style={{ display: "flex", gap: 6, flexShrink: 0 }}>
          {isConnected && (
            <button
              onClick={() => onSync(connector.id)}
              disabled={isBusy || connector.status === "syncing"}
              title="Sync now"
              style={{
                background: "var(--surface-2)", border: "1px solid var(--border)",
                borderRadius: 8, padding: "6px 8px", cursor: "pointer",
                color: "var(--text-2)", display: "flex", alignItems: "center",
                opacity: isBusy ? 0.5 : 1,
              }}
            >
              <RefreshCw size={13} />
            </button>
          )}
          {isConnected && connector.id === "gmail" && (
            <button
              onClick={async () => {
                try {
                  const result = await invoke("gmail_debug");
                  alert(JSON.stringify(result, null, 2));
                } catch (e) {
                  alert("Debug failed: " + String(e));
                }
              }}
              title="Test Gmail IMAP connection"
              style={{
                background: "var(--surface-2)", border: "1px solid var(--border)",
                borderRadius: 8, padding: "6px 8px", cursor: "pointer",
                color: "var(--text-3)", display: "flex", alignItems: "center",
              }}
            >
              <Bug size={13} />
            </button>
          )}
          {hasForm && !isConnected && (
            <button
              onClick={() => onToggleExpand(connector.id)}
              style={{
                background: "var(--surface-2)", border: "1px solid var(--border)",
                borderRadius: 8, padding: "6px 10px", cursor: "pointer",
                fontSize: 11, color: "var(--text-3)",
              }}
            >
              {expanded ? "▲" : "▼"}
            </button>
          )}
          <button
            onClick={() => {
              if (isConnected) {
                onDisconnect(connector.id);
              } else if (hasForm && !expanded) {
                onToggleExpand(connector.id);
              } else {
                onConnect(connector.id, hasForm ? credentials : undefined);
              }
            }}
            disabled={isBusy || connector.status === "connecting"}
            style={{
              background: isConnected ? "transparent" : "var(--accent)",
              border: isConnected ? "1px solid var(--border)" : "none",
              borderRadius: 8, padding: "6px 14px",
              fontSize: 12, fontWeight: 600, cursor: "pointer",
              color: isConnected ? "var(--text-2)" : "white",
              display: "flex", alignItems: "center", gap: 5,
              opacity: isBusy ? 0.5 : 1,
            }}
          >
            {isBusy ? (
              <Loader size={12} style={{ animation: "spin 1s linear infinite" }} />
            ) : isConnected ? (
              <><PlugZap size={12} /> Disconnect</>
            ) : expanded && hasForm ? (
              <><CheckCircle size={12} /> Save & Connect</>
            ) : connector.id === "gmail" && !isConnected ? (
              <><span style={{ fontSize: 13 }}>G</span> Sign in with Google</>
            ) : (
              <><Plug size={12} /> Connect</>
            )}
          </button>
        </div>
      </div>

      {/* Credential form */}
      {expanded && hasForm && !isConnected && (
        <div style={{
          marginTop: 14, paddingTop: 14,
          borderTop: "1px solid var(--border)",
          display: "flex", flexDirection: "column", gap: 10,
        }}>
          {formFields.map(field => (
            <div key={field.key}>
              <label style={{ fontSize: 11, fontWeight: 600, color: "var(--text-2)", display: "block", marginBottom: 4 }}>
                {field.label}
              </label>
              <input
                type={field.type}
                placeholder={field.placeholder}
                value={credentials[field.key] || ""}
                onChange={e => onCredentialChange(connector.id, field.key, e.target.value)}
                style={{
                  width: "100%", boxSizing: "border-box",
                  background: "var(--surface-2)", border: "1px solid var(--border)",
                  borderRadius: 7, padding: "7px 10px",
                  fontSize: 12, color: "var(--text-1)", outline: "none",
                }}
              />
              {field.help && (
                <p style={{ fontSize: 10, color: "var(--text-3)", margin: "3px 0 0", lineHeight: 1.4 }}>
                  {field.help}
                </p>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Person card ───────────────────────────────────────────────────────────────

function PersonCard({ person }: { person: Person }) {
  let tags: string[] = [];
  let sources: string[] = [];
  try { tags = JSON.parse(person.tags || "[]"); } catch { /* ignore */ }
  try { sources = JSON.parse(person.sources || "[]"); } catch { /* ignore */ }

  return (
    <div style={{
      background: "var(--surface-1)", border: "1px solid var(--border)",
      borderRadius: 10, padding: "12px 14px",
    }}>
      <div style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: 4 }}>
        <div style={{
          width: 32, height: 32, borderRadius: "50%", background: "var(--accent)",
          display: "flex", alignItems: "center", justifyContent: "center",
          fontSize: 13, fontWeight: 700, color: "white", flexShrink: 0,
        }}>
          {person.canonical_name[0]?.toUpperCase()}
        </div>
        <div>
          <div style={{ fontSize: 13, fontWeight: 600, color: "var(--text-1)" }}>
            {person.canonical_name}
          </div>
          {person.relationship && (
            <div style={{ fontSize: 11, color: "var(--text-3)" }}>{person.relationship}</div>
          )}
        </div>
        <div style={{ marginLeft: "auto", display: "flex", gap: 4 }}>
          {sources.map(s => (
            <span key={s} style={{
              fontSize: 10, background: "var(--surface-2)", borderRadius: 6,
              padding: "1px 6px", color: "var(--text-3)", border: "1px solid var(--border)",
            }}>
              {s === "gmail" ? "📧" : s === "whatsapp_web" ? "💬" : s === "imessage" ? "🍏" : s}
            </span>
          ))}
        </div>
      </div>
      {person.bio && (
        <p style={{ fontSize: 11, color: "var(--text-2)", margin: "4px 0 0", lineHeight: 1.5 }}>
          {person.bio}
        </p>
      )}
      {tags.length > 0 && (
        <div style={{ display: "flex", flexWrap: "wrap", gap: 4, marginTop: 6 }}>
          {tags.slice(0, 4).map(tag => (
            <span key={tag} style={{
              fontSize: 10, background: "var(--surface-2)", borderRadius: 6,
              padding: "1px 7px", color: "var(--text-3)",
            }}>
              {tag}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Main page ─────────────────────────────────────────────────────────────────

export function ConnectionsPage() {
  const [connectors, setConnectors] = useState<Connector[]>([]);
  const [people, setPeople] = useState<Person[]>([]);
  const [loading, setLoading] = useState<string | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [expandedConnector, setExpandedConnector] = useState<string | null>(null);
  const [credentials, setCredentials] = useState<Record<string, Record<string, string>>>({});
  const [dynamicFields, setDynamicFields] = useState<Record<string, FieldDef[]>>({});
  const [tab, setTab] = useState<"sources" | "people">("sources");

  const loadConnectors = useCallback(async () => {
    try {
      const data = await invoke<Connector[]>("list_connectors");
      setConnectors(Array.isArray(data) ? data : []);
    } catch (e) {
      console.error("list_connectors failed:", e);
    }
  }, []);

  const loadPeople = useCallback(async () => {
    try {
      const data = await invoke<Person[]>("list_people");
      if (Array.isArray(data)) setPeople(data);
    } catch (e) {
      console.error("list_people failed:", e);
    }
  }, []);

  useEffect(() => {
    loadConnectors();
    loadPeople();
    const interval = setInterval(loadConnectors, 10_000);
    return () => clearInterval(interval);
  }, [loadConnectors, loadPeople]);

  const handleConnect = useCallback(async (id: string, creds?: Record<string, string>) => {
    setLoading(id);
    setNotice(null);
    try {
      const result = await invoke<{ type: string; url?: string; message?: string; fields?: FieldDef[] }>(
        "connect_connector",
        { connectorId: id, credentials: creds && Object.keys(creds).length > 0 ? creds : null }
      );
      if (result.type === "oauth_url" && result.url) {
        const { openUrl } = await import("@tauri-apps/plugin-opener");
        await openUrl(result.url);
        setNotice({ text: "Browser opened — complete sign-in to connect.", kind: "info" });
      } else if (result.type === "browser_opening") {
        setNotice({ text: result.message || "Browser is opening on your computer…", kind: "info" });
      } else if (result.type === "ready") {
        setNotice({ text: "Connected successfully.", kind: "success" });
        setExpandedConnector(null);
      } else if (result.type === "credential_required") {
        setNotice({ text: result.message || "Enter your credentials below.", kind: "info" });
        setExpandedConnector(id);
        // Store any dynamically returned fields (e.g. Gmail OAuth client setup)
        if (result.fields && Array.isArray(result.fields)) {
          setDynamicFields(prev => ({ ...prev, [id]: result.fields as FieldDef[] }));
        }
      } else if (result.type === "installing") {
        setNotice({ text: result.message || "Installing dependencies… click Connect again when done.", kind: "info" });
      } else if (result.type === "permission_required") {
        setNotice({ text: result.message || "Permission required.", kind: "error" });
      } else if (result.type === "setup_required") {
        setNotice({ text: result.message || "Setup required.", kind: "info" });
        setExpandedConnector(id);
      } else if (result.type === "error") {
        setNotice({ text: result.message || "Connection failed.", kind: "error" });
      } else {
        setNotice({ text: `Unexpected response: ${result.type}`, kind: "error" });
      }
      await loadConnectors();
    } catch (e: unknown) {
      setNotice({ text: `Connection failed: ${String(e)}`, kind: "error" });
    } finally {
      setLoading(null);
    }
  }, [loadConnectors]);

  const handleDisconnect = useCallback(async (id: string) => {
    setLoading(id);
    try {
      await invoke("disconnect_connector", { connectorId: id });
      await loadConnectors();
      setNotice({ text: `${id} disconnected.`, kind: "info" });
    } catch (e: unknown) {
      console.error(e);
    } finally {
      setLoading(null);
    }
  }, [loadConnectors]);

  const handleSync = useCallback(async (id: string) => {
    setLoading(id);
    try {
      await invoke("sync_connector", { connectorId: id });
      setNotice({ text: `Syncing ${id}... check back in a moment.`, kind: "info" });
      setTimeout(() => { loadConnectors(); loadPeople(); }, 4000);
    } catch (e: unknown) {
      setNotice({ text: `Sync failed: ${String(e)}`, kind: "error" });
    } finally {
      setLoading(null);
    }
  }, [loadConnectors, loadPeople]);

  const connectedCount = connectors.filter(c => c.status === "connected" || c.status === "syncing").length;

  const noticeStyle = (kind: Notice["kind"]): React.CSSProperties => ({
    margin: "10px 20px 0",
    background: kind === "error" ? "rgba(239,68,68,0.08)" : kind === "success" ? "rgba(34,197,94,0.08)" : "rgba(59,130,246,0.08)",
    border: `1px solid ${kind === "error" ? "rgba(239,68,68,0.3)" : kind === "success" ? "rgba(34,197,94,0.3)" : "rgba(59,130,246,0.3)"}`,
    borderRadius: 8, padding: "8px 12px", fontSize: 12,
    color: kind === "error" ? "#ef4444" : kind === "success" ? "#22c55e" : "#3b82f6",
    display: "flex", alignItems: "center", justifyContent: "space-between", gap: 8,
  });

  return (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", overflow: "hidden" }}>
      <div style={{ padding: "18px 20px 12px", borderBottom: "1px solid var(--border)", flexShrink: 0 }}>
        <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
          <div>
            <h2 style={{ fontSize: 16, fontWeight: 700, color: "var(--text-1)", margin: 0 }}>Connections</h2>
            <p style={{ fontSize: 12, color: "var(--text-3)", margin: "3px 0 0" }}>
              {connectedCount > 0
                ? `${connectedCount} source${connectedCount !== 1 ? "s" : ""} connected—Buddy can see your messages`
                : "Connect your apps to give Buddy real-time context"}
            </p>
          </div>
          <button onClick={() => { loadConnectors(); loadPeople(); }}
            style={{ background: "none", border: "1px solid var(--border)", borderRadius: 8, padding: "5px 8px", cursor: "pointer", color: "var(--text-3)" }}>
            <RefreshCw size={13} />
          </button>
        </div>
        <div style={{ display: "flex", gap: 4, marginTop: 12 }}>
          {(["sources", "people"] as const).map(t => (
            <button key={t} onClick={() => setTab(t)} style={{
              fontSize: 12, fontWeight: 600, padding: "5px 12px", borderRadius: 8,
              border: "none", cursor: "pointer",
              background: tab === t ? "var(--accent)" : "var(--surface-2)",
              color: tab === t ? "white" : "var(--text-2)",
            }}>
              {t === "sources" ? `Sources (${connectors.length})` : `People (${people.length})`}
            </button>
          ))}
        </div>
      </div>

      {notice && (
        <div style={noticeStyle(notice.kind)}>
          <span>{notice.text}</span>
          <button onClick={() => setNotice(null)}
            style={{ background: "none", border: "none", cursor: "pointer", color: "inherit", opacity: 0.6, fontSize: 14, padding: 0 }}>
            x
          </button>
        </div>
      )}

      <div style={{ flex: 1, overflowY: "auto", padding: "12px 20px 20px" }}>
        {tab === "sources" ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            {connectors.length === 0 ? (
              <div style={{ textAlign: "center", padding: "40px 0", color: "var(--text-3)" }}>
                <PlugZap size={32} strokeWidth={1} style={{ opacity: 0.3, marginBottom: 12 }} />
                <p style={{ fontSize: 13 }}>Sidecar is starting up...</p>
              </div>
            ) : (
              connectors.map(c => (
                <ConnectorCard
                  key={c.id}
                  connector={c}
                  onConnect={(id, creds) => handleConnect(id, creds)}
                  onDisconnect={handleDisconnect}
                  onSync={handleSync}
                  loading={loading}
                  expanded={expandedConnector === c.id}
                  onToggleExpand={id => setExpandedConnector(expandedConnector === id ? null : id)}
                  credentials={credentials[c.id] || {}}
                  onCredentialChange={(id, key, val) =>
                    setCredentials(prev => ({ ...prev, [id]: { ...(prev[id] || {}), [key]: val } }))}
                  dynamicFields={dynamicFields[c.id]}
                />
              ))
            )}
          </div>
        ) : (
              <div>
            {people.length === 0 ? (
              <div style={{ textAlign: "center", padding: "40px 0", color: "var(--text-3)" }}>
                <Users size={32} strokeWidth={1} style={{ opacity: 0.3, marginBottom: 12 }} />
                <p style={{ fontSize: 13 }}>Connect a source and sync to build people profiles.</p>
              </div>
            ) : (
              <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(260px, 1fr))", gap: 8 }}>
                {people.map(p => <PersonCard key={p.id} person={p} />)}
              </div>
            )}
          </div>
        )}
      </div>
      <style>{`@keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }`}</style>
    </div>
  );
}
