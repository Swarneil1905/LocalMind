import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import {
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  LoaderCircle,
  Mail,
  MessageSquare,
  Plug,
  RefreshCw,
  Users,
  X,
} from "lucide-react";
import "./supporting-pages.css";
import { McpPanel } from "./McpPanel";

interface Connector {
  id: string;
  name: string;
  description: string;
  status: "disconnected" | "connecting" | "connected" | "error" | "syncing" | "expired" | "offline" | "disabled" | "locked";
  detail?: string;
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
type Field = {
  key: string;
  label: string;
  type: string;
  placeholder: string;
  help?: string;
};
type Notice = { text: string; kind: "info" | "error" | "success" };
const STATUS_LABEL = {
  connected: "Connected",
  syncing: "Syncing…",
  connecting: "Connecting…",
  disconnected: "Not connected",
  error: "Needs attention",
  expired: "Authorization expired",
  offline: "Offline",
  disabled: "Disconnected locally",
  locked: "Credential store locked",
};
function listFromJson(value: string): string[] {
  try {
    const parsed: unknown = JSON.parse(value);
    return Array.isArray(parsed)
      ? parsed.filter((item): item is string => typeof item === "string")
      : [];
  } catch {
    return [];
  }
}
function PersonRow({ person }: { person: Person }) {
  const sources = listFromJson(person.sources || "[]");
  return (
    <article className="person-row">
      <div className="person-initial">
        {person.canonical_name.slice(0, 1).toUpperCase()}
      </div>
      <div className="record-copy">
        <h3>{person.canonical_name}</h3>
        {person.relationship && (
          <span className="muted">{person.relationship}</span>
        )}
        {person.bio && <p>{person.bio}</p>}
        <div className="person-tags">
          {listFromJson(person.tags || "[]")
            .slice(0, 4)
            .map((tag) => (
              <span className="page-badge" key={tag}>
                {tag}
              </span>
            ))}
          {sources.map((source) => (
            <span className="muted" key={source}>
              {source === "gmail"
                ? "Gmail"
                : source === "whatsapp_web"
                  ? "WhatsApp"
                  : source === "imessage"
                    ? "iMessage"
                    : source}
            </span>
          ))}
        </div>
      </div>
    </article>
  );
}
function ConnectorRow({
  connector,
  busy,
  blocked,
  expanded,
  fields,
  credentials,
  onToggle,
  onCredential,
  onConnect,
  onDisconnect,
  onSync,
}: {
  connector: Connector;
  busy: boolean;
  blocked: boolean;
  expanded: boolean;
  fields: Field[];
  credentials: Record<string, string>;
  onToggle: () => void;
  onCredential: (key: string, value: string) => void;
  onConnect: () => void;
  onDisconnect: () => void;
  onSync: () => void;
}) {
  const connected =
    ["connected", "syncing", "expired", "offline", "disabled"].includes(connector.status);
  const isMac = /Macintosh|Mac OS X/i.test(navigator.userAgent);
  const unsupported = connector.platform === "macos" && !isMac;
  const pending =
    busy || connector.status === "connecting" || connector.status === "syncing";
  const Icon =
    connector.id === "gmail"
      ? Mail
      : connector.id === "whatsapp_web" || connector.id === "imessage"
        ? MessageSquare
        : Plug;
  return (
    <article className="connector-row">
      <div className="connector-main">
        <div className="connector-icon">
          <Icon size={22} strokeWidth={1.5} />
        </div>
        <div className="record-copy">
          <div className="connector-title">
            <h3>{connector.name}</h3>
            <span
              className={`page-badge ${connector.status === "error" ? "error-text" : ""}`}
            >
              {unsupported
                ? "macOS only"
                : (STATUS_LABEL[connector.status] ?? "Not connected")}
            </span>
          </div>
          <p>{connector.description}</p>
          {connector.detail && <p role="status">{connector.detail}</p>}
        </div>
        <div className="connector-actions">
          {connector.status === "expired" && <button className="page-button" disabled={pending || blocked} onClick={onConnect}>Reconnect</button>}
          {connected && connector.status !== "disabled" && (
            <button
              className="page-icon-button"
              aria-label={`Sync ${connector.name}`}
              onClick={onSync}
              disabled={pending || blocked}
            >
              <RefreshCw
                size={16}
                className={connector.status === "syncing" ? "spin" : ""}
              />
            </button>
          )}
          {!connected && fields.length > 0 && (
            <button
              className="page-icon-button"
              aria-label={
                expanded
                  ? "Hide connection settings"
                  : "Show connection settings"
              }
              aria-expanded={expanded}
              onClick={onToggle}
            >
              {expanded ? <ChevronUp size={16} /> : <ChevronDown size={16} />}
            </button>
          )}
          <button
            className={`page-button ${connected ? "" : "primary"}`}
            disabled={pending || blocked || unsupported}
            onClick={
              connected
                ? onDisconnect
                : fields.length && !expanded
                  ? onToggle
                  : onConnect
            }
          >
            {busy && <LoaderCircle size={14} className="spin" />}
            {connected
              ? "Disconnect"
              : pending
                ? "Connecting…"
                : fields.length && expanded
                  ? "Save & connect"
                  : connector.id === "gmail"
                    ? "Sign in with Google"
                    : "Connect"}
          </button>
        </div>
      </div>
      {expanded && fields.length > 0 && !connected && (
        <div className="connector-fields">
          {fields.map((field) => (
            <div className="field-group" key={field.key}>
              <label htmlFor={`connector-${connector.id}-${field.key}`}>
                {field.label}
              </label>
              <input
                id={`connector-${connector.id}-${field.key}`}
                className="page-input"
                disabled={blocked}
                type={field.type}
                autoComplete="off"
                placeholder={field.placeholder}
                value={credentials[field.key] || ""}
                onChange={(event) =>
                  onCredential(field.key, event.target.value)
                }
              />
              {field.help && <p>{field.help}</p>}
            </div>
          ))}
        </div>
      )}
    </article>
  );
}
export function ConnectionsPage() {
  const [connectors, setConnectors] = useState<Connector[]>([]);
  const [people, setPeople] = useState<Person[]>([]);
  const [busy, setBusy] = useState<string | null>(null);
  const [initialLoading, setInitialLoading] = useState(true);
  const [loadFailed, setLoadFailed] = useState(false);
  const [peopleFailed, setPeopleFailed] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [credentials, setCredentials] = useState<
    Record<string, Record<string, string>>
  >({});
  const [fields, setFields] = useState<Record<string, Field[]>>({});
  const [tab, setTab] = useState<"sources" | "people">("sources");
  const active = useRef(true);
  const fetching = useRef(false);
  const refresh = useCallback(async () => {
    if (fetching.current) return;
    fetching.current = true;
    try {
      const [sourceResult, peopleResult] = await Promise.allSettled([
        invoke<Connector[]>("list_connectors"),
        invoke<Person[]>("list_people"),
      ]);
      if (!active.current) return;
      if (sourceResult.status === "fulfilled") {
        setConnectors(
          Array.isArray(sourceResult.value) ? sourceResult.value : [],
        );
        setLoadFailed(false);
      } else setLoadFailed(true);
      if (peopleResult.status === "fulfilled") {
        setPeople(Array.isArray(peopleResult.value) ? peopleResult.value : []);
        setPeopleFailed(false);
      } else setPeopleFailed(true);
    } finally {
      fetching.current = false;
      if (active.current) setInitialLoading(false);
    }
  }, []);
  useEffect(() => {
    active.current = true;
    void refresh();
    const timer = window.setInterval(() => void refresh(), 10000);
    return () => {
      active.current = false;
      window.clearInterval(timer);
    };
  }, [refresh]);
  async function connect(id: string) {
    if (busy) return;
    setBusy(id);
    setNotice(null);
    try {
      const creds = credentials[id] ?? {};
      const result = await invoke<{
        type: string;
        url?: string;
        message?: string;
        fields?: Field[];
      }>("connect_connector", {
        connectorId: id,
        credentials: Object.keys(creds).length ? creds : null,
      });
      if (result.type === "oauth_url" && result.url) {
        setCredentials((previous) => {
          const next = { ...previous };
          delete next[id];
          return next;
        });
        setExpanded(null);
        const { openUrl } = await import("@tauri-apps/plugin-opener");
        await openUrl(result.url);
        setNotice({
          text: "Complete sign-in in your browser, then return here.",
          kind: "info",
        });
      } else if (result.type === "ready") {
        setNotice({ text: "Connected successfully.", kind: "success" });
        setExpanded(null);
        setCredentials((previous) => {
          const next = { ...previous };
          delete next[id];
          return next;
        });
      } else if (result.type === "credential_required") {
        setExpanded(id);
        if (Array.isArray(result.fields))
          setFields((previous) => ({ ...previous, [id]: result.fields ?? [] }));
        setNotice({
          text: result.message || "Enter the connection details below.",
          kind: "info",
        });
      } else {
        const known = [
          "browser_opening",
          "installing",
          "permission_required",
          "setup_required",
          "error",
        ].includes(result.type);
        setNotice({
          text:
            known && result.message
              ? result.message
              : "Couldn’t connect this app. Please try again.",
          kind:
            result.type === "permission_required" ||
            result.type === "error" ||
            !known
              ? "error"
              : "info",
        });
      }
      await refresh();
    } catch {
      setNotice({
        text: "Couldn’t connect this app. Check your details and try again.",
        kind: "error",
      });
    } finally {
      setBusy(null);
    }
  }
  async function disconnect(connector: Connector) {
    if (
      busy ||
      !window.confirm(
        `Disconnect ${connector.name}? Existing synced data may remain on this device.`,
      )
    )
      return;
    setBusy(connector.id);
    setNotice(null);
    try {
      await invoke("disconnect_connector", { connectorId: connector.id });
      await refresh();
      setNotice({ text: `${connector.name} disconnected.`, kind: "info" });
    } catch {
      setNotice({
        text: "Couldn’t disconnect this app. Please try again.",
        kind: "error",
      });
    } finally {
      setBusy(null);
    }
  }
  async function sync(connector: Connector) {
    if (busy) return;
    setBusy(connector.id);
    setNotice(null);
    try {
      await invoke("sync_connector", { connectorId: connector.id });
      setNotice({
        text: `Sync requested for ${connector.name}. Its status will update here.`,
        kind: "info",
      });
      await refresh();
    } catch {
      setNotice({
        text: "Couldn’t sync this app. Please try again.",
        kind: "error",
      });
    } finally {
      setBusy(null);
    }
  }
  const connectedCount = connectors.filter(
    (connector) =>
      connector.status === "connected" || connector.status === "syncing",
  ).length;
  return (
    <div className="workspace-page">
      <div className="workspace-page-inner">
        <header className="page-heading">
          <div>
            <h1>Connections</h1>
            <p>Choose the apps that can add context to your conversations.</p>
          </div>
          <button
            className="page-button"
            onClick={() => void refresh()}
            disabled={initialLoading}
          >
            <RefreshCw size={15} />
            Refresh
          </button>
        </header>
        <div className="page-toolbar">
          <div className="page-tabs" aria-label="Connections view">
            <button
              aria-pressed={tab === "sources"}
              onClick={() => setTab("sources")}
            >
              <Plug size={15} />
              Apps<span>{connectors.length}</span>
            </button>
            <button
              aria-pressed={tab === "people"}
              onClick={() => setTab("people")}
            >
              <Users size={15} />
              People<span>{people.length}</span>
            </button>
          </div>
          <span className="muted">{connectedCount} connected</span>
        </div>
        {notice && (
          <div
            className={`page-notice ${notice.kind}`}
            role={notice.kind === "error" ? "alert" : "status"}
          >
            {notice.kind === "success" && <CheckCircle2 size={16} />}
            <span>{notice.text}</span>
            <button
              className="page-icon-button"
              aria-label="Dismiss notification"
              onClick={() => setNotice(null)}
            >
              <X size={15} />
            </button>
          </div>
        )}
        {initialLoading ? (
          <div className="page-empty-state" role="status">
            <LoaderCircle size={24} className="spin" />
            <p>Loading connections…</p>
          </div>
        ) : loadFailed ? (
          <div className="page-empty-state">
            <Plug size={24} />
            <h2>Connections are unavailable</h2>
            <p>Check that LocalMind is ready, then refresh to try again.</p>
            <button className="page-button" onClick={() => void refresh()}>
              Try again
            </button>
          </div>
        ) : tab === "sources" ? (
          <>
            <McpPanel />
            {connectors.length ? (
              <div className="record-list">
                {connectors.map((connector) => (
                  <ConnectorRow
                    key={connector.id}
                    connector={connector}
                    busy={busy === connector.id}
                    blocked={busy !== null}
                    expanded={expanded === connector.id}
                    fields={fields[connector.id] ?? []}
                    credentials={credentials[connector.id] ?? {}}
                    onToggle={() =>
                      setExpanded((value) =>
                        value === connector.id ? null : connector.id,
                      )
                    }
                    onCredential={(key, value) =>
                      setCredentials((previous) => ({
                        ...previous,
                        [connector.id]: {
                          ...previous[connector.id],
                          [key]: value,
                        },
                      }))
                    }
                    onConnect={() => void connect(connector.id)}
                    onDisconnect={() => void disconnect(connector)}
                    onSync={() => void sync(connector)}
                  />
                ))}
              </div>
            ) : (
              <div className="page-empty-state">
                <Plug size={25} />
                <h2>No connectors available</h2>
                <p>This installation has no available app connectors.</p>
              </div>
            )}
            <p className="page-footnote">
              Connecting an app allows LocalMind to retrieve data from that
              service. You can disconnect it here at any time.
            </p>
          </>
        ) : peopleFailed ? (
          <div className="page-empty-state">
            <Users size={24} />
            <h2>People are unavailable</h2>
            <p>Refresh to try loading these profiles again.</p>
            <button className="page-button" onClick={() => void refresh()}>
              Retry
            </button>
          </div>
        ) : people.length ? (
          <div className="record-list">
            {people.map((person) => (
              <PersonRow key={person.id} person={person} />
            ))}
          </div>
        ) : (
          <div className="page-empty-state">
            <div className="empty-icon">
              <Users size={25} strokeWidth={1.5} />
            </div>
            <h2>No people saved yet</h2>
            <p>
              People profiles appear after you connect and sync a supported app.
            </p>
          </div>
        )}
      </div>
    </div>
  );
}
