import { useState, useEffect, useCallback, useRef } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import {
  ArrowRight,
  CheckCircle2,
  Download,
  LoaderCircle,
  AlertCircle,
} from "lucide-react";
import "./supporting-pages.css";

const MODELS = [
  {
    name: "qwen2.5:1.5b",
    label: "Qwen 2.5 · 1.5B",
    description:
      "A compact model for quick conversations and background tasks.",
    size: "~1.0 GB",
    recommended: true,
  },
  {
    name: "qwen3:8b",
    label: "Qwen 3 · 8B",
    description:
      "A larger model for everyday chat and more involved questions.",
    size: "~5.2 GB",
    recommended: true,
  },
  {
    name: "llama3.2",
    label: "Llama 3.2 · 3B",
    description: "A general-purpose model from Meta.",
    size: "~2.0 GB",
    recommended: false,
  },
  {
    name: "nomic-embed-text",
    label: "Nomic Embed Text",
    description: "Required to search and reference your files in Knowledge.",
    size: "~274 MB",
    recommended: true,
  },
];
interface PullProgress {
  model: string;
  status: string;
  percent: number;
  completed: number;
  total: number;
}
type ModelState = "idle" | "pulling" | "done" | "error";

export function FirstRunSetup({
  onComplete,
  forceShow = false,
}: {
  onComplete: () => void;
  forceShow?: boolean;
}) {
  const [checking, setChecking] = useState(true);
  const [selected, setSelected] = useState(
    () =>
      new Set(
        MODELS.filter((model) => model.recommended).map((model) => model.name),
      ),
  );
  const [pulling, setPulling] = useState(false);
  const [states, setStates] = useState<Record<string, ModelState>>({});
  const [progress, setProgress] = useState<Record<string, PullProgress>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);
  const dialogRef = useRef<HTMLDivElement>(null);
  const unlistenRef = useRef<UnlistenFn | null>(null);
  useEffect(() => {
    let cancelled = false;
    mounted.current = true;
    invoke<string[]>("list_ollama_models")
      .then((models) => {
        if (cancelled) return;
        if (models.length && !forceShow) onComplete();
        else setChecking(false);
      })
      .catch(() => {
        if (cancelled) return;
        if (forceShow) {
          setChecking(false);
          setError(
            "Couldn’t check installed models. Make sure Ollama is running before downloading.",
          );
        } else onComplete();
      });
    return () => {
      cancelled = true;
      mounted.current = false;
      unlistenRef.current?.();
    };
  }, [onComplete, forceShow]);
  useEffect(() => {
    dialogRef.current?.focus();
  }, [checking]);

  const toggle = useCallback((name: string) => {
    setSelected((previous) => {
      const next = new Set(previous);
      if (next.has(name)) next.delete(name);
      else next.add(name);
      return next;
    });
  }, []);
  async function download() {
    if (!selected.size || pulling) return;
    setPulling(true);
    setError(null);
    try {
      const unlisten = await listen<PullProgress>(
        "pull-progress",
        ({ payload }) => {
          if (!mounted.current) return;
          setProgress((previous) => ({
            ...previous,
            [payload.model]: payload,
          }));
        },
      );
      if (!mounted.current) {
        unlisten();
        return;
      }
      unlistenRef.current = unlisten;
      for (const name of selected) {
        if (!mounted.current) break;
        if (states[name] === "done") continue;
        setStates((previous) => ({ ...previous, [name]: "pulling" }));
        setErrors((previous) => ({ ...previous, [name]: "" }));
        try {
          await invoke("pull_ollama_model", { name });
          if (mounted.current)
            setStates((previous) => ({ ...previous, [name]: "done" }));
        } catch {
          if (mounted.current) {
            setStates((previous) => ({ ...previous, [name]: "error" }));
            setErrors((previous) => ({
              ...previous,
              [name]: "Download failed. Check your connection and try again.",
            }));
          }
        }
      }
    } catch {
      if (mounted.current)
        setError(
          "Couldn’t start the download. Make sure Ollama is running, then try again.",
        );
    } finally {
      unlistenRef.current?.();
      unlistenRef.current = null;
      if (mounted.current) setPulling(false);
    }
  }
  const allDone =
    selected.size > 0 && [...selected].every((name) => states[name] === "done");
  const hasErrors = [...selected].some((name) => states[name] === "error");
  return (
    <div className="setup-overlay">
      <div
        className="setup-dialog"
        ref={dialogRef}
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-labelledby="setup-title"
        onKeyDown={(event) => {
          if (event.key !== "Tab") return;
          const controls = Array.from(
            event.currentTarget.querySelectorAll<HTMLElement>(
              "button:not([disabled]), input:not([disabled])",
            ),
          );
          const first = controls[0];
          const last = controls[controls.length - 1];
          if (!first) {
            event.preventDefault();
            return;
          }
          if (
            event.shiftKey &&
            (document.activeElement === first ||
              document.activeElement === dialogRef.current)
          ) {
            event.preventDefault();
            last.focus();
          } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
          }
        }}
      >
        {checking ? (
          <div className="setup-checking" role="status">
            <LoaderCircle className="spin" size={25} />
            <h1 id="setup-title">Checking your local models</h1>
            <p>This should only take a moment.</p>
          </div>
        ) : (
          <>
            <header className="setup-heading">
              <img
                src="/brand/localmind-mark-256.png"
                width="44"
                height="44"
                alt=""
              />
              <h1 id="setup-title">
                {forceShow
                  ? "Download local models"
                  : "Make this your LocalMind"}
              </h1>
              <p>
                Choose the models to install on this computer. You can change
                your selection later in Settings.
              </p>
            </header>
            {error && (
              <p className="page-notice error" role="alert">
                {error}
              </p>
            )}
            <div className="setup-models">
              {MODELS.map((model) => {
                const state = states[model.name] ?? "idle";
                const current = progress[model.name];
                const percent = Math.min(
                  100,
                  Math.max(0, current?.percent ?? 0),
                );
                return (
                  <label
                    className={`setup-model ${selected.has(model.name) ? "selected" : ""}`}
                    key={model.name}
                  >
                    <input
                      type="checkbox"
                      checked={selected.has(model.name)}
                      disabled={pulling || state === "done"}
                      onChange={() => toggle(model.name)}
                    />
                    <div className="setup-model-content">
                      <div className="setup-model-name">
                        <strong>{model.label}</strong>
                        <span>{model.size}</span>
                      </div>
                      <p>{model.description}</p>
                      {state === "pulling" && (
                        <div className="setup-progress" role="status">
                          <progress
                            value={percent}
                            max={100}
                            aria-label={`Downloading ${model.label}`}
                          />
                          <span>
                            {current?.status === "downloading"
                              ? `${percent}% · ${formatBytes(current.completed)} / ${formatBytes(current.total)}`
                              : (current?.status ?? "Starting download…")}
                          </span>
                        </div>
                      )}
                      {state === "done" && (
                        <span className="setup-state">
                          <CheckCircle2 size={13} />
                          Installed
                        </span>
                      )}
                      {state === "error" && (
                        <span className="setup-state error-text" role="alert">
                          <AlertCircle size={13} />
                          {errors[model.name]}
                        </span>
                      )}
                    </div>
                  </label>
                );
              })}
            </div>
            <p className="setup-note">
              Downloads need an internet connection and free disk space. Local
              chat runs on your computer; optional web search and app
              connections use online services.
            </p>
            <footer className="setup-footer">
              {pulling ? (
                <p role="status">
                  <LoaderCircle size={15} className="spin" />
                  Downloading models. Keep LocalMind open.
                </p>
              ) : (
                <>
                  <button className="page-button" onClick={onComplete}>
                    {hasErrors
                      ? "Continue with available models"
                      : forceShow
                        ? "Done"
                        : "Set up later"}
                  </button>
                  {allDone ? (
                    <button
                      className="page-button primary"
                      onClick={onComplete}
                    >
                      Open LocalMind <ArrowRight size={15} />
                    </button>
                  ) : (
                    <button
                      className="page-button primary"
                      disabled={!selected.size}
                      onClick={() => void download()}
                    >
                      <Download size={15} />
                      {hasErrors
                        ? "Retry downloads"
                        : `Download ${selected.size || ""} model${selected.size === 1 ? "" : "s"}`}
                    </button>
                  )}
                </>
              )}
            </footer>
          </>
        )}
      </div>
    </div>
  );
}
function formatBytes(bytes: number) {
  return bytes >= 1_000_000_000
    ? `${(bytes / 1_000_000_000).toFixed(1)} GB`
    : `${Math.round(bytes / 1_000_000)} MB`;
}
