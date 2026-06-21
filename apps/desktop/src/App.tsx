// Spec reference: Section 14 (Chat UI), Section 15 (All Application Screens)
// Phase 1: functional chat, Ollama detection, model mode selector.
// Phase 3.5: conversation persistence wired in.
// Phase 4: Projects wired in, project selector in chat header.
// Phase 5: webSearchEnabled wired through useChat + Composer.

import { useCallback, useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import {
  Home,
  MessageSquare,
  Dog,
  Brain,
  Plug,
  FolderOpen,
  Settings,
  ChevronLeft,
  X,
  FolderOpen as FolderIcon,
  Monitor,
  Sun,
  Moon,
  PanelRight,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { Composer } from "./components/Composer";
import { ConversationList } from "./components/ConversationList";
import { FirstRunSetup } from "./components/FirstRunSetup";
import { UpdateBanner } from "./components/UpdateBanner";
import { MessageList } from "./components/MessageList";
import { MemoryPage } from "./components/MemoryPage";
import { ConnectionsPage } from "./components/ConnectionsPage";
import { TodayPage } from "./components/TodayPage";
import { BuddyPage } from "./components/BuddyPage";
import { ProjectsPage } from "./components/ProjectsPage";
import { SettingsPage } from "./components/SettingsPage";
import { useChat } from "./hooks/useChat";
import { ConversationMessage, useConversations } from "./hooks/useConversations";
import { Memory, useMemory } from "./hooks/useMemory";
import { useOllama } from "./hooks/useOllama";
import { useProjects } from "./hooks/useProjects";
import { Message, ModelMode, MODEL_MAP } from "./types";
import "./App.css";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type PageId =
  | "today"
  | "chats"
  | "buddy"
  | "mind"
  | "connections"
  | "projects"
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
  { id: "today",       label: "Today",       Icon: Home          },
  { id: "chats",       label: "Chat",        Icon: MessageSquare },
  { id: "buddy",       label: "Buddy",       Icon: Dog           },
  { id: "mind",        label: "Mind",        Icon: Brain         },
  { id: "connections", label: "Connections", Icon: Plug          },
  { id: "projects",    label: "Projects",    Icon: FolderOpen    },
  { id: "settings",    label: "Settings",    Icon: Settings      },
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
  const [setupComplete, setSetupComplete] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(true);
  const [activePage, setActivePage] = useState<PageId>("today");
  const [modelMode, setModelMode] = useState<ModelMode>("balanced");
  const [speedModel, setSpeedModel] = useState(MODEL_MAP.speed);
  const [balancedModel, setBalancedModel] = useState(MODEL_MAP.balanced);
  const [memoryEnabled, setMemoryEnabled] = useState(true);
  const [knowledgeEnabled, setKnowledgeEnabled] = useState(false);
  const [hydeEnabled, setHydeEnabled] = useState(false);
  const [webSearchEnabled, setWebSearchEnabled] = useState(false);
  const [theme, setTheme] = useState<"dark" | "light" | "system">(
    () => (localStorage.getItem("lm-theme") as "dark" | "light" | "system") ?? "dark"
  );

  // Apply theme class to <html> element
  useEffect(() => {
    const root = document.documentElement;
    if (theme === "system") {
      const prefersDark = window.matchMedia("(prefers-color-scheme: dark)").matches;
      root.classList.toggle("light", !prefersDark);
    } else {
      root.classList.toggle("light", theme === "light");
    }
    localStorage.setItem("lm-theme", theme);
  }, [theme]);

  const ollamaStatus = useOllama();
  const { memories, links: memoryLinks, deleteMemory, deleteLink: deleteMemoryLink } = useMemory();

  const {
    conversations,
    activeId: activeConvId,
    createConversation,
    selectConversation,
    saveCurrentTurn,
    deleteConversation,
    renameConversation,
  } = useConversations();

  // Top-level useProjects instance drives the chat-header project selector.
  // ProjectsPage has its own instance; both stay in sync via Tauri events.
  const { projects, assignConversation } = useProjects();

  const [followUpQuestions, setFollowUpQuestions] = useState<string[]>([]);
  const [composerDraft, setComposerDraft] = useState<string>("");
  const [rightPanelOpen, setRightPanelOpen] = useState(false);

  // Called by useChat after each successful reply.
  const handleTurnComplete = useCallback(
    async (userContent: string, assistantContent: string, assistantThinking: string | null) => {
      saveCurrentTurn(userContent, assistantContent, assistantThinking);
      // Fetch follow-up question suggestions from the Speed model
      try {
        const qs = await invoke<string[]>("get_followup_questions", {
          lastUser: userContent,
          lastAssistant: assistantContent.slice(0, 1200), // trim to avoid huge prompts
          model: undefined,
        });
        setFollowUpQuestions(qs ?? []);
      } catch {
        setFollowUpQuestions([]);
      }
    },
    [saveCurrentTurn]
  );

  const {
    messages,
    isStreaming,
    isSearching,
    webSources,
    sendMessage,
    stopStreaming,
    loadMessages,
    clearMessages,
  } = useChat({
    modelMode,
    speedModel,
    memoryEnabled,
    knowledgeEnabled,
    hydeEnabled,
    webSearchEnabled,
    embedModel: EMBED_MODEL,
    onTurnComplete: handleTurnComplete,
  });

  // Create a new conversation and clear the chat pane.
  const handleNewConversation = useCallback(async () => {
    clearMessages();
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
      setFollowUpQuestions([]); // clear stale follow-ups on new send
      if (!activeConvId) {
        const title = text.trim().slice(0, 40) || "New chat";
        await createConversation(title).catch(() => {});
      }
      sendMessage(text);
    },
    [activeConvId, createConversation, sendMessage]
  );

  const handleAssignProject = useCallback(
    (projectId: string | null) => {
      if (!activeConvId) return;
      assignConversation(activeConvId, projectId).catch(() => {});
    },
    [activeConvId, assignConversation]
  );

  return (
    <>
      {!setupComplete && (
        <FirstRunSetup onComplete={() => setSetupComplete(true)} />
      )}
      {setupComplete && <UpdateBanner />}
      <div
        style={{
          display: "flex",
          height: "100vh",
          overflow: "hidden",
          backgroundColor: "var(--bg)",
          visibility: setupComplete ? "visible" : "hidden",
        }}
      >
      <Sidebar
        collapsed={sidebarCollapsed}
        activePage={activePage}
        ollamaRunning={ollamaStatus?.running ?? null}
        theme={theme}
        onNavigate={setActivePage}
        onToggle={() => setSidebarCollapsed((c) => !c)}
        onThemeChange={setTheme}
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
        webSearchEnabled={webSearchEnabled}
        memories={memories}
        conversations={conversations}
        activeConvId={activeConvId}
        projects={projects}
        onSend={handleSend}
        onStop={stopStreaming}
        onModelModeChange={setModelMode}
        onSpeedModelChange={setSpeedModel}
        onBalancedModelChange={setBalancedModel}
        onMemoryToggle={() => setMemoryEnabled((v) => !v)}
        onKnowledgeToggle={() => setKnowledgeEnabled((v) => !v)}
        onWebSearchToggle={() => setWebSearchEnabled((v) => !v)}
        hydeEnabled={hydeEnabled}
        onHydeToggle={() => setHydeEnabled((v) => !v)}
        onDeleteMemory={deleteMemory}
        memoryLinks={memoryLinks}
        onDeleteMemoryLink={deleteMemoryLink}
        followUpQuestions={followUpQuestions}
        onSendFollowUp={handleSend}
        onNewConversation={handleNewConversation}
        onSelectConversation={handleSelectConversation}
        onDeleteConversation={deleteConversation}
        onRenameConversation={renameConversation}
        onAssignProject={handleAssignProject}
        composerDraft={composerDraft}
        onComposerDraftApplied={() => setComposerDraft("")}
        onSetComposerDraft={setComposerDraft}
        rightPanelOpen={rightPanelOpen}
        onToggleRightPanel={() => setRightPanelOpen((v) => !v)}
        hasRightPanelContent={memories.length > 0 || webSources.length > 0}
        isSearching={isSearching}
        onNavigate={setActivePage}
      />
      {rightPanelOpen && (memories.length > 0 || webSources.length > 0) && (
        <RightPanel memories={memories} webSources={webSources} onDeleteMemory={deleteMemory} />
      )}
      </div>
    </>
  );
}

// ---------------------------------------------------------------------------
// Sidebar
// ---------------------------------------------------------------------------

interface SidebarProps {
  collapsed: boolean;
  activePage: PageId;
  ollamaRunning: boolean | null;
  theme: "dark" | "light" | "system";
  onNavigate: (page: PageId) => void;
  onToggle: () => void;
  onThemeChange: (t: "dark" | "light" | "system") => void;
}

function Sidebar({ collapsed, activePage, ollamaRunning, theme, onNavigate, onToggle, onThemeChange }: SidebarProps) {
  const width = collapsed ? SIDEBAR_COLLAPSED : SIDEBAR_EXPANDED;

  return (
    <aside
      style={{
        width,
        minWidth: width,
        display: "flex",
        flexDirection: "column",
        backgroundColor: "var(--surface)",
        borderRight: "1px solid var(--border)",
        flexShrink: 0,
        transition: "width 200ms ease, min-width 200ms ease",
        overflow: "hidden",
      }}
    >
      {/* Logo / title row */}
      <div
        style={{
          height: 52,
          display: "flex",
          alignItems: "center",
          padding: "0 10px",
          gap: 8,
          borderBottom: "1px solid var(--border)",
          flexShrink: 0,
        }}
      >
        {/* Paw logo mark */}
        <div
          onClick={onToggle}
          title={collapsed ? "Expand sidebar" : "Collapse sidebar"}
          style={{
            width: 28,
            height: 28,
            borderRadius: 8,
            background: "linear-gradient(135deg, var(--accent) 0%, #818cf8 100%)",
            display: "flex",
            alignItems: "center",
            justifyContent: "center",
            fontSize: 14,
            flexShrink: 0,
            cursor: "pointer",
            userSelect: "none",
          }}
        >
          🐾
        </div>
        {!collapsed && (
          <>
            <span style={{ fontSize: 13, fontWeight: 700, color: "var(--text)", flex: 1, whiteSpace: "nowrap", letterSpacing: "-0.01em" }}>
              LocalMind
            </span>
            <button
              onClick={onToggle}
              title="Collapse sidebar"
              style={{
                display: "flex",
                alignItems: "center",
                justifyContent: "center",
                width: 22,
                height: 22,
                borderRadius: 4,
                color: "var(--text-3)",
              }}
            >
              <ChevronLeft size={13} strokeWidth={1.5} />
            </button>
          </>
        )}
      </div>

      {/* Nav items */}
      <nav style={{ flex: 1, padding: "8px 0", overflowY: "auto" }}>
        {NAV_ITEMS.map(({ id, label, Icon }) => {
          const active = activePage === id;
          return (
            <button
              key={id}
              onClick={() => onNavigate(id)}
              title={collapsed ? label : undefined}
              style={{
                display: "flex",
                alignItems: "center",
                margin: "1px 6px",
                height: 34,
                width: "calc(100% - 12px)",
                padding: collapsed ? "0 9px" : "0 10px",
                gap: 9,
                fontSize: 13,
                fontWeight: active ? 500 : 400,
                color: active ? "var(--text)" : "var(--text-2)",
                backgroundColor: active ? "var(--surface-2)" : "transparent",
                borderRadius: 6,
                justifyContent: collapsed ? "center" : undefined,
                whiteSpace: "nowrap",
                transition: "background-color 120ms, color 120ms",
              }}
            >
              <Icon size={collapsed ? 17 : 15} strokeWidth={active ? 2 : 1.5} style={{ color: active ? "var(--accent)" : undefined }} />
              {!collapsed && label}
            </button>
          );
        })}
      </nav>

      {/* Footer: Ollama status + theme toggle */}
      <div
        style={{
          padding: collapsed ? "10px 8px" : "10px 12px",
          borderTop: "1px solid var(--border)",
          display: "flex",
          flexDirection: "column",
          gap: 8,
          flexShrink: 0,
        }}
      >
        {/* Ollama status */}
        {!collapsed && (
          <div style={{ display: "flex", alignItems: "center", gap: 7 }}>
            <span
              style={{
                width: 7,
                height: 7,
                borderRadius: "50%",
                backgroundColor:
                  ollamaRunning === null
                    ? "var(--text-3)"
                    : ollamaRunning
                    ? "#22c55e"
                    : "#ef4444",
                display: "block",
                flexShrink: 0,
              }}
            />
            <span style={{ fontSize: 11, color: "var(--text-3)" }}>
              {ollamaRunning === null ? "Checking..." : ollamaRunning ? "Ollama running" : "Ollama offline"}
            </span>
          </div>
        )}

        {/* Theme toggle: Monitor / Sun / Moon */}
        <div
          style={{
            display: "flex",
            gap: 2,
            backgroundColor: "var(--surface-2)",
            borderRadius: 6,
            padding: 2,
            justifyContent: collapsed ? "center" : undefined,
          }}
        >
          {(
            [
              { id: "system" as const, Icon: Monitor, label: "System theme" },
              { id: "light"  as const, Icon: Sun,     label: "Light theme"  },
              { id: "dark"   as const, Icon: Moon,    label: "Dark theme"   },
            ] as const
          ).map(({ id, Icon, label }) => {
            const active = theme === id;
            return (
              <button
                key={id}
                onClick={() => onThemeChange(id)}
                title={label}
                style={{
                  flex: collapsed ? undefined : 1,
                  display: "flex",
                  alignItems: "center",
                  justifyContent: "center",
                  padding: "4px 0",
                  borderRadius: 4,
                  backgroundColor: active ? "var(--surface)" : "transparent",
                  color: active ? "var(--accent)" : "var(--text-3)",
                  border: active ? "1px solid var(--border)" : "1px solid transparent",
                  cursor: "pointer",
                  transition: "background-color 150ms, color 150ms",
                }}
              >
                <Icon size={13} strokeWidth={1.5} />
              </button>
            );
          })}
        </div>
      </div>
    </aside>
  );
}

// ---------------------------------------------------------------------------
// Main area
// ---------------------------------------------------------------------------

interface MainAreaProps {
  activePage: PageId;
  messages: Message[];
  isStreaming: boolean;
  modelMode: ModelMode;
  ollamaStatus: ReturnType<typeof useOllama>;
  speedModel: string;
  balancedModel: string;
  memoryEnabled: boolean;
  knowledgeEnabled: boolean;
  webSearchEnabled: boolean;
  memories: Memory[];
  conversations: ReturnType<typeof useConversations>["conversations"];
  activeConvId: string | null;
  projects: ReturnType<typeof useProjects>["projects"];
  onSend: (text: string) => void;
  onStop: () => void;
  onModelModeChange: (mode: ModelMode) => void;
  onSpeedModelChange: (model: string) => void;
  onBalancedModelChange: (model: string) => void;
  onMemoryToggle: () => void;
  onKnowledgeToggle: () => void;
  onWebSearchToggle: () => void;
  hydeEnabled: boolean;
  onHydeToggle: () => void;
  onDeleteMemory: (id: string) => void;
  memoryLinks: import("./hooks/useMemory").MemoryLink[];
  onDeleteMemoryLink: (linkId: string) => void;
  followUpQuestions: string[];
  onSendFollowUp: (question: string) => void;
  onNewConversation: () => void;
  onSelectConversation: (id: string) => void;
  onDeleteConversation: (id: string) => void;
  onRenameConversation: (id: string, title: string) => void;
  onAssignProject: (projectId: string | null) => void;
  composerDraft: string;
  onComposerDraftApplied: () => void;
  onSetComposerDraft: (text: string) => void;
  rightPanelOpen: boolean;
  onToggleRightPanel: () => void;
  hasRightPanelContent: boolean;
  isSearching: boolean;
  onNavigate: (page: PageId) => void;
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
  webSearchEnabled,
  memories,
  conversations,
  activeConvId,
  projects,
  onSend,
  onStop,
  onModelModeChange,
  onSpeedModelChange,
  onBalancedModelChange,
  onMemoryToggle,
  onKnowledgeToggle,
  onWebSearchToggle,
  hydeEnabled,
  onHydeToggle,
  onDeleteMemory,
  memoryLinks,
  onDeleteMemoryLink,
  followUpQuestions,
  onSendFollowUp,
  onNewConversation,
  onSelectConversation,
  onDeleteConversation,
  onRenameConversation,
  onAssignProject,
  composerDraft,
  onComposerDraftApplied,
  onSetComposerDraft,
  rightPanelOpen,
  onToggleRightPanel,
  hasRightPanelContent,
  isSearching,
  onNavigate,
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

        {/* Controls shown only on Chats page */}
        {activePage === "chats" && activeConvId && projects.length > 0 && (
          <ProjectSelector
            projects={projects}
            onAssign={onAssignProject}
          />
        )}
        {/* Right panel toggle — only visible when there's actual content */}
        {hasRightPanelContent && (
          <button
            onClick={onToggleRightPanel}
            title={rightPanelOpen ? "Hide side panel" : "Show side panel"}
            style={{
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              width: 28,
              height: 28,
              borderRadius: 5,
              backgroundColor: rightPanelOpen ? "var(--surface-2)" : "transparent",
              color: rightPanelOpen ? "var(--accent)" : "var(--text-3)",
              border: rightPanelOpen ? "1px solid var(--border)" : "1px solid transparent",
              cursor: "pointer",
              transition: "background-color 150ms, color 150ms",
            }}
          >
            <PanelRight size={15} strokeWidth={1.5} />
          </button>
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
          hydeEnabled={hydeEnabled}
          onHydeToggle={onHydeToggle}
        />
      ) : activePage === "chats" ? (
        // Chat page: conversation list + companion chat area
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
            {messages.length === 0 ? (
              /* ── Empty / welcome state ── */
              <div style={{ flex: 1, display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center", padding: "0 24px 60px", gap: 24, overflow: "hidden" }}>
                {/* Buddy avatar + greeting */}
                <div style={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 12 }}>
                  <div style={{ width: 64, height: 64, borderRadius: "50%", background: "linear-gradient(135deg, #6366f1 0%, #818cf8 100%)", display: "flex", alignItems: "center", justifyContent: "center", fontSize: 32, boxShadow: "0 4px 20px rgba(99,102,241,0.3)" }}>🐾</div>
                  <div style={{ textAlign: "center" }}>
                    <h1 style={{ fontSize: 22, fontWeight: 700, color: "var(--text)", letterSpacing: "-0.02em", margin: "0 0 6px" }}>
                      Hey! What's on your mind?
                    </h1>
                    <p style={{ fontSize: 13, color: "var(--text-3)", margin: 0 }}>
                      Everything stays on this machine. Speak freely.
                    </p>
                  </div>
                </div>
                <div style={{ width: "100%", maxWidth: 660 }}>
                  <Composer onSend={onSend} onStop={onStop} isStreaming={isStreaming} disabled={ollamaRunning === false} memoryEnabled={memoryEnabled} onMemoryToggle={onMemoryToggle} knowledgeEnabled={knowledgeEnabled} onKnowledgeToggle={onKnowledgeToggle} webSearchEnabled={webSearchEnabled} onWebSearchToggle={onWebSearchToggle} modelMode={modelMode} onModelModeChange={onModelModeChange} ollamaRunning={ollamaRunning} draft={composerDraft} onDraftApplied={onComposerDraftApplied} />
                </div>
                <div style={{ display: "flex", gap: 8, flexWrap: "wrap", justifyContent: "center", maxWidth: 660 }}>
                  {[
                    { label: "📧 Check my emails", text: "Can you check my recent emails and summarize what's important?" },
                    { label: "✍️ Draft a reply", text: "Help me draft a reply to:" },
                    { label: "📅 What's on today?", text: "What do I have going on today based on my calendar and messages?" },
                    { label: "💡 Brainstorm with me", text: "Help me brainstorm ideas for:" },
                    { label: "🧠 What do you remember?", text: "What do you remember about me and my recent activities?" },
                    { label: "💬 Catch me up", text: "Give me a quick catch-up on what happened while I was away." },
                  ].map(({ label, text }) => (
                    <button key={label} onClick={() => onSetComposerDraft(text)}
                      style={{ padding: "7px 14px", background: "var(--surface)", border: "1px solid var(--border)", borderRadius: 20, fontSize: 12, color: "var(--text-2)", cursor: "pointer", whiteSpace: "nowrap", transition: "border-color 0.12s, color 0.12s" }}
                      onMouseEnter={e => { (e.currentTarget as HTMLButtonElement).style.borderColor = "var(--accent)"; (e.currentTarget as HTMLButtonElement).style.color = "var(--text)"; }}
                      onMouseLeave={e => { (e.currentTarget as HTMLButtonElement).style.borderColor = "var(--border)"; (e.currentTarget as HTMLButtonElement).style.color = "var(--text-2)"; }}
                    >{label}</button>
                  ))}
                </div>
              </div>
            ) : (
              /* ── Active chat ── */
              <>
                <MessageList messages={messages} isStreaming={isStreaming} followUpQuestions={followUpQuestions} onSendFollowUp={onSendFollowUp} isSearching={isSearching} />
                <Composer onSend={onSend} onStop={onStop} isStreaming={isStreaming} disabled={ollamaRunning === false} memoryEnabled={memoryEnabled} onMemoryToggle={onMemoryToggle} knowledgeEnabled={knowledgeEnabled} onKnowledgeToggle={onKnowledgeToggle} webSearchEnabled={webSearchEnabled} onWebSearchToggle={onWebSearchToggle} modelMode={modelMode} onModelModeChange={onModelModeChange} ollamaRunning={ollamaRunning} draft={composerDraft} onDraftApplied={onComposerDraftApplied} />
              </>
            )}
          </div>
        </div>
      ) : activePage === "today" ? (
        <TodayPage onNavigateToChat={(prompt) => {
          onNavigate("chats");
          if (prompt) setTimeout(() => onSetComposerDraft(prompt), 100);
        }} />
      ) : activePage === "buddy" ? (
        <BuddyPage />
      ) : activePage === "mind" ? (
        <MemoryPage
          memories={memories ?? []}
          links={memoryLinks ?? []}
          onDelete={onDeleteMemory}
          onDeleteLink={onDeleteMemoryLink}
        />
      ) : activePage === "connections" ? (
        <ConnectionsPage />
      ) : activePage === "projects" ? (
        <ProjectsPage />
      ) : null}
    </main>
  );
}

// ---------------------------------------------------------------------------
// Project selector - shown in chat header when a conversation is active
// ---------------------------------------------------------------------------

interface ProjectSelectorProps {
  projects: ReturnType<typeof useProjects>["projects"];
  onAssign: (projectId: string | null) => void;
}

function ProjectSelector({ projects, onAssign }: ProjectSelectorProps) {
  const [open, setOpen] = useState(false);

  return (
    <div style={{ position: "relative" }}>
      <button
        onClick={() => setOpen((v) => !v)}
        title="Assign conversation to a project"
        style={{
          display: "flex",
          alignItems: "center",
          gap: 5,
          fontSize: 11,
          padding: "3px 10px",
          borderRadius: 4,
          backgroundColor: "var(--surface-2)",
          color: "var(--text-2)",
          border: "1px solid var(--border)",
        }}
      >
        <FolderIcon size={13} strokeWidth={1.5} />
        Project
      </button>

      {open && (
        <>
          {/* Backdrop */}
          <div
            onClick={() => setOpen(false)}
            style={{ position: "fixed", inset: 0, zIndex: 10 }}
          />
          {/* Dropdown */}
          <div
            style={{
              position: "absolute",
              top: "calc(100% + 6px)",
              right: 0,
              zIndex: 11,
              backgroundColor: "var(--surface)",
              border: "1px solid var(--border)",
              borderRadius: 6,
              boxShadow: "0 4px 16px rgba(0,0,0,0.15)",
              minWidth: 180,
              maxHeight: 260,
              overflowY: "auto",
              padding: 4,
            }}
          >
            <button
              onClick={() => { onAssign(null); setOpen(false); }}
              style={{
                display: "block",
                width: "100%",
                textAlign: "left",
                fontSize: 12,
                padding: "7px 10px",
                borderRadius: 4,
                color: "var(--text-3)",
              }}
            >
              No project
            </button>
            <div style={{ height: 1, backgroundColor: "var(--border-subtle)", margin: "2px 0" }} />
            {projects.map((p) => (
              <button
                key={p.id}
                onClick={() => { onAssign(p.id); setOpen(false); }}
                style={{
                  display: "block",
                  width: "100%",
                  textAlign: "left",
                  fontSize: 12,
                  padding: "7px 10px",
                  borderRadius: 4,
                  color: "var(--text-2)",
                }}
              >
                {p.name}
              </button>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

// Right panel
// ---------------------------------------------------------------------------

import type { WebSource } from "./hooks/useChat";

interface RightPanelProps {
  memories: Memory[];
  webSources: WebSource[];
  onDeleteMemory: (id: string) => void;
}

function RightPanel({ memories, webSources, onDeleteMemory }: RightPanelProps) {
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
      {/* Used Memory — only rendered when there are memories */}
      {memories.length > 0 && (
        <div style={{ padding: "12px 16px", borderBottom: "1px solid var(--border-subtle)" }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: "var(--text-3)", textTransform: "uppercase", letterSpacing: "0.06em" }}>
            Used Memory
          </span>
          <div style={{ marginTop: 8, display: "flex", flexDirection: "column", gap: 6 }}>
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
        </div>
      )}

      {/* Sources — only rendered when web search returned results */}
      {webSources.length > 0 && (
        <div style={{ padding: "12px 16px" }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: "var(--text-3)", textTransform: "uppercase", letterSpacing: "0.06em" }}>
            Sources
          </span>
          <div style={{ marginTop: 8, display: "flex", flexDirection: "column", gap: 8 }}>
            {webSources.map((s, i) => (
              <a
                key={i}
                href={s.url}
                target="_blank"
                rel="noopener noreferrer"
                style={{
                  display: "block",
                  backgroundColor: "var(--surface-2)",
                  borderRadius: 4,
                  padding: "7px 9px",
                  textDecoration: "none",
                  border: "1px solid var(--border)",
                }}
              >
                <div style={{ fontSize: 12, fontWeight: 600, color: "var(--text)", lineHeight: 1.3, marginBottom: 3 }}>
                  {s.title || s.url}
                </div>
                <div style={{ fontSize: 11, color: "var(--text-3)", marginBottom: 4, wordBreak: "break-all" }}>
                  {s.url}
                </div>
                {s.snippet && (
                  <div style={{ fontSize: 11, color: "var(--text-2)", lineHeight: 1.4 }}>
                    {s.snippet.slice(0, 120)}{s.snippet.length > 120 ? "..." : ""}
                  </div>
                )}
              </a>
            ))}
          </div>
        </div>
      )}

    </aside>
  );
}
