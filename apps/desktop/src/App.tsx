// Spec reference: Section 14 (Chat UI), Section 15 (All Application Screens)
// Phase 0: static shell only — no functionality, correct layout and design tokens.

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
// Constants — Section 14 (Left Navigation Sidebar)
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

// Right panel sections — Section 14 (Right Context Panel)
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
        onNavigate={setActivePage}
        onToggle={() => setSidebarCollapsed((c) => !c)}
      />
      <MainArea activePage={activePage} />
      <RightPanel />
    </div>
  );
}

// ---------------------------------------------------------------------------
// Sidebar
// ---------------------------------------------------------------------------

interface SidebarProps {
  collapsed: boolean;
  activePage: PageId;
  onNavigate: (page: PageId) => void;
  onToggle: () => void;
}

function Sidebar({ collapsed, activePage, onNavigate, onToggle }: SidebarProps) {
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
        // Section 16 (Animation): sidebar width transition is the only permitted sidebar animation
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
                // Section 14: active state has a left border accent
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

      {/* Footer: status indicators — Section 14 */}
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
        {/* Ollama health indicator */}
        <div style={{ display: "flex", alignItems: "center", gap: 7 }}>
          <span
            style={{
              width: 7,
              height: 7,
              borderRadius: "50%",
              backgroundColor: "var(--text-3)",
              flexShrink: 0,
              display: "block",
            }}
          />
          {!collapsed && (
            <span style={{ fontSize: 11, color: "var(--text-3)", whiteSpace: "nowrap" }}>
              Ollama: checking
            </span>
          )}
        </div>

        {/* Version */}
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

function MainArea({ activePage }: { activePage: PageId }) {
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
      {/* Chat header — Section 14 */}
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
          }}
        >
          {activePage}
        </span>
      </div>

      {/* Message area placeholder */}
      <div
        style={{
          flex: 1,
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          color: "var(--text-3)",
          fontSize: 13,
        }}
      >
        Select a conversation to begin
      </div>

      {/* Composer placeholder — Section 14 */}
      <div
        style={{
          borderTop: "1px solid var(--border)",
          padding: "12px 16px",
          flexShrink: 0,
        }}
      >
        <div
          style={{
            backgroundColor: "var(--surface)",
            border: "1px solid var(--border)",
            borderRadius: 6,
            padding: "10px 14px",
            fontSize: 13,
            color: "var(--text-3)",
            minHeight: 48,
            display: "flex",
            alignItems: "center",
          }}
        >
          Message LocalMind
        </div>
      </div>
    </main>
  );
}

// ---------------------------------------------------------------------------
// Right panel
// ---------------------------------------------------------------------------

function RightPanel() {
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
      {RIGHT_PANEL_SECTIONS.map((section) => (
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
          <div
            style={{
              marginTop: 8,
              fontSize: 12,
              color: "var(--text-3)",
            }}
          >
            No data
          </div>
        </div>
      ))}
    </aside>
  );
}
