/**
 * FirstRunSetup
 *
 * Shown the first time the user opens LocalMind if no Ollama models are
 * installed locally. Lets them pick models to download and shows live
 * progress bars while each model pulls.
 */

import { useState, useEffect, useCallback } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

// ---------------------------------------------------------------------------
// Model catalogue
// ---------------------------------------------------------------------------

interface ModelOption {
  /** Ollama pull name (passed to `ollama pull`) */
  name: string;
  /** Human-readable label */
  label: string;
  description: string;
  /** Approximate download size shown to the user */
  sizeLabel: string;
  recommended?: boolean;
}

const MODELS: ModelOption[] = [
  {
    name: "phi4-mini",
    label: "Phi 4 Mini",
    description: "Speed model — snappy responses, great for quick tasks",
    sizeLabel: "~2.3 GB",
    recommended: true,
  },
  {
    name: "qwen3:8b",
    label: "Qwen 3 8B",
    description: "Balanced model — strong reasoning for most tasks",
    sizeLabel: "~5.2 GB",
    recommended: true,
  },
  {
    name: "llama3.2",
    label: "Llama 3.2 3B",
    description: "Versatile general-purpose model by Meta",
    sizeLabel: "~2.0 GB",
  },
  {
    name: "nomic-embed-text",
    label: "Nomic Embed Text",
    description: "Embedding model — required for Knowledge search",
    sizeLabel: "~274 MB",
    recommended: true,
  },
];

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface PullProgress {
  model: string;
  status: string;
  percent: number;
  completed: number;
  total: number;
}

type ModelState = "idle" | "pulling" | "done" | "error";

interface Props {
  /** Called when setup is finished (or skipped) so App can proceed. */
  onComplete: () => void;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function formatBytes(bytes: number): string {
  if (bytes === 0) return "0 B";
  const gb = bytes / 1_000_000_000;
  if (gb >= 1) return `${gb.toFixed(1)} GB`;
  const mb = bytes / 1_000_000;
  return `${mb.toFixed(0)} MB`;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export function FirstRunSetup({ onComplete }: Props) {
  // `null` while we're still checking Ollama
  const [needsSetup, setNeedsSetup] = useState<boolean | null>(null);
  const [selected, setSelected] = useState<Set<string>>(
    new Set(MODELS.filter((m) => m.recommended).map((m) => m.name))
  );
  const [pulling, setPulling] = useState(false);
  const [currentModel, setCurrentModel] = useState<string | null>(null);
  const [modelStates, setModelStates] = useState<Record<string, ModelState>>({});
  const [progressMap, setProgressMap] = useState<Record<string, PullProgress>>({});
  const [errorMap, setErrorMap] = useState<Record<string, string>>({});

  // ── Check existing models on mount ──────────────────────────────────────
  useEffect(() => {
    invoke<string[]>("list_ollama_models")
      .then((models) => {
        if (models.length > 0) {
          // Models already installed — skip setup
          onComplete();
        } else {
          setNeedsSetup(true);
        }
      })
      .catch(() => {
        // Ollama might still be warming up — skip setup rather than block launch
        onComplete();
      });
  }, [onComplete]);

  // ── Toggle model selection ───────────────────────────────────────────────
  const toggleModel = useCallback((name: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(name)) next.delete(name);
      else next.add(name);
      return next;
    });
  }, []);

  // ── Pull all selected models sequentially ────────────────────────────────
  const startPull = useCallback(async () => {
    if (selected.size === 0) return;
    setPulling(true);

    // Listen for streaming progress from the Rust sidecar
    const unlisten = await listen<PullProgress>("pull-progress", (event) => {
      const p = event.payload;
      setProgressMap((prev) => ({ ...prev, [p.model]: p }));
      if (p.status === "success") {
        setModelStates((prev) => ({ ...prev, [p.model]: "done" }));
      }
    });

    const queue = Array.from(selected);
    for (const modelName of queue) {
      setCurrentModel(modelName);
      setModelStates((prev) => ({ ...prev, [modelName]: "pulling" }));
      try {
        await invoke("pull_ollama_model", { name: modelName });
        setModelStates((prev) => ({ ...prev, [modelName]: "done" }));
      } catch (err) {
        setModelStates((prev) => ({ ...prev, [modelName]: "error" }));
        setErrorMap((prev) => ({ ...prev, [modelName]: String(err) }));
      }
    }

    unlisten();
    setCurrentModel(null);
    // Brief pause so the user sees the last model complete
    await new Promise((r) => setTimeout(r, 800));
    onComplete();
  }, [selected, onComplete]);

  // ── Skip ─────────────────────────────────────────────────────────────────
  const skip = useCallback(() => onComplete(), [onComplete]);

  // ── Loading state ─────────────────────────────────────────────────────────
  if (needsSetup === null) {
    return (
      <div style={styles.overlay}>
        <div style={styles.card}>
          <p style={styles.checking}>Connecting to local AI…</p>
        </div>
      </div>
    );
  }

  if (!needsSetup) return null;

  const allDone = Array.from(selected).every(
    (n) => modelStates[n] === "done"
  );

  return (
    <div style={styles.overlay}>
      <div style={styles.card}>
        {/* Header */}
        <div style={styles.header}>
          <div style={styles.logo}>LM</div>
          <h1 style={styles.title}>Welcome to LocalMind</h1>
          <p style={styles.subtitle}>
            Download AI models to run entirely on your machine.
            <br />
            No internet required after download. No data ever leaves your device.
          </p>
        </div>

        {/* Model list */}
        <div style={styles.modelList}>
          {MODELS.map((model) => {
            const isSelected = selected.has(model.name);
            const state: ModelState = modelStates[model.name] ?? "idle";
            const prog = progressMap[model.name];

            return (
              <div
                key={model.name}
                style={{
                  ...styles.modelRow,
                  ...(isSelected && !pulling ? styles.modelRowSelected : {}),
                  ...(pulling ? styles.modelRowDisabled : {}),
                }}
                onClick={!pulling ? () => toggleModel(model.name) : undefined}
              >
                {/* Checkbox / status icon */}
                <div style={styles.checkCell}>
                  {state === "done" ? (
                    <span style={styles.iconDone}>✓</span>
                  ) : state === "error" ? (
                    <span style={styles.iconError}>✗</span>
                  ) : state === "pulling" ? (
                    <span style={styles.iconPulling}>↓</span>
                  ) : (
                    <div
                      style={{
                        ...styles.checkbox,
                        ...(isSelected ? styles.checkboxChecked : {}),
                      }}
                    >
                      {isSelected && <span style={styles.checkMark}>✓</span>}
                    </div>
                  )}
                </div>

                {/* Model info */}
                <div style={styles.modelInfo}>
                  <div style={styles.modelNameRow}>
                    <span style={styles.modelLabel}>{model.label}</span>
                    {model.recommended && (
                      <span style={styles.badge}>Recommended</span>
                    )}
                    <span style={styles.sizeLabel}>{model.sizeLabel}</span>
                  </div>
                  <p style={styles.modelDesc}>{model.description}</p>

                  {/* Progress bar */}
                  {state === "pulling" && (
                    <div style={{ marginTop: "6px" }}>
                      <div style={styles.progressTrack}>
                        <div
                          style={{
                            ...styles.progressBar,
                            width: `${prog?.percent ?? 0}%`,
                          }}
                        />
                      </div>
                      <span style={styles.progressText}>
                        {prog
                          ? prog.status === "downloading"
                            ? `${prog.percent}% — ${formatBytes(prog.completed)} / ${formatBytes(prog.total)}`
                            : prog.status
                          : "Starting…"}
                      </span>
                    </div>
                  )}

                  {state === "error" && (
                    <p style={styles.errorText}>
                      {errorMap[model.name] ?? "Download failed"}
                    </p>
                  )}
                </div>
              </div>
            );
          })}
        </div>

        {/* Footer */}
        <div style={styles.footer}>
          {!pulling ? (
            <>
              <button style={styles.skipBtn} onClick={skip}>
                Skip for now
              </button>
              <button
                style={{
                  ...styles.primaryBtn,
                  ...(selected.size === 0 ? styles.primaryBtnDisabled : {}),
                }}
                onClick={startPull}
                disabled={selected.size === 0}
              >
                Download {selected.size > 0 ? `${selected.size} model${selected.size > 1 ? "s" : ""}` : "models"}
              </button>
            </>
          ) : allDone ? (
            <button style={styles.primaryBtn} onClick={onComplete}>
              Launch LocalMind →
            </button>
          ) : (
            <p style={styles.pullingNote}>
              Downloading{currentModel ? ` ${currentModel}` : ""}… please keep the app open.
            </p>
          )}
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Inline styles
// ---------------------------------------------------------------------------

const styles: Record<string, React.CSSProperties> = {
  overlay: {
    position: "fixed",
    inset: 0,
    background: "var(--bg-base)",
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    zIndex: 9999,
    padding: "24px",
  },
  card: {
    background: "var(--bg-surface)",
    border: "1px solid var(--border)",
    borderRadius: "16px",
    padding: "40px",
    maxWidth: "560px",
    width: "100%",
    display: "flex",
    flexDirection: "column",
    gap: "28px",
    boxShadow: "0 24px 64px rgba(0,0,0,0.5)",
  },
  header: {
    textAlign: "center",
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    gap: "12px",
  },
  logo: {
    width: "56px",
    height: "56px",
    borderRadius: "14px",
    background: "var(--accent)",
    color: "#fff",
    fontWeight: 700,
    fontSize: "22px",
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    letterSpacing: "-0.5px",
  },
  title: {
    margin: 0,
    fontSize: "22px",
    fontWeight: 700,
    color: "var(--text-primary)",
  },
  subtitle: {
    margin: 0,
    fontSize: "14px",
    color: "var(--text-secondary)",
    lineHeight: 1.6,
  },
  checking: {
    color: "var(--text-secondary)",
    margin: 0,
    fontSize: "14px",
  },
  modelList: {
    display: "flex",
    flexDirection: "column",
    gap: "8px",
  },
  modelRow: {
    display: "flex",
    gap: "14px",
    padding: "14px 16px",
    borderRadius: "10px",
    border: "1px solid var(--border)",
    background: "var(--bg-base)",
    cursor: "pointer",
    transition: "border-color 0.15s, background 0.15s",
  },
  modelRowSelected: {
    borderColor: "var(--accent)",
    background: "color-mix(in srgb, var(--accent) 8%, var(--bg-base))",
  },
  modelRowDisabled: {
    cursor: "default",
  },
  checkCell: {
    flexShrink: 0,
    paddingTop: "2px",
  },
  checkbox: {
    width: "18px",
    height: "18px",
    borderRadius: "5px",
    border: "2px solid var(--border)",
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    transition: "border-color 0.15s, background 0.15s",
  },
  checkboxChecked: {
    borderColor: "var(--accent)",
    background: "var(--accent)",
  },
  checkMark: {
    color: "#fff",
    fontSize: "11px",
    fontWeight: 700,
    lineHeight: 1,
  },
  iconDone: {
    color: "var(--accent)",
    fontSize: "16px",
    fontWeight: 700,
  },
  iconError: {
    color: "var(--error, #ef4444)",
    fontSize: "16px",
    fontWeight: 700,
  },
  iconPulling: {
    color: "var(--accent)",
    fontSize: "16px",
    fontWeight: 700,
  },
  modelInfo: {
    flex: 1,
    minWidth: 0,
    display: "flex",
    flexDirection: "column",
    gap: "4px",
  },
  modelNameRow: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
  },
  modelLabel: {
    fontWeight: 600,
    fontSize: "14px",
    color: "var(--text-primary)",
  },
  badge: {
    fontSize: "11px",
    fontWeight: 600,
    color: "var(--accent)",
    background: "color-mix(in srgb, var(--accent) 15%, transparent)",
    padding: "2px 6px",
    borderRadius: "4px",
  },
  sizeLabel: {
    marginLeft: "auto",
    fontSize: "12px",
    color: "var(--text-secondary)",
  },
  modelDesc: {
    margin: 0,
    fontSize: "13px",
    color: "var(--text-secondary)",
  },
  progressTrack: {
    height: "6px",
    borderRadius: "3px",
    background: "var(--bg-hover)",
    overflow: "hidden",
    position: "relative" as const,
  },
  progressBar: {
    position: "absolute" as const,
    top: 0,
    left: 0,
    height: "100%",
    borderRadius: "3px",
    background: "var(--accent)",
    transition: "width 0.3s ease",
  },
  progressText: {
    display: "block",
    marginTop: "4px",
    fontSize: "12px",
    color: "var(--text-secondary)",
    whiteSpace: "nowrap" as const,
  },
  errorText: {
    margin: 0,
    marginTop: "4px",
    fontSize: "12px",
    color: "var(--error, #ef4444)",
  },
  footer: {
    display: "flex",
    alignItems: "center",
    justifyContent: "flex-end",
    gap: "12px",
  },
  skipBtn: {
    background: "none",
    border: "none",
    color: "var(--text-secondary)",
    fontSize: "14px",
    cursor: "pointer",
    padding: "8px 12px",
    borderRadius: "8px",
  },
  primaryBtn: {
    background: "var(--accent)",
    color: "#fff",
    border: "none",
    borderRadius: "8px",
    padding: "10px 20px",
    fontSize: "14px",
    fontWeight: 600,
    cursor: "pointer",
  },
  primaryBtnDisabled: {
    opacity: 0.4,
    cursor: "not-allowed",
  },
  pullingNote: {
    margin: 0,
    fontSize: "13px",
    color: "var(--text-secondary)",
  },
};
