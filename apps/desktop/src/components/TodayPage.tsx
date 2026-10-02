import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import {
  ArrowRight,
  FileText,
  MessageSquare,
  PenLine,
  Plug,
  RefreshCw,
} from "lucide-react";
import "./supporting-pages.css";

interface ConnectorStatus {
  id: string;
  name: string;
  status: string;
}

const ACTIONS = [
  {
    title: "Work through an idea",
    description: "Explore a question or a decision.",
    icon: MessageSquare,
    prompt: "Help me think through an idea. Ask me what I’m working on first.",
  },
  {
    title: "Improve a draft",
    description: "Make your writing clear and useful.",
    icon: PenLine,
    prompt:
      "I’d like help improving a draft. Ask me to share the text and who it’s for.",
  },
  {
    title: "Understand a document",
    description: "Find the main points in your material.",
    icon: FileText,
    prompt:
      "Help me understand a document. Ask me to paste the text or enable Knowledge for my indexed files.",
  },
];

export function TodayPage({
  onNavigateToChat,
  onNavigateToConnections,
}: {
  onNavigateToChat: (prompt?: string) => void;
  onNavigateToConnections?: () => void;
}) {
  const [connectors, setConnectors] = useState<ConnectorStatus[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    let cancelled = false;
    invoke<ConnectorStatus[]>("list_connectors")
      .then((data) => {
        if (!cancelled) {
          setConnectors(Array.isArray(data) ? data : []);
          setError(false);
        }
      })
      .catch(() => {
        if (!cancelled) setError(true);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [refresh]);
  const hour = new Date().getHours();
  const greeting =
    hour < 12 ? "Good morning" : hour < 17 ? "Good afternoon" : "Good evening";
  const connected = connectors.filter(
    (item) => item.status === "connected" || item.status === "syncing",
  );
  return (
    <div className="workspace-page today-page">
      <div className="workspace-page-inner">
        <header className="page-heading">
          <div>
            <p className="page-kicker">
              {new Date().toLocaleDateString(undefined, {
                weekday: "long",
                month: "long",
                day: "numeric",
              })}
            </p>
            <h1>{greeting}.</h1>
            <p>Your conversations, knowledge, and projects in one place.</p>
          </div>
          <button
            className="page-button primary"
            onClick={() => onNavigateToChat()}
          >
            Start a conversation <ArrowRight size={15} />
          </button>
        </header>
        <section className="page-section" aria-labelledby="today-start">
          <div className="section-heading">
            <h2 id="today-start">Where would you like to start?</h2>
          </div>
          <div className="start-actions">
            {ACTIONS.map(({ title, description, icon: Icon, prompt }) => (
              <button
                key={title}
                className="start-action"
                onClick={() => onNavigateToChat(prompt)}
              >
                <Icon size={20} strokeWidth={1.5} />
                <div>
                  <strong>{title}</strong>
                  <span>{description}</span>
                </div>
                <ArrowRight size={15} className="action-arrow" />
              </button>
            ))}
          </div>
        </section>
        <section className="page-section" aria-labelledby="today-connections">
          <div className="section-heading">
            <h2 id="today-connections">Connected apps</h2>
            {onNavigateToConnections && (
              <button
                className="page-text-button"
                onClick={onNavigateToConnections}
              >
                Manage connections <ArrowRight size={14} />
              </button>
            )}
          </div>
          <div className="page-panel">
            {loading ? (
              <p className="muted" role="status">
                Checking connections…
              </p>
            ) : error ? (
              <div className="inline-status">
                <p>Connection status is unavailable.</p>
                <button
                  className="page-button"
                  onClick={() => {
                    setLoading(true);
                    setRefresh((value) => value + 1);
                  }}
                >
                  <RefreshCw size={14} /> Retry
                </button>
              </div>
            ) : connected.length ? (
              <div className="connection-summary">
                {connected.map((item) => (
                  <div className="compact-row" key={item.id}>
                    <Plug size={17} />
                    <strong>{item.name}</strong>
                    <span className="page-badge">
                      {item.status === "syncing" ? "Syncing" : "Connected"}
                    </span>
                  </div>
                ))}
              </div>
            ) : (
              <div className="quiet-empty">
                <Plug size={22} strokeWidth={1.5} />
                <div>
                  <h3>No apps connected</h3>
                  <p>
                    You can chat with your local model now. Add an app in
                    Connections when you need more context.
                  </p>
                </div>
              </div>
            )}
          </div>
        </section>
        <div className="page-footnote">
          <span className="status-dot" /> Local models run on this computer. Web
          search and connected apps use their own online services.
        </div>
      </div>
    </div>
  );
}
