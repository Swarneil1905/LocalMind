// Spec reference: Section 15 (Settings Screen)
// Phase 1: Local Model + GPU sections active.
// Phase 5: Web Search section active.

import { useState } from "react";
import { OllamaStatus } from "../hooks/useOllama";

interface SettingsPageProps {
  ollamaStatus: OllamaStatus | null;
  speedModel: string;
  balancedModel: string;
  onSpeedModelChange: (model: string) => void;
  onBalancedModelChange: (model: string) => void;
}

type SettingsSection =
  | "local-model"
  | "gpu"
  | "web-search"
  | "cloud-boost"
  | "memory"
  | "privacy"
  | "updates"
  | "shortcuts";

const SECTIONS: { id: SettingsSection; label: string; phase1: boolean }[] = [
  { id: "local-model",  label: "Local Model",       phase1: true  },
  { id: "gpu",          label: "GPU",                phase1: true  },
  { id: "web-search",   label: "Web Search",         phase1: false },
  { id: "cloud-boost",  label: "Cloud Boost",        phase1: false },
  { id: "memory",       label: "Memory",             phase1: false },
  { id: "privacy",      label: "Privacy",            phase1: false },
  { id: "updates",      label: "Updates",            phase1: false },
  { id: "shortcuts",    label: "Keyboard Shortcuts", phase1: false },
];

// Curated open models verified to run on 6 GB VRAM.
const RECOMMENDED_MODELS = [
  {
    name: "maternion/lfm2.5",
    label: "LFM2.5-8B-A1B",
    by: "Liquid AI",
    slot: "speed" as const,
    vram: "< 6 GB",
    ctx: "128k",
    note: "1.5B active params, MoE. Best speed model for 6 GB GPUs.",
    pull: "ollama pull maternion/lfm2.5",
  },
  {
    name: "phi4-mini:3.8b",
    label: "Phi-4-Mini 3.8B",
    by: "Microsoft",
    slot: "speed" as const,
    vram: "~3 GB",
    ctx: "16k",
    note: "Dense 3.8B model. Strong for its size. Good speed fallback.",
    pull: "ollama pull phi4-mini:3.8b",
  },
  {
    name: "deepseek-r1:7b",
    label: "DeepSeek-R1 7B",
    by: "DeepSeek",
    slot: "balanced" as const,
    vram: "~5 GB",
    ctx: "32k",
    note: "Reasoning model with chain-of-thought. Current default.",
    pull: "ollama pull deepseek-r1:7b",
  },
  {
    name: "qwen3:8b",
    label: "Qwen3 8B",
    by: "Alibaba",
    slot: "balanced" as const,
    vram: "~5-6 GB",
    ctx: "32k",
    note: "Strong reasoning + coding. 40+ tok/s on 6 GB GPU. Recommended alternative.",
    pull: "ollama pull qwen3:8b",
  },
  {
    name: "mellum2:12b-a2.5b-thinking-q4_k_m",
    label: "Mellum2-12B-A2.5B-Thinking",
    by: "JetBrains",
    slot: "balanced" as const,
    vram: "~4 GB",
    ctx: "32k",
    note: "2.5B active params, coding-focused MoE. Chain-of-thought. Apache 2.0.",
    pull: "ollama pull mellum2:12b-a2.5b-thinking-q4_k_m",
  },
] as const;

export function SettingsPage({
  ollamaStatus,
  speedModel,
  balancedModel,
  onSpeedModelChange,
  onBalancedModelChange,
}: SettingsPageProps) {
  const [activeSection, setActiveSection] = useState<SettingsSection>("local-model");
  const [speedContext, setSpeedContext] = useState(2048);
  const [balancedContext, setBalancedContext] = useState(4096);

  return (
    <div style={{ display: "flex", flex: 1, overflow: "hidden" }}>
      <nav
        style={{
          width: 180,
          minWidth: 180,
          borderRight: "1px solid var(--border)",
          padding: "12px 0",
          overflowY: "auto",
          flexShrink: 0,
        }}
      >
        {SECTIONS.map(({ id, label, phase1 }) => (
          <button
            key={id}
            onClick={() => phase1 && setActiveSection(id)}
            style={{
              width: "100%",
              height: 34,
              display: "flex",
              alignItems: "center",
              padding: "0 16px",
              fontSize: 13,
              textAlign: "left",
              color: !phase1 ? "var(--text-3)" : activeSection === id ? "var(--text)" : "var(--text-2)",
              backgroundColor: activeSection === id && phase1 ? "var(--surface-2)" : "transparent",
              borderLeft: `2px solid ${activeSection === id && phase1 ? "var(--accent)" : "transparent"}`,
              cursor: phase1 ? "pointer" : "default",
              gap: 8,
            }}
          >
            {label}
            {!phase1 && (
              <span style={{ fontSize: 10, color: "var(--text-3)", marginLeft: "auto" }}>later</span>
            )}
          </button>
        ))}
      </nav>

      <div style={{ flex: 1, overflowY: "auto", padding: "24px 32px" }}>
        {activeSection === "local-model" && (
          <LocalModelSection
            ollamaStatus={ollamaStatus}
            speedModel={speedModel}
            balancedModel={balancedModel}
            speedContext={speedContext}
            balancedContext={balancedContext}
            onSpeedModelChange={onSpeedModelChange}
            onBalancedModelChange={onBalancedModelChange}
            onSpeedContextChange={setSpeedContext}
            onBalancedContextChange={setBalancedContext}
          />
        )}
        {activeSection === "gpu" && <GpuSection ollamaStatus={ollamaStatus} />}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Local Model section
// ---------------------------------------------------------------------------

interface LocalModelSectionProps {
  ollamaStatus: OllamaStatus | null;
  speedModel: string;
  balancedModel: string;
  speedContext: number;
  balancedContext: number;
  onSpeedModelChange: (m: string) => void;
  onBalancedModelChange: (m: string) => void;
  onSpeedContextChange: (n: number) => void;
  onBalancedContextChange: (n: number) => void;
}

function LocalModelSection({
  ollamaStatus,
  speedModel,
  balancedModel,
  speedContext,
  balancedContext,
  onSpeedModelChange,
  onBalancedModelChange,
  onSpeedContextChange,
  onBalancedContextChange,
}: LocalModelSectionProps) {
  const running = ollamaStatus?.running ?? false;
  const models = ollamaStatus?.models ?? [];
  const modelNames = models.map((m) => m.name);
  const [copied, setCopied] = useState<string | null>(null);

  function copyPull(cmd: string) {
    navigator.clipboard.writeText(cmd).then(() => {
      setCopied(cmd);
      setTimeout(() => setCopied(null), 1800);
    });
  }

  return (
    <div style={{ maxWidth: 560 }}>
      <SectionHeading>Local Model</SectionHeading>

      <SettingRow label="Ollama status">
        <StatusBadge running={running} />
      </SettingRow>

      {running && ollamaStatus?.version && (
        <SettingRow label="Ollama version">
          <Value>{ollamaStatus.version}</Value>
        </SettingRow>
      )}

      <SettingRow label="Available models">
        {models.length === 0 ? (
          <Value dimmed>No models found</Value>
        ) : (
          <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
            {models.map((m) => (
              <div key={m.name} style={{ display: "flex", gap: 12, alignItems: "baseline" }}>
                <Value>{m.name}</Value>
                <Value dimmed>{formatBytes(m.size)}</Value>
              </div>
            ))}
          </div>
        )}
      </SettingRow>

      <Divider />

      <SettingRow label="Speed model" hint="Used for routing and background tasks (1B-2B)">
        <ModelSelect value={speedModel} options={modelNames} onChange={onSpeedModelChange} disabled={!running} />
      </SettingRow>

      <SettingRow label="Speed context length" hint={`${speedContext} tokens`}>
        <input
          type="range" min={512} max={8192} step={256} value={speedContext}
          onChange={(e) => onSpeedContextChange(Number(e.target.value))}
          style={{ width: 200, accentColor: "var(--accent)" }}
        />
      </SettingRow>

      <Divider />

      <SettingRow label="Balanced model" hint="Main chat model (3B-8B)">
        <ModelSelect value={balancedModel} options={modelNames} onChange={onBalancedModelChange} disabled={!running} />
      </SettingRow>

      <SettingRow label="Balanced context length" hint={`${balancedContext} tokens`}>
        <input
          type="range" min={512} max={8192} step={256} value={balancedContext}
          onChange={(e) => onBalancedContextChange(Number(e.target.value))}
          style={{ width: 200, accentColor: "var(--accent)" }}
        />
      </SettingRow>

      <Divider />

      {/* Recommended models */}
      <div style={{ marginBottom: 10 }}>
        <div style={{ fontSize: 13, fontWeight: 600, color: "var(--text)", marginBottom: 4 }}>
          Recommended models
        </div>
        <div style={{ fontSize: 12, color: "var(--text-3)", marginBottom: 12 }}>
          Verified on 6 GB VRAM. Copy the pull command, run it in a terminal, then click Use.
        </div>

        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          {RECOMMENDED_MODELS.map((m) => {
            const isPulled = modelNames.some((n) => n.startsWith(m.name.split(":")[0]));
            return (
              <div
                key={m.name}
                style={{
                  border: "1px solid var(--border)",
                  borderRadius: 6,
                  padding: "12px 14px",
                  backgroundColor: "var(--surface)",
                }}
              >
                <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 4 }}>
                  <span style={{ fontSize: 13, fontWeight: 600, color: "var(--text)" }}>{m.label}</span>
                  <span style={{ fontSize: 11, color: "var(--text-3)" }}>by {m.by}</span>
                  <span
                    style={{
                      marginLeft: "auto",
                      fontSize: 10,
                      fontWeight: 600,
                      padding: "2px 7px",
                      borderRadius: 3,
                      backgroundColor: m.slot === "speed" ? "var(--surface-2)" : "var(--accent)",
                      color: m.slot === "speed" ? "var(--text-2)" : "#fff",
                      textTransform: "uppercase",
                      letterSpacing: "0.05em",
                    }}
                  >
                    {m.slot}
                  </span>
                </div>

                <div style={{ display: "flex", gap: 16, marginBottom: 6 }}>
                  <span style={{ fontSize: 11, color: "var(--text-3)" }}>VRAM: {m.vram}</span>
                  <span style={{ fontSize: 11, color: "var(--text-3)" }}>Context: {m.ctx}</span>
                  {isPulled && (
                    <span style={{ fontSize: 11, color: "#22c55e", fontWeight: 600 }}>Pulled</span>
                  )}
                </div>

                <p style={{ fontSize: 12, color: "var(--text-2)", marginBottom: 10, lineHeight: 1.5 }}>
                  {m.note}
                </p>

                <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                  <code
                    style={{
                      flex: 1, fontSize: 11, fontFamily: "monospace",
                      backgroundColor: "var(--surface-2)", padding: "4px 8px",
                      borderRadius: 3, color: "var(--text-2)",
                      overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap",
                    }}
                  >
                    {m.pull}
                  </code>
                  <button
                    onClick={() => copyPull(m.pull)}
                    style={{
                      fontSize: 11, padding: "4px 10px", borderRadius: 3,
                      backgroundColor: "var(--surface-2)",
                      color: copied === m.pull ? "#22c55e" : "var(--text-2)",
                      border: "1px solid var(--border)", flexShrink: 0,
                    }}
                  >
                    {copied === m.pull ? "Copied!" : "Copy"}
                  </button>
                  <button
                    onClick={() =>
                      m.slot === "speed" ? onSpeedModelChange(m.name) : onBalancedModelChange(m.name)
                    }
                    disabled={!running}
                    style={{
                      fontSize: 11, padding: "4px 10px", borderRadius: 3,
                      backgroundColor: running ? "var(--accent)" : "var(--surface-2)",
                      color: running ? "#fff" : "var(--text-3)",
                      flexShrink: 0, opacity: running ? 1 : 0.6,
                    }}
                  >
                    Use
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// GPU section
// ---------------------------------------------------------------------------

function GpuSection({ ollamaStatus }: { ollamaStatus: OllamaStatus | null }) {
  const gpu = ollamaStatus?.gpu ?? null;
  const onGpu = !!gpu;

  return (
    <div style={{ maxWidth: 560 }}>
      <SectionHeading>GPU</SectionHeading>

      <SettingRow label="GPU detected">
        {gpu ? <Value>{gpu.name}</Value> : <Value dimmed>No GPU detected</Value>}
      </SettingRow>

      {gpu && (
        <>
          <SettingRow label="VRAM">
            <Value>{mibToGib(gpu.vramFreeMib)} GB free / {mibToGib(gpu.vramTotalMib)} GB total</Value>
          </SettingRow>
          <SettingRow label="VRAM used">
            <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
              <div style={{ width: 180, height: 6, backgroundColor: "var(--surface-2)", borderRadius: 3, overflow: "hidden" }}>
                <div
                  style={{
                    width: `${((gpu.vramTotalMib - gpu.vramFreeMib) / gpu.vramTotalMib) * 100}%`,
                    height: "100%", backgroundColor: "var(--accent)", borderRadius: 3, transition: "width 500ms",
                  }}
                />
              </div>
              <Value dimmed>
                {Math.round(((gpu.vramTotalMib - gpu.vramFreeMib) / gpu.vramTotalMib) * 100)}%
              </Value>
            </div>
          </SettingRow>
        </>
      )}

      <SettingRow label="Inference mode">
        <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
          <span style={{ width: 7, height: 7, borderRadius: "50%", backgroundColor: onGpu ? "#22c55e" : "#f59e0b", display: "block" }} />
          <Value>{onGpu ? "GPU" : "CPU (no GPU detected)"}</Value>
        </div>
      </SettingRow>

      {!onGpu && (
        <div style={{ marginTop: 16, padding: "10px 14px", backgroundColor: "var(--surface-2)", borderRadius: 5, border: "1px solid var(--border)", fontSize: 12, color: "var(--text-2)" }}>
          No NVIDIA GPU was detected. LocalMind will use the CPU for inference,
          which is significantly slower. Install Ollama and ensure CUDA drivers
          are up to date if you have a GPU.
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Shared components
// ---------------------------------------------------------------------------

function SectionHeading({ children }: { children: React.ReactNode }) {
  return (
    <h2 style={{ fontSize: 15, fontWeight: 600, color: "var(--text)", marginBottom: 20, paddingBottom: 10, borderBottom: "1px solid var(--border)" }}>
      {children}
    </h2>
  );
}

function Divider() {
  return <div style={{ height: 1, backgroundColor: "var(--border-subtle)", margin: "16px 0" }} />;
}

function SettingRow({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <div style={{ display: "flex", alignItems: "flex-start", gap: 24, marginBottom: 14 }}>
      <div style={{ width: 180, flexShrink: 0 }}>
        <div style={{ fontSize: 13, color: "var(--text)", lineHeight: 1.4 }}>{label}</div>
        {hint && <div style={{ fontSize: 11, color: "var(--text-3)", marginTop: 2 }}>{hint}</div>}
      </div>
      <div style={{ flex: 1, display: "flex", alignItems: "center" }}>{children}</div>
    </div>
  );
}

function Value({ children, dimmed }: { children: React.ReactNode; dimmed?: boolean }) {
  return <span style={{ fontSize: 13, color: dimmed ? "var(--text-3)" : "var(--text)" }}>{children}</span>;
}

function StatusBadge({ running }: { running: boolean }) {
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 7 }}>
      <span style={{ width: 7, height: 7, borderRadius: "50%", backgroundColor: running ? "#22c55e" : "#ef4444", display: "block" }} />
      <Value>{running ? "Running" : "Not found"}</Value>
    </div>
  );
}

function ModelSelect({ value, options, onChange, disabled }: { value: string; options: string[]; onChange: (v: string) => void; disabled: boolean }) {
  const allOptions = options.includes(value) ? options : [value, ...options];
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      disabled={disabled}
      style={{ fontSize: 13, color: "var(--text)", backgroundColor: "var(--surface-2)", border: "1px solid var(--border)", borderRadius: 4, padding: "4px 8px", cursor: disabled ? "not-allowed" : "pointer", minWidth: 180 }}
    >
      {allOptions.map((name) => <option key={name} value={name}>{name}</option>)}
    </select>
  );
}

function formatBytes(bytes: number): string {
  const gb = bytes / 1024 / 1024 / 1024;
  if (gb >= 1) return `${gb.toFixed(1)} GB`;
  return `${Math.round(bytes / 1024 / 1024)} MB`;
}

function mibToGib(mib: number): string {
  return (mib / 1024).toFixed(1);
}
