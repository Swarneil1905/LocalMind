// Spec reference: Section 14 (Chat UI), Section 15 (All Application Screens)
// Phase 1: functional chat, Ollama detection, model mode selector.

import { useState } from "react";
import {
  LayoutDashboard,
  MessageSquare,
  FolderOpen,
  Brain,
  BookOpen,
  CheckSquare,
  Settings,
  ChevronLeft,
  ChevronRight,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { Composer } from "./components/Composer";
import { MessageList } from "./components/MessageList";
import { SettingsPage } from "./components/SettingsPage";
import { useChat } from "./hooks/useChat";
import { useMemory } from "./hooks/useMemory";
import { useOllama } from "./hooks/useOllama";
import { ModelMode, MODEL_MAP } from "./types";
import "./App.css";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type PageId =
  | "today"
  | "chats"
  | "projects"
  | "memory"
  | "knowledge"
  | "tasks"
  | "settings";

interface NavItem {
  id: PageId;
  label: string;
  Icon: LucideIcon;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const NAV_ITEMS: NavItem[] = [
  { id: "today",     label: "Today",     Icon: LayoutDashboard },
  { id: "chats",     label: "Chats",     Icon: MessageSquare   },
  { id: "projects",  label: "Projects",  Icon: FolderOpen      },
  { id: "memory",    label: "Memory",    Icon: Brain           },
  { id: "knowledge", label: "Knowledge", Icon: BookOpen        },
  { id: "tasks",     label: "Tasks",     Icon: CheckSquare     },
  { id: "settings",  label: "Settings",  Icon: Settings        },
];

const RIGHT_PANEL_SECTIONS = [
  "Used memory",
  "Sources",
  "Tool activity",
  "Suggested saves",
  "Permissions",
];

const SIDEBAR_EXPANDED = 240;
const SIDEBAR_COLLAPSED = 48;

// ---------------------------------------------------------------------------
// App root
// ---------------------------------------------------------------------------

export default function App() {
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const [activePage, setActivePage] = useState<PageId>("chats");
  const [modelMode, setModelMode] = useState<ModelMode>("balanced");
  const [speedModel, setSpeedModel] = useState(MODEL_MAP.speed);
  const [balancedModel, setBalancedModel] = useState(MODEL_MAP.balanced);
  const [memoryEnabled, setMemoryEnabled] = useState(true);

  const ollamaStatus = useOllama();
  const { memories, deleteMemory } = useMemory();
  const { messages, isStreaming, sendMessage, stopStreaming } = useChat({
    modelMode,
    speedModel,
    memoryEnabled,
  });

  return (
    <div
      style={{
        display: "flex",
        height: "100vh",
        overflow: "hidden",
        backgroundColor: "var(--bg)",
      }}
    >
      <Sidebar
        collapsed={sidebarCollapsed}
        activePage={activePage}
        ollamaRunning={ollamaStatus?.running ?? null}
        onNavigate={setActivePage}
        onToggle={() => setSidebarCollapsed((c) => !c)}
      />
      <MainArea
        activePage={activePage}
        messages={messages}
        isStreaming={isStreaming}
        modelMode={modelMode}
        ollamaStatus={ollamaStatus}
        speedModel={speedModel}
        balancedModel={balancedModel}
        memoryEnabled={memoryEnabled}
        onSend={sendMessage}
        onStop={stopStreaming}
        onModelModeChange={setModelMode}
        onSpeedModelChange={setSpeedModel}
        onBalancedModelChange={setBalancedModel}
        onMemoryToggle={() => setMemoryEnabled((v) => !v)}
      />
      <RightPanel memories={memories} onDeleteMemory={deleteMemory} />
    </div>
  );
}

// ---------------------------------------------------------------------------
// Sidebar
// ---------------------------------------------------------------------------

interface SidebarProps {
  collapsed: boolean;
  activePage: PageId;
  ollamaRunning: boolean | null;
  onNavigate: (page: PageId) => void;
  onToggle: () => void;
}

function Sidebar({
  collapsed,
  activePage,
  ollamaRunning,
  onNavigate,
  onToggle,
}: SidebarProps) {
  const width = collapsed ? SIDEBAR_COLLAPSED : SIDEBAR_EXPANDED;

  const dotColor =
    ollamaRunning === null
      ? "var(--text-3)"
      : ollamaRunning
      ? "#22c55e"
      : "#ef4444";

  const ollamaLabel =
    ollamaRunning === null
      ? "Ollama: checking"
      : ollamaRunning
      ? "Ollama: running"
      : "Ollama: not found";

  return (
    <aside
      style={{
        width,
        minWidth: width,
        display: "flex",
        flexDirection: "column",
        backgroundColor: "var(--surface)",
        borderRight: "1px solid var(--border)",
        transition: "width 200ms ease-in-out, min-width 200ms ease-in-out",
        overflow: "hidden",
        flexShrink: 0,
      }}
    >
      {/* Header */}
      <div
        style={{
          display: "flex",
          alignItems: "center",
          justifyContent: collapsed ? "center" : "space-between",
          height: 48,
          padding: collapsed ? "0 12px" : "0 16px",
          borderBottom: "1px solid var(--border)",
          flexShrink: 0,
        }}
      >
        {!collapsed && (
          <span
            style={{
              fontSize: 13,
              fontWeight: 600,
              color: "var(--text)",
              whiteSpace: "nowrap",
              letterSpacing: "-0.01em",
            }}
          >
            LocalMind
          </span>
        )}
        <button
          onClick={onToggle}
          aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
          style={{
            display: "flex",
            alignItems: "center",
            justifyContent: "center",
            width: 24,
            height: 24,
            borderRadius: 3,
            color: "var(--text-3)",
            flexShrink: 0,
          }}
        >
          {collapsed ? (
            <ChevronRight size={14} strokeWidth={1.5} />
          ) : (
            <ChevronLeft size={14} strokeWidth={1.5} />
          )}
        </button>
      </div>

      {/* Navigation */}
      <nav style={{ flex: 1, padding: "4px 0", overflowY: "auto" }}>
        {NAV_ITEMS.map(({ id, label, Icon }) => {
          const active = activePage === id;
          return (
            <button
              key={id}
              onClick={() => onNavigate(id)}
              aria-label={label}
              aria-current={active ? "page" : undefined}
              style={{
                display: "flex",
                alignItems: "center",
                gap: 10,
                width: "100%",
                height: 36,
                padding: collapsed ? "0 15px" : "0 16px",
                justifyContent: collapsed ? "center" : "flex-start",
                color: active ? "var(--text)" : "var(--text-2)",
                backgroundColor: active ? "var(--surface-2)" : "transparent",
                borderLeft: `2px solid ${active ? "var(--accent)" : "transparent"}`,
                fontSize: 13,
                whiteSpace: "nowrap",
                textAlign: "left",
              }}
            >
              <Icon size={16} strokeWidth={1.5} style={{ flexShrink: 0 }} />
              {!collapsed && label}
            </button>
          );
        })}
      </nav>

      {/* Footer */}
      <div
        style={{
          borderTop: "1px solid var(--border)",
          padding: collapsed ? "10px 0" : "10px 16px",
          display: "flex",
          flexDirection: "column",
          gap: 6,
          flexShrink: 0,
          alignItems: collapsed ? "center" : "flex-start",
        }}
      >
        <div style={{ display: "flex", alignItems: "center", gap: 7 }}>
          <span
            style={{
              width: 7,
              height: 7,
              borderRadius: "50%",
              backgroundColor: dotColor,
              flexShrink: 0,
              display: "block",
              transition: "background-color 300ms",
            }}
          />
          {!collapsed && (
            <span style={{ fontSize: 11, color: "var(--text-3)", whiteSpace: "nowrap" }}>
              {ollamaLabel}
            </span>
          )}
        </div>
        {!collapsed && (
          <span style={{ fontSize: 11, color: "var(--text-3)" }}>v0.1.0</span>
        )}
      </div>
    </aside>
  );
}

// ---------------------------------------------------------------------------
// Main area
// ---------------------------------------------------------------------------

interface MainAreaProps {
  activePage: PageId;
  messages: ReturnType<typeof useChat>["messages"];
  isStreaming: boolean;
  modelMode: ModelMode;
  ollamaStatus: ReturnType<typeof useOllama>;
  speedModel: string;
  balancedModel: string;
  memoryEnabled: boolean;
  onSend: (text: string) => void;
  onStop: () => void;
  onModelModeChange: (mode: ModelMode) => void;
  onSpeedModelChange: (model: string) => void;
  onBalancedModelChange: (model: string) => void;
  onMemoryToggle: () => void;
}

function MainArea({
  activePage,
  messages,
  isStreaming,
  modelMode,
  ollamaStatus,
  speedModel,
  balancedModel,
  memoryEnabled,
  onSend,
  onStop,
  onModelModeChange,
  onSpeedModelChange,
  onBalancedModelChange,
  onMemoryToggle,
}: MainAreaProps) {
  const ollamaRunning = ollamaStatus?.running ?? null;

  return (
    <main
      style={{
        flex: 1,
        display: "flex",
        flexDirection: "column",
        overflow: "hidden",
        backgroundColor: "var(--bg)",
      }}
    >
      {/* Header — always visible */}
      <div
        style={{
          height: 48,
          display: "flex",
          alignItems: "center",
          padding: "0 16px",
          borderBottom: "1px solid var(--border)",
          flexShrink: 0,
          gap: 12,
        }}
      >
        <span
          style={{
            fontSize: 13,
            color: "var(--text-2)",
            textTransform: "capitalize",
            flex: 1,
          }}
        >
          {activePage}
        </span>

        {/* Model selector is only shown on chat pages */}
        {activePage !== "settings" && (
          <ModelModeSelector
            value={modelMode}
            onChange={onModelModeChange}
            ollamaRunning={ollamaRunning}
          />
        )}
      </div>

      {/* Page content */}
      {activePage === "settings" ? (
        <SettingsPage
          ollamaStatus={ollamaStatus}
          speedModel={speedModel}
          balancedModel={balancedModel}
          onSpeedModelChange={onSpeedModelChange}
          onBalancedModelChange={onBalancedModelChange}
        />
      ) : (
        <>
          <MessageList messages={messages} isStreaming={isStreaming} />
          <Composer
            onSend={onSend}
            onStop={onStop}
            isStreaming={isStreaming}
            disabled={ollamaRunning === false}
            memoryEnabled={memoryEnabled}
            onMemoryToggle={onMemoryToggle}
          />
        </>
      )}
    </main>
  );
}

// ---------------------------------------------------------------------------
// Model mode selector — Section 14 (Chat header)
// ---------------------------------------------------------------------------

const MODE_LABELS: Record<ModelMode, string> = {
  speed: "Speed",
  balanced: "Balanced",
  boost: "Boost",
};

const MODES: ModelMode[] = ["speed", "balanced", "boost"];

interface ModelModeSelectorProps {
  value: ModelMode;
  onChange: (mode: ModelMode) => void;
  ollamaRunning: boolean | null;
}

function ModelModeSelector({ value, onChange, ollamaRunning }: ModelModeSelectorProps) {
  return (
    <div
      role="group"
      aria-label="Model mode"
      style={{
        display: "flex",
        backgroundColor: "var(--surface-2)",
        borderRadius: 5,
        padding: 2,
        gap: 2,
      }}
    >
      {MODES.map((mode) => {
        const active = value === mode;
        const boostDisabled = mode === "boost";
        return (
          <button
            key={mode}
            onClick={() => !boostDisabled && onChange(mode)}
            disabled={boostDisabled || ollamaRunning === false}
            title={boostDisabled ? "Boost requires an API key (Phase 2)" : undefined}
            style={{
              fontSize: 11,
              fontWeight: active ? 600 : 400,
              padding: "3px 10px",
              borderRadius: 3,
              backgroundColor: active ? "var(--surface)" : "transparent",
              color: boostDisabled
                ? "var(--text-3)"
                : active
                ? "var(--text)"
                : "var(--text-2)",
              cursor: boostDisabled ? "not-allowed" : "pointer",
              transition: "background-color 150ms",
              border: active ? "1px solid var(--border)" : "1px solid transparent",
              opacity: boostDisabled ? 0.5 : 1,
            }}
          >
            {MODE_LABELS[mode]}
          </button>
        );
      })}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Right panel
// ---------------------------------------------------------------------------

import { Memory } from "./hooks/useMemory";
import { X } from "lucide-react";

interface RightPanelProps {
  memories: Memory[];
  onDeleteMemory: (id: string) => void;
}

function RightPanel({ memories, onDeleteMemory }: RightPanelProps) {
  return (
    <aside
      style={{
        width: 280,
        minWidth: 280,
        display: "flex",
        flexDirection: "column",
        backgroundColor: "var(--surface)",
        borderLeft: "1px solid var(--border)",
        flexShrink: 0,
        overflowY: "auto",
      }}
    >
      {/* Used Memory — active in Phase 2 */}
      <div style={{ padding: "12px 16px", borderBottom: "1px solid var(--border-subtle)" }}>
        <span
          style={{
            fontSize: 11,
            fontWeight: 600,
            color: "var(--text-3)",
            textTransform: "uppercase",
            letterSpacing: "0.06em",
          }}
        >
          Used Memory
        </span>
        <div style={{ marginTop: 8 }}>
          {memories.length === 0 ? (
            <p style={{ fontSize: 12, color: "var(--text-3)" }}>No memories yet</p>
          ) : (
            <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
              {memories.map((m) => (
                <div
                  key={m.id}
                  style={{
                    display: "flex",
                    alignItems: "flex-start",
                    gap: 6,
                    backgroundColor: "var(--surface-2)",
                    borderRadius: 4,
                    padding: "6px 8px",
                  }}
                >
                  <span style={{ fontSize: 12, color: "var(--text-2)", flex: 1, lineHeight: 1.4 }}>
                    {m.content}
                  </span>
                  <button
                    onClick={() => onDeleteMemory(m.id)}
                    title="Delete memory"
                    style={{
                      display: "flex",
                      alignItems: "center",
                      justifyContent: "center",
                      width: 16,
                      height: 16,
                      borderRadius: 3,
                      color: "var(--text-3)",
                      flexShrink: 0,
                      marginTop: 1,
                    }}
                  >
                    <X size={10} strokeWidth={1.5} />
                  </button>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>

      {/* Remaining sections — placeholder until later phases */}
      {RIGHT_PANEL_SECTIONS.filter((s) => s !== "Used memory").map((section) => (
        <div
          key={section}
          style={{
            padding: "12px 16px",
            borderBottom: "1px solid var(--border-subtle)",
          }}
        >
          <span
            style={{
              fontSize: 11,
              fontWeight: 600,
              color: "var(--text-3)",
              textTransform: "uppercase",
              letterSpacing: "0.06em",
            }}
          >
            {section}
          </span>
          <div style={{ marginTop: 8, fontSize: 12, color: "var(--text-3)" }}>
            No data
          </div>
        </div>
      ))}
    </aside>
  );
}
