// Spec reference: Section 14 (Chat UI), Section 15 (All Application Screens)
// Phase 1: functional chat, Ollama detection, model mode selector.
// Phase 3.5: conversation persistence wired in.

import { useCallback, useState } from "react";
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
  X,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { Composer } from "./components/Composer";
import { ConversationList } from "./components/ConversationList";
import { KnowledgePage } from "./components/KnowledgePage";
import { MessageList } from "./components/MessageList";
import { MemoryPage } from "./components/MemoryPage";
import { PlaceholderPage } from "./components/PlaceholderPage";
import { SettingsPage } from "./components/SettingsPage";
import { useChat } from "./hooks/useChat";
import { ConversationMessage, useConversations } from "./hooks/useConversations";
import { Memory, useMemory } from "./hooks/useMemory";
import { useOllama } from "./hooks/useOllama";
import { Message, ModelMode, MODEL_MAP } from "./types";
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

// Embedding model used for knowledge indexing and search
const EMBED_MODEL = "nomic-embed-text";

// Convert a DB ConversationMessage row to the UI Message shape.
function dbMsgToMessage(m: ConversationMessage): Message {
  return {
    id: m.id,
    role: m.role,
    content: m.content,
    thinking: m.thinking ?? undefined,
    isThinking: false,
  };
}

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
  const [knowledgeEnabled, setKnowledgeEnabled] = useState(false);

  const ollamaStatus = useOllama();
  const { memories, deleteMemory } = useMemory();

  const {
    conversations,
    activeId: activeConvId,
    createConversation,
    selectConversation,
    saveCurrentTurn,
    deleteConversation,
    renameConversation,
  } = useConversations();

  // Called by useChat after each successful reply.
  const handleTurnComplete = useCallback(
    (userContent: string, assistantContent: string, assistantThinking: string | null) => {
      saveCurrentTurn(userContent, assistantContent, assistantThinking);
    },
    [saveCurrentTurn]
  );

  const {
    messages,
    isStreaming,
    sendMessage,
    stopStreaming,
    loadMessages,
    clearMessages,
  } = useChat({
    modelMode,
    speedModel,
    memoryEnabled,
    knowledgeEnabled,
    embedModel: EMBED_MODEL,
    onTurnComplete: handleTurnComplete,
  });

  // Create a new conversation and clear the chat pane.
  const handleNewConversation = useCallback(async () => {
    clearMessages();
    // Title will be auto-updated to the first message later; for now use placeholder.
    await createConversation("New chat").catch(() => {});
  }, [clearMessages, createConversation]);

  // Switch to an existing conversation and hydrate the message list.
  const handleSelectConversation = useCallback(
    async (id: string) => {
      const dbMessages = await selectConversation(id);
      loadMessages(dbMessages.map(dbMsgToMessage));
    },
    [selectConversation, loadMessages]
  );

  // Wrap sendMessage to auto-create a conversation if none is active.
  const handleSend = useCallback(
    async (text: string) => {
      if (!activeConvId) {
        // Derive a short title from the first user message (max 40 chars)
        const title = text.trim().slice(0, 40) || "New chat";
        await createConversation(title).catch(() => {});
      }
      sendMessage(text);
    },
    [activeConvId, createConversation, sendMessage]
  );

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
        knowledgeEnabled={knowledgeEnabled}
        memories={memories}
        conversations={conversations}
        activeConvId={activeConvId}
        onSend={handleSend}
        onStop={stopStreaming}
        onModelModeChange={setModelMode}
        onSpeedModelChange={setSpeedModel}
        onBalancedModelChange={setBalancedModel}
        onMemoryToggle={() => setMemoryEnabled((v) => !v)}
        onKnowledgeToggle={() => setKnowledgeEnabled((v) => !v)}
        onDeleteMemory={deleteMemory}
        onNewConversation={handleNewConversation}
        onSelectConversation={handleSelectConversation}
        onDeleteConversation={deleteConversation}
        onRenameConversation={renameConversation}
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
  knowledgeEnabled: boolean;
  memories: Memory[];
  conversations: ReturnType<typeof useConversations>["conversations"];
  activeConvId: string | null;
  onSend: (text: string) => void;
  onStop: () => void;
  onModelModeChange: (mode: ModelMode) => void;
  onSpeedModelChange: (model: string) => void;
  onBalancedModelChange: (model: string) => void;
  onMemoryToggle: () => void;
  onKnowledgeToggle: () => void;
  onDeleteMemory: (id: string) => void;
  onNewConversation: () => void;
  onSelectConversation: (id: string) => void;
  onDeleteConversation: (id: string) => void;
  onRenameConversation: (id: string, title: string) => void;
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
  knowledgeEnabled,
  memories,
  conversations,
  activeConvId,
  onSend,
  onStop,
  onModelModeChange,
  onSpeedModelChange,
  onBalancedModelChange,
  onMemoryToggle,
  onKnowledgeToggle,
  onDeleteMemory,
  onNewConversation,
  onSelectConversation,
  onDeleteConversation,
  onRenameConversation,
}: MainAreaProps) {
  const ollamaRunning = ollamaStatus?.running ?? null;
  const activeLabel =
    NAV_ITEMS.find((n) => n.id === activePage)?.label ?? activePage;

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
      {/* Header - always visible */}
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
            flex: 1,
          }}
        >
          {activeLabel}
        </span>

        {/* Model selector shown only on Chats page */}
        {activePage === "chats" && (
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
      ) : activePage === "memory" ? (
        <MemoryPage memories={memories ?? []} onDelete={onDeleteMemory} />
      ) : activePage === "chats" ? (
        // Chats page: conversation list panel + chat area side by side
        <div style={{ display: "flex", flex: 1, overflow: "hidden" }}>
          <ConversationList
            conversations={conversations}
            activeId={activeConvId}
            onNew={onNewConversation}
            onSelect={onSelectConversation}
            onDelete={onDeleteConversation}
            onRename={onRenameConversation}
          />
          <div style={{ flex: 1, display: "flex", flexDirection: "column", overflow: "hidden" }}>
            <MessageList messages={messages} isStreaming={isStreaming} />
            <Composer
              onSend={onSend}
              onStop={onStop}
              isStreaming={isStreaming}
              disabled={ollamaRunning === false}
              memoryEnabled={memoryEnabled}
              onMemoryToggle={onMemoryToggle}
              knowledgeEnabled={knowledgeEnabled}
              onKnowledgeToggle={onKnowledgeToggle}
            />
          </div>
        </div>
      ) : activePage === "knowledge" ? (
        <KnowledgePage embedModel={EMBED_MODEL} />
      ) : activePage === "projects" ? (
        <PlaceholderPage
          Icon={NAV_ITEMS.find((n) => n.id === "projects")!.Icon}
          title="Projects"
          description="Organize chats, memory, and files by project context."
          phase="Coming in Phase 4"
        />
      ) : activePage === "today" ? (
        <PlaceholderPage
          Icon={NAV_ITEMS.find((n) => n.id === "today")!.Icon}
          title="Today"
          description="Your daily digest - open tasks, recent context, and suggested actions."
          phase="Coming in Phase 5"
        />
      ) : activePage === "tasks" ? (
        <PlaceholderPage
          Icon={NAV_ITEMS.find((n) => n.id === "tasks")!.Icon}
          title="Tasks"
          description="Track and manage tasks with AI-assisted capture and follow-up."
          phase="Coming in Phase 5"
        />
      ) : null}
    </main>
  );
}

// ---------------------------------------------------------------------------
// Model mode selector - Section 14 (Chat header)
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
      {/* Used Memory - active in Phase 2 */}
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

      {/* Remaining sections - placeholder until later phases */}
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
