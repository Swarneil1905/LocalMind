import { conversationMessages } from "./lib/conversationMessages";
import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import {
  BookOpen,
  Brain,
  CheckSquare,
  ChevronRight,
  FolderOpen,
  Home,
  MessageSquare,
  PanelLeft,
  Plug,
  SquarePen,
  PictureInPicture2,
  Maximize2,
  Pin,
  Minus,
  Settings,
  ShieldCheck,
  X,
  AudioLines,
  Film,
} from "lucide-react";
import { Brand } from "./components/Brand";
import { ChatStarters } from "./components/ChatStarters";
import { ChatReadiness } from "./components/ChatReadiness";
import { useMiniWindow } from "./hooks/useMiniWindow";
import { Composer } from "./components/Composer";
import { ConversationList } from "./components/ConversationList";
import { FirstRunSetup } from "./components/FirstRunSetup";
import { UpdateBanner } from "./components/UpdateBanner";
import { MessageList } from "./components/MessageList";
import { TaskActions } from "./components/TaskActions";
import { MemoryPage } from "./components/MemoryPage";
import { ConnectionsPage } from "./components/ConnectionsPage";
import { TodayPage } from "./components/TodayPage";
import { BuddyPage } from "./components/BuddyPage";
import { ProjectsPage } from "./components/ProjectsPage";
import { KnowledgePage } from "./components/KnowledgePage";
import { TasksPage } from "./components/TasksPage";
import { SettingsPage } from "./components/SettingsPage";
import { MediaPage } from "./components/MediaPage";
import { useChat, type SendSelection } from "./hooks/useChat";
import { useProviders } from "./hooks/useProviders";
import { useAttachments } from "./hooks/useAttachments";
import { CloudConsent } from "./components/CloudConsent";
import type { CloudProvider, CloudScope, ProviderId } from "./lib/providers";
import { useConversations } from "./hooks/useConversations";
import { useMemory } from "./hooks/useMemory";
import { useOllama } from "./hooks/useOllama";
import { useSidecarStatus } from "./hooks/useSidecarStatus";
import { useModelReadiness } from "./hooks/useModelReadiness";
import { useProjects } from "./hooks/useProjects";
import { MODEL_MAP } from "./types";
import type { ModelMode, WebSource } from "./types";
import "./App.css";
import "./workspace-design.css";
import "./calm-surfaces.css";

type PageId =
  | "today"
  | "chats"
  | "mind"
  | "knowledge"
  | "connections"
  | "projects"
  | "tasks"
  | "buddy"
  | "media"
  | "settings";
type Theme = "light" | "dark" | "system";
const NAV = [
  { id: "today", label: "Overview", Icon: Home },
  { id: "chats", label: "Chat", Icon: MessageSquare },
  { id: "knowledge", label: "Knowledge", Icon: BookOpen },
  { id: "mind", label: "Memory", Icon: Brain },
  { id: "projects", label: "Projects", Icon: FolderOpen },
  { id: "tasks", label: "Tasks", Icon: CheckSquare },
  { id: "media", label: "Media", Icon: Film },
] as const;
const EMBED_MODEL = "nomic-embed-text";
interface PendingCloud {
  text: string; conversationId: string; turnId?: string; selection: SendSelection;
  provider: CloudProvider; request: Record<string, unknown>; version: number;
}

export default function App() {
  const mini = useMiniWindow();
  const { active: miniActive, setCompact } = mini;
  const miniSidebar = useRef(true);
  const [setupComplete, setSetupComplete] = useState(false);
  const finishSetup = useCallback(() => setSetupComplete(true), []);
  const [modelSetupOpen, setModelSetupOpen] = useState(false);
  const closeModelSetup = useCallback(() => setModelSetupOpen(false), []);
  const [sidebarOpen, setSidebarOpen] = useState(() => window.innerWidth > 760);
  const [narrow, setNarrow] = useState(() => window.innerWidth <= 760);
  const sidebarRef = useRef<HTMLElement>(null);
  const sidebarToggleRef = useRef<HTMLButtonElement>(null);
  const [activePage, setActivePage] = useState<PageId>("chats");
  const [modelMode, setModelMode] = useState<ModelMode>("balanced");
  const [providerId, setProviderId] = useState<ProviderId>("ollama");
  const providers = useProviders();
  const cloud = providers.status?.providers.find(provider => provider.id === providerId);
  const [pendingCloud, setPendingCloud] = useState<PendingCloud | null>(null);
  const [speedModel, setSpeedModel] = useState(
    () => localStorage.getItem("lm-speed-model") || MODEL_MAP.speed,
  );
  const [balancedModel, setBalancedModel] = useState(
    () => localStorage.getItem("lm-balanced-model") || MODEL_MAP.balanced,
  );
  const [memoryEnabled, setMemoryEnabled] = useState(true);
  const [knowledgeEnabled, setKnowledgeEnabled] = useState(false);
  const [hydeEnabled, setHydeEnabled] = useState(false);
  const [webSearchEnabled, setWebSearchEnabled] = useState(false);
  const [theme, setTheme] = useState<Theme>(() => {
    const saved = localStorage.getItem("lm-theme");
    return saved === "dark" || saved === "light" ? saved : "system";
  });
  const [notice, setNotice] = useState<string | null>(null);
  const [loadingConversation, setLoadingConversation] = useState(false);
  const [chatLoadError, setChatLoadError] = useState(false);
  const [pendingSend, setPendingSend] = useState(false);
  const [followUps, setFollowUps] = useState<string[]>([]);
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const navigationVersion = useRef(0);
  const changingConversation = useRef(false);
  const sending = useRef(false);
  const composerRef = useRef<HTMLTextAreaElement>(null);
  const ollama = useOllama();
  const runtime = useSidecarStatus();
  const {
    memories,
    links,
    deleteMemory,
    deleteLink,
    loading: memoryLoading,
    error: memoryError,
    refresh: refreshMemory,
  } = useMemory();
  const {
    conversations,
    activeId,
    createConversation,
    selectConversation,
    saveCurrentTurn,
    deleteConversation,
    renameConversation,
    updateConversationProject,
    clearSelection,
  } = useConversations();
  const { projects, assignConversation } = useProjects();
  const chatProjectId = conversations.find((c) => c.id === activeId)?.project_id ?? null;
  const draftKey = activeId || "new";
  const attachments = useAttachments(draftKey);
  useEffect(() => {
    const expand = () => { if (miniActive) void setCompact(false); };
    window.addEventListener("localmind-visual-open", expand);
    return () => window.removeEventListener("localmind-visual-open", expand);
  }, [miniActive, setCompact]);

  useEffect(() => {
    const followup = (event: Event) => {
      const text = (event as CustomEvent<unknown>).detail;
      if (typeof text === "string" && text.length < 110_000) setDrafts(previous => ({ ...previous, [draftKey]: text + (previous[draftKey] || "") }));
    };
    window.addEventListener("localmind-visual-followup", followup);
    return () => window.removeEventListener("localmind-visual-followup", followup);
  }, [draftKey]);


  useEffect(() => {
    localStorage.setItem("lm-speed-model", speedModel);
  }, [speedModel]);
  useEffect(() => {
    localStorage.setItem("lm-balanced-model", balancedModel);
  }, [balancedModel]);
  useEffect(() => {
    const media = window.matchMedia("(max-width: 760px)");
    const resize = () => {
      setNarrow(media.matches);
      if (media.matches) setSidebarOpen(false);
    };
    media.addEventListener("change", resize);
    return () => media.removeEventListener("change", resize);
  }, []);
  useEffect(() => {
    if (!sidebarOpen || !narrow || miniActive) return;
    const previous = document.activeElement;
    const buttons = () =>
      Array.from(
        sidebarRef.current?.querySelectorAll<HTMLElement>(
          "button:not(:disabled), input, select, [href]",
        ) || [],
      );
    buttons()[0]?.focus();
    const trap = (event: KeyboardEvent) => {
      if (event.key !== "Tab") return;
      const targets = buttons();
      const first = targets[0],
        last = targets[targets.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last?.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first?.focus();
      }
    };
    document.addEventListener("keydown", trap);
    return () => {
      document.removeEventListener("keydown", trap);
      requestAnimationFrame(() => {
        if (previous instanceof HTMLElement && previous.isConnected)
          previous.focus();
        else
          document
            .querySelector<HTMLButtonElement>('[aria-label="Open sidebar"]')
            ?.focus();
      });
    };
  }, [sidebarOpen, narrow, miniActive]);
  useEffect(() => {
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const apply = () =>
      document.documentElement.classList.toggle(
        "light",
        theme === "light" || (theme === "system" && !media.matches),
      );
    apply();
    media.addEventListener("change", apply);
    localStorage.setItem("lm-theme", theme);
    return () => media.removeEventListener("change", apply);
  }, [theme]);

  const handleComplete = useCallback(
    async (
      user: string,
      answer: string,
      thinking: string | null,
      conversationId: string | null,
      sources: WebSource[],
      attachmentIds: string[] = [],
    ) => {
      const version = navigationVersion.current;
      try {
        await saveCurrentTurn(user, answer, thinking, conversationId, sources, attachmentIds);
      } catch {
        setNotice(
          "This reply could not be saved. Copy it before leaving this chat.",
        );
        throw new Error("Conversation save failed");
      }
      void (async () => { try {
        const questions = await invoke<string[]>("get_followup_questions", {
          lastUser: user,
          lastAssistant: answer.slice(0, 1200),
          model: speedModel,
        });
        if (version === navigationVersion.current)
          setFollowUps(Array.isArray(questions) ? questions : []);
      } catch {
        /* Suggestions are optional; the saved answer remains usable. */
      } })();
    },
    [saveCurrentTurn, speedModel],
  );
  const {
    messages,
    isStreaming,
    isSearching,
    sendMessage,
    stopStreaming,
    loadMessages,
    clearMessages,
    waitForPersistence,
  } = useChat({
    modelMode,
    speedModel,
    balancedModel,
    memoryEnabled,
    knowledgeEnabled,
    hydeEnabled,
    webSearchEnabled,
    embedModel: EMBED_MODEL,
    projectId: chatProjectId,
    onTurnComplete: handleComplete,
    onTurnFailure: (text, conversationId, selection) => {
      setDrafts(previous => ({ ...previous, [conversationId || "new"]: previous[conversationId || "new"] || text }));
      if (selection?.attachments?.length && selection.attachmentDraftId) attachments.restore(conversationId || "new", selection.attachmentDraftId, selection.attachments);
    },
  });
  const [voiceScope, setVoiceScope] = useState(0);
  const draft = drafts[draftKey] || "";
  const setDraft = useCallback(
    (value: string) => setDrafts((prev) => ({ ...prev, [draftKey]: value })),
    [draftKey],
  );
  const localModel = modelMode === "speed" ? speedModel : balancedModel;
  const selectedModel = providerId === "ollama" ? localModel : cloud?.model || "Choose a cloud model";
  const modelCheck = useModelReadiness(selectedModel, modelMode,
    runtime.status === "ready" && setupComplete && !modelSetupOpen && providerId === "ollama");
  const modelAvailable = !!ollama?.models.some(
    (m) => m.name === selectedModel || m.name === `${selectedModel}:latest`,
  );
  const ready = runtime.status === "ready" && (providerId === "ollama"
    ? !!ollama?.running && modelAvailable && modelCheck.ready
    : !!cloud?.connected && !!cloud.model && providers.status?.local_only === false);
  const imageInput = providerId === "ollama" ? modelCheck.result?.vision : cloud?.vision;

  const navigate = useCallback((page: PageId) => {
    const show = () => {
      setActivePage(page);
      if (window.innerWidth <= 760) setSidebarOpen(false);
    };
    if (miniActive && page !== "chats") void setCompact(false).then((ok) => {
      if (ok) {
        setSidebarOpen(window.innerWidth > 760 && miniSidebar.current);
        show();
      }
    });
    else show();
  }, [miniActive, setCompact]);
  useEffect(() => {
    const attach = (event: Event) => {
      const file = (event as CustomEvent<unknown>).detail;
      if (file instanceof File && file.type === "image/png" && file.size <= 8*1024*1024) { attachments.add([file]); navigate("chats"); }
    };
    window.addEventListener("localmind-generated-image", attach);
    return () => window.removeEventListener("localmind-generated-image", attach);
  }, [attachments, navigate]);
  const newChat = useCallback(() => {
    setPendingCloud(null);
    setVoiceScope(value => value + 1);
    navigationVersion.current++;
    changingConversation.current = false;
    setLoadingConversation(false);
    setChatLoadError(false);
    clearMessages();
    clearSelection();
    setFollowUps([]);
    setNotice(null);
    navigate("chats");
    requestAnimationFrame(() => composerRef.current?.focus());
  }, [clearMessages, clearSelection, navigate]);
  const selectChat = useCallback(
    async (id: string) => {
      setPendingCloud(null);
      setVoiceScope(value => value + 1);
      const version = ++navigationVersion.current;
      changingConversation.current = true;
      setLoadingConversation(true);
      setChatLoadError(false);
      clearMessages();
      setFollowUps([]);
      setNotice(null);
      navigate("chats");
      try {
        await waitForPersistence();
        if (version !== navigationVersion.current) return;
        const rows = await selectConversation(id);
        if (version === navigationVersion.current)
          loadMessages(
            conversationMessages(rows),
          );
      } catch {
        if (version === navigationVersion.current) setChatLoadError(true);
      } finally {
        if (version === navigationVersion.current) {
          changingConversation.current = false;
          setLoadingConversation(false);
        }
      }
    },
    [clearMessages, loadMessages, selectConversation, navigate, waitForPersistence],
  );
  const dispatchTurn = useCallback((turn: Pick<PendingCloud, "text" | "conversationId" | "turnId" | "selection">) => {
    setDrafts(previous => ({ ...previous, [turn.conversationId]: previous[turn.conversationId]?.trim() === turn.text.trim() ? "" : previous[turn.conversationId] || "" }));
    attachments.release(turn.conversationId, turn.selection.attachments?.map(item => item.id) || []);
    void sendMessage(turn.text, turn.conversationId, turn.turnId, turn.selection);
  }, [attachments, sendMessage]);
  const send = useCallback(
    async (text: string, voiceTurnId?: string, retrySelection?: SendSelection): Promise<boolean> => {
      if (
        !text.trim() ||
        changingConversation.current ||
        chatLoadError ||
        sending.current ||
        isStreaming ||
        !ready || pendingCloud || attachments.busy || !attachments.valid || (attachments.items.some(item => item.mime_type?.startsWith("image/")) && imageInput === false)
      )
        return false;
      sending.current = true;
      setPendingSend(true);
      const version = ++navigationVersion.current;
      setNotice(null);
      setFollowUps([]);
      const selection: SendSelection = retrySelection || {
        provider: providerId, model: selectedModel, consentId: cloud?.consent_id,
        attachments: attachments.attachments, attachmentDraftId: attachments.draftId,
      };
      try {
        const id =
          activeId || (await createConversation(text.trim().slice(0, 60))).id;
        if (version !== navigationVersion.current) return false;
        if (!activeId) {
          attachments.move(draftKey, id);
          setDrafts(previous => ({ ...previous, [draftKey]: "", [id]: previous[draftKey] || text }));
        }
        const turn = { text, conversationId: id, turnId: voiceTurnId, selection };
        if (providerId !== "ollama" && cloud) {
          const scopes: CloudScope[] = ["messages"];
          if (memoryEnabled) scopes.push("memory");
          if (knowledgeEnabled || chatProjectId) scopes.push("knowledge");
          if (webSearchEnabled) scopes.push("web");
          if (selection.attachments?.length || messages.some(message => message.attachments?.length)) scopes.push("attachments");
          if (!cloud.consent_id || cloud.consent_model !== cloud.model || scopes.some(scope => !cloud.approved_scopes.includes(scope))) {
            setPendingCloud({ ...turn, provider: cloud, version, request: {
              message: text, model: selectedModel, provider: providerId, conversation_id: id,
              project_id: chatProjectId, history: messages.map(message => ({ role: message.role, content: message.content })),
              memory_enabled: memoryEnabled, knowledge_enabled: knowledgeEnabled,
              web_search_enabled: webSearchEnabled, connector_context_enabled: false,
              embed_model: EMBED_MODEL, profile: modelMode, hyde_enabled: hydeEnabled,
              attachment_ids: selection.attachments?.map(item => item.id) || [],
              attachment_draft_id: selection.attachmentDraftId,
            } });
            return false;
          }
        }
        dispatchTurn(turn);
        return true;
      } catch {
        setNotice(
          "Couldn't start a saved chat. Your draft is still here; try again.",
        );
        return false;
      } finally {
        sending.current = false;
        setPendingSend(false);
      }
    },
    [
      activeId,
      chatLoadError,
      createConversation,
      draftKey,
      isStreaming,
      ready,
      pendingCloud, attachments, imageInput, providerId, selectedModel, cloud,
      memoryEnabled, knowledgeEnabled, webSearchEnabled, messages, chatProjectId,
      modelMode, hydeEnabled, dispatchTurn,
    ],
  );

  const toggleMini = useCallback(async () => {
    if (!miniActive) miniSidebar.current = sidebarOpen;
    const compact = !miniActive;
    if (await setCompact(compact)) {
      if (compact) { navigate("chats"); setSidebarOpen(false); }
      else setSidebarOpen(window.innerWidth > 760 && miniSidebar.current);
      requestAnimationFrame(() => composerRef.current?.focus());
    }
  }, [miniActive, setCompact, sidebarOpen, navigate]);


  useEffect(() => {
    const shortcuts = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === "m") {
        event.preventDefault();
        void toggleMini();
        return;
      }
      if (
        (event.ctrlKey || event.metaKey) &&
        event.shiftKey &&
        event.key.toLowerCase() === "o"
      ) {
        event.preventDefault();
        newChat();
      }
      if (event.key === "Escape" && window.innerWidth <= 760)
        setSidebarOpen(false);
    };
    window.addEventListener("keydown", shortcuts);
    return () => window.removeEventListener("keydown", shortcuts);
  }, [newChat, toggleMini]);

  const title =
    activePage === "chats"
      ? conversations.find((c) => c.id === activeId)?.title || "New chat"
      : NAV.find((n) => n.id === activePage)?.label ||
        (
          {
            connections: "Connections",
            settings: "Settings",
            buddy: "Voice",
          } as Partial<Record<PageId, string>>
        )[activePage];
  const openPrompt = (prompt?: string) => {
    navigate("chats");
    if (prompt) setDraft(prompt);
    requestAnimationFrame(() => composerRef.current?.focus());
  };
  const last = messages[messages.length - 1];
  const retry = () => {
    const user = [...messages].reverse().find((m) => m.role === "user");
    if (!user || !ready || isStreaming) return;
    // Replace the displayed interrupted pair; its recovery record stays separate
    // from completed conversation history.
    loadMessages(messages.slice(0, -2));
    void send(user.content, undefined, { provider: providerId, model: selectedModel, consentId: cloud?.consent_id, attachments: user.attachments, attachmentDraftId: user.attachmentDraftId });
  };

  return (
    <>
      {pendingCloud && <CloudConsent provider={pendingCloud.provider} request={pendingCloud.request}
        onCancel={() => setPendingCloud(null)} onApprove={consentId => {
          if (pendingCloud.version === navigationVersion.current) dispatchTurn({ ...pendingCloud, selection: { ...pendingCloud.selection, consentId } });
          setPendingCloud(null); void providers.refresh();
        }} />}
      {!setupComplete && <FirstRunSetup runtimeStatus={runtime.status} onRestartService={() => void runtime.restart()} chatModel={selectedModel} preset={modelMode} onChooseChatModel={modelMode === "speed" ? setSpeedModel : setBalancedModel} onComplete={finishSetup} />}
      {setupComplete && modelSetupOpen && (
        <FirstRunSetup runtimeStatus={runtime.status} onRestartService={() => void runtime.restart()} chatModel={selectedModel} preset={modelMode} onChooseChatModel={modelMode === "speed" ? setSpeedModel : setBalancedModel} forceShow onComplete={closeModelSetup} />
      )}
      <div
        className={`app-shell ${miniActive ? "mini-mode" : ""}`}
        inert={!setupComplete || modelSetupOpen}
        style={{ visibility: setupComplete ? "visible" : "hidden" }}
      >
        <a className="skip-link" href="#main-content">
          Skip to main content
        </a>
        {sidebarOpen && (
          <button
            className="sidebar-scrim"
            aria-label="Close navigation"
            onClick={() => setSidebarOpen(false)}
          />
        )}
        <aside
          ref={sidebarRef}
          className={`app-sidebar ${sidebarOpen ? "is-open" : "is-closed"}`}
          aria-label="Workspace navigation"
          inert={!sidebarOpen || miniActive}
        >
          <div className="sidebar-brand">
            <Brand />
            <span>LocalMind</span>
            <button
              className="icon-button"
              onClick={() => setSidebarOpen(false)}
              aria-label="Close sidebar"
            >
              <PanelLeft size={17} />
            </button>
          </div>
          <button className="new-chat-button" onClick={newChat}>
            <span className="new-chat-icon"><SquarePen size={17} /></span>
            New chat<kbd title="Ctrl+Shift+O">Ctrl ⇧ O</kbd>
          </button>
          <nav className="workspace-nav" aria-label="Workspace">
            {NAV.map(({ id, label, Icon }) => (
              <button
                key={id}
                aria-current={activePage === id ? "page" : undefined}
                onClick={() => navigate(id)}
              >
                <Icon size={17} strokeWidth={1.6} />
                {label}
              </button>
            ))}
          </nav>
          <ConversationList
            conversations={conversations}
            activeId={activePage === "chats" ? activeId : null}
            onSelect={selectChat}
            onDelete={async (id) => {
              const version = navigationVersion.current;
              await deleteConversation(id);
              if (id === activeId && version === navigationVersion.current)
                newChat();
            }}
            onRename={renameConversation}
          />
          <div className="sidebar-footer">
            <nav aria-label="Application">
              <button
                aria-current={activePage === "connections" ? "page" : undefined}
                onClick={() => navigate("connections")}
              >
                <Plug size={16} />
                Connections
              </button>
              <button
                aria-current={activePage === "buddy" ? "page" : undefined}
                onClick={() => navigate("buddy")}
              >
                <AudioLines size={16} />
                Voice<span className="muted-tag">Pilot</span>
              </button>
              <button
                aria-current={activePage === "settings" ? "page" : undefined}
                onClick={() => navigate("settings")}
              >
                <Settings size={16} />
                Settings
              </button>
            </nav>
            <div className="sidebar-bottom">
              <button
                className="runtime-indicator"
                onClick={() => navigate("settings")}
              >
                <span className={`status-dot ${ready ? "ready" : ""}`} />
                {ready
                  ? "Running locally"
                  : runtime.status === "failed"
                    ? "Needs attention"
                    : "Checking setup"}
              </button>
              <select
                aria-label="Color theme"
                value={theme}
                onChange={(e) => setTheme(e.target.value as Theme)}
              >
                <option value="system">System</option>
                <option value="light">Light</option>
                <option value="dark">Dark</option>
              </select>
            </div>
          </div>
        </aside>
        <main
          id="main-content"
          className="app-main"
          tabIndex={-1}
          inert={narrow && sidebarOpen && !miniActive}
        >
          <header className={`app-header ${activePage === "chats" ? "chat-header" : ""}`}>
            {!sidebarOpen && !miniActive && (
              <button
                ref={sidebarToggleRef}
                className="icon-button"
                aria-label="Open sidebar"
                onClick={() => setSidebarOpen(true)}
              >
                <PanelLeft size={19} />
              </button>
            )}
            <div className="page-identity">
              <span>{activePage === "chats" ? "Chat" : "Workspace"}</span>
              <ChevronRight size={13} />
              <h1 title={title}>{title}</h1>
            </div>
            <div className="header-actions">
              {miniActive ? <div className="mini-window-controls">
                <button className="icon-button" disabled={mini.busy} aria-label="Stay on top" aria-pressed={mini.pinned}
                  title="Stay on top" onClick={() => void mini.setPinned(!mini.pinned)}><Pin size={16} /></button>
                <button className="icon-button" disabled={mini.busy} aria-label="Minimize mini chat" title="Minimize"
                  onClick={() => void mini.minimize()}><Minus size={16} /></button>
                <button className="icon-button" disabled={mini.busy} aria-label="Expand chat" title="Expand chat"
                  onClick={() => void toggleMini()}><Maximize2 size={16} /></button>
              </div> : <button className="mini-launch" disabled={mini.busy} onClick={() => void toggleMini()}
                title="Open mini chat (Ctrl/Cmd + Shift + M)"><PictureInPicture2 size={16} />Mini chat</button>}

              {!miniActive && activePage === "chats" && activeId && projects.length > 0 && (
                <select
                  aria-label="Move chat to project"
                  value={chatProjectId ?? ""}
                  disabled={isStreaming || pendingSend}
                  onChange={async (e) => {
                    const id = e.target.value;
                    try {
                      await assignConversation(activeId, id || null);
                      updateConversationProject(activeId, id || null);
                      setNotice(
                        id ? `Chat added to ${projects.find((p) => p.id === id)?.name || "project"}.`
                          : "Chat moved out of the project.",
                      );
                    } catch {
                      setNotice(
                        "Couldn't add this chat to the project. Try again.",
                      );
                    }
                  }}
                >
                  <option value="">
                    No project
                  </option>
                  {projects.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name}
                    </option>
                  ))}
                </select>
              )}
              <span className="local-label">
                <ShieldCheck size={14} />
                {activePage === "media"
                  ? "Local + optional cloud"
                  : activePage === "chats"
                    ? (providerId !== "ollama" ? `Cloud · ${cloud?.label || providerId}` : webSearchEnabled ? "Web search enabled" : "Local model")
                    : "Local workspace"}
              </span>
            </div>
          </header>
          {setupComplete && <UpdateBanner />}
          {mini.error && <div className="app-notice" role="alert">{mini.error}</div>}
          {notice && (
            <div className="app-notice" role="status">
              <span>{notice}</span>
              <button
                className="icon-button"
                onClick={() => setNotice(null)}
                aria-label="Dismiss notification"
              >
                <X size={16} />
              </button>
            </div>
          )}
          {activePage === "chats" ? (
            <div
              className={`chat-main ${messages.length === 0 ? "empty-chat" : ""}`}
            >
              {loadingConversation ? (
                <div className="chat-loading" role="status">
                  Opening conversation…
                </div>
              ) : chatLoadError ? (
                <div className="chat-loading" role="alert">
                  <div>
                    <p>Could not load this conversation.</p>
                    <button
                      className="secondary-button"
                      onClick={() => activeId && void selectChat(activeId)}
                    >
                      Try loading again
                    </button>
                  </div>
                </div>
              ) : messages.length > 0 ? (
                <MessageList
                  key={`messages-${activeId || "new"}`}
                  messages={messages}
                  conversationId={activeId || undefined}
                  isStreaming={isStreaming}
                  isSearching={isSearching}
                  followUpQuestions={followUps}
                  onSendFollowUp={(q) => void send(q)}
                  onRegenerate={
                    last?.error || last?.cancelled ? retry : undefined
                  }
                />
              ) : (
                <div className="chat-welcome">
                  <div className="welcome-identity"><Brand size={24} /><span>Your local workspace</span></div>
                  <h2>What’s on your mind?</h2>
                  <p>Ask a question or explore an idea.</p>
                </div>
              )}
              {!miniActive && activeId && !loadingConversation && !chatLoadError && <TaskActions key={`tasks-${activeId}`} conversationId={activeId} projectId={chatProjectId} isStreaming={isStreaming} onOpenTasks={() => navigate("tasks")} />}
              {(providerId === "ollama" || runtime.status !== "ready") && <ChatReadiness
                runtimeStatus={runtime.status}
                runtimeError={runtime.error}
                ollamaRunning={!!ollama?.running}
                modelAvailable={modelAvailable}
                model={selectedModel}
                checking={modelCheck.checking}
                result={modelCheck.result}
                onRestart={() => void runtime.restart()}
                onRetry={modelCheck.retry}
                onSettings={() => navigate("settings")}
                onDownloads={() => setModelSetupOpen(true)}
              />}
              {providerId !== "ollama" && !ready && runtime.status === "ready" && <div className="app-notice" role="status">{providers.status?.local_only ? "Cloud chat is blocked by your local-only setting." : "Connect this provider and select a model in Settings."}<button className="secondary-button" onClick={() => navigate("settings")}>Open settings</button></div>}
              <Composer
                messages={messages}
                voiceScope={`${voiceScope}:${chatProjectId || "unassigned"}`}
                textareaRef={composerRef}
                value={draft}
                onChange={setDraft}
                onSend={send}
                onStop={stopStreaming}
                isStreaming={isStreaming}
                disabled={
                  !ready || loadingConversation || chatLoadError || pendingSend || !!pendingCloud
                }
                memoryEnabled={memoryEnabled}
                onMemoryToggle={() => setMemoryEnabled((v) => !v)}
                knowledgeEnabled={knowledgeEnabled}
                onKnowledgeToggle={() => setKnowledgeEnabled((v) => !v)}
                webSearchEnabled={webSearchEnabled}
                onWebSearchToggle={() => setWebSearchEnabled((v) => !v)}
                modelMode={modelMode}
                onModelModeChange={setModelMode}
                modelName={selectedModel}
                modelNames={{ speed: speedModel, balanced: balancedModel }}
                onOpenKnowledge={() => navigate("knowledge")}
                provider={providerId} providers={providers.status?.providers} localOnly={providers.status?.local_only ?? true}
                onProviderChange={value => { setPendingCloud(null); setProviderId(value); }}
                onOpenProviderSettings={() => navigate("settings")}
                attachments={attachments.items} onAddAttachments={attachments.add}
                onRemoveAttachment={attachments.remove} onRetryAttachment={attachments.retry}
                attachmentMode={attachments.mode} onAttachmentModeChange={attachments.setMode} attachmentBusy={attachments.busy} attachmentValid={attachments.valid} imageInput={imageInput}
                attachmentNotice={attachments.notice}
                installedModels={ollama?.models.map(model => model.name)}
                onLocalModelChange={modelMode === "speed" ? setSpeedModel : setBalancedModel}
              />
              {messages.length === 0 && !loadingConversation && !chatLoadError && !miniActive && <ChatStarters onChoose={openPrompt} />}
            </div>
          ) : activePage === "today" ? (
            <TodayPage
              onNavigateToChat={openPrompt}
              onNavigateToConnections={() => navigate("connections")}
            />
          ) : activePage === "mind" ? (
            <MemoryPage
              onOpenConversation={id => void selectChat(id)}
              memories={memories ?? []}
              links={links ?? []}
              onDelete={deleteMemory}
              onDeleteLink={deleteLink}
              loading={memoryLoading}
              loadError={memoryError}
              onRefresh={() => void refreshMemory()}
            />
          ) : activePage === "knowledge" ? (
            <KnowledgePage embedModel={EMBED_MODEL} />
          ) : activePage === "projects" ? (
            <ProjectsPage speedModel={speedModel} />
          ) : activePage === "tasks" ? (
            <TasksPage projects={projects} />
          ) : activePage === "media" ? (
            <MediaPage />
          ) : activePage === "connections" ? (
            <ConnectionsPage />
          ) : activePage === "buddy" ? (
            <BuddyPage onNavigateToChat={() => navigate("chats")} />
          ) : (
            <SettingsPage
              onManageModels={() => setModelSetupOpen(true)}
              ollamaStatus={ollama}
              speedModel={speedModel}
              balancedModel={balancedModel}
              onSpeedModelChange={setSpeedModel}
              onBalancedModelChange={setBalancedModel}
              hydeEnabled={hydeEnabled}
              onHydeToggle={() => setHydeEnabled((v) => !v)}
            />
          )}
        </main>
      </div>
    </>
  );
}
