import { invoke } from "@tauri-apps/api/core";
import { useEffect, useState, type ReactNode } from "react";
import {
  BookOpen,
  Check,
  Cpu,
  Download,
  Globe,
  HardDrive,
  LoaderCircle,
  ShieldCheck,
} from "lucide-react";
import { OllamaStatus } from "../hooks/useOllama";
import { ModelProfileSettings } from "./ModelProfileSettings";
import "./supporting-pages.css";

interface SettingsPageProps {
  ollamaStatus: OllamaStatus | null;
  speedModel: string;
  balancedModel: string;
  onSpeedModelChange: (model: string) => void;
  onBalancedModelChange: (model: string) => void;
  hydeEnabled: boolean;
  onHydeToggle: () => void;
  onManageModels?: () => void;
}
const SECTIONS = [
  { id: "models", label: "Models", icon: Cpu },
  { id: "hardware", label: "Hardware", icon: HardDrive },
  { id: "search", label: "Web search", icon: Globe },
  { id: "knowledge", label: "Knowledge", icon: BookOpen },
  { id: "privacy", label: "Data & privacy", icon: ShieldCheck },
] as const;
type Section = (typeof SECTIONS)[number]["id"];

export function SettingsPage(props: SettingsPageProps) {
  const [section, setSection] = useState<Section>("models");
  return (
    <div className="workspace-page">
      <div className="workspace-page-inner">
        <header className="page-heading">
          <div>
            <h1>Settings</h1>
            <p>Make LocalMind work for this computer and the way you use it.</p>
          </div>
        </header>
        <div className="settings-layout">
          <nav className="settings-nav" aria-label="Settings sections">
            {SECTIONS.map(({ id, label, icon: Icon }) => (
              <button
                key={id}
                aria-current={section === id ? "page" : undefined}
                onClick={() => setSection(id)}
              >
                <Icon size={16} strokeWidth={1.7} />
                {label}
              </button>
            ))}
          </nav>
          <div className="settings-content">
            {section === "models" && <ModelSettings {...props} />}
            {section === "hardware" && (
              <HardwareSettings status={props.ollamaStatus} />
            )}
            {section === "search" && <SearchSettings />}
            {section === "knowledge" && (
              <section>
                <div className="settings-section-heading">
                  <h2>Knowledge retrieval</h2>
                  <p>
                    Choose how LocalMind finds passages in your indexed files.
                  </p>
                </div>
                <SettingRow
                  label="Expanded search"
                  hint="Generate a draft answer to help locate related passages. This can improve relevance, but takes longer."
                >
                  <button
                    className="page-switch"
                    role="switch"
                    aria-label="Expanded knowledge search"
                    aria-checked={props.hydeEnabled}
                    onClick={props.onHydeToggle}
                  >
                    <span />
                  </button>
                </SettingRow>
                <p className="page-footnote">
                  Uses the Speed model. Falls back to direct search when the
                  model is unavailable.
                </p>
              </section>
            )}
            {section === "privacy" && (
              <section>
                <div className="settings-section-heading">
                  <h2>Data & privacy</h2>
                  <p>Your installation has its own local workspace.</p>
                </div>
                <div className="privacy-facts">
                  <div>
                    <h3>Conversations and memory</h3>
                    <p>
                      Stored on this computer under your operating system user
                      account. Review and remove saved context in Memory.
                    </p>
                  </div>
                  <div>
                    <h3>Local AI</h3>
                    <p>
                      Responses are generated through Ollama on this computer.
                      Model downloads require an internet connection.
                    </p>
                  </div>
                  <div>
                    <h3>Optional online services</h3>
                    <p>
                      Web search sends search queries to your chosen provider.
                      Connected apps communicate with their services when you
                      connect or sync them.
                    </p>
                  </div>
                  <div>
                    <h3>Other installations</h3>
                    <p>
                      Each person sets up their own models and connections.
                      LocalMind does not synchronize conversations between
                      installations.
                    </p>
                  </div>
                </div>
              </section>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

function SettingRow({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: ReactNode;
}) {
  return (
    <div className="setting-row">
      <div>
        <h3>{label}</h3>
        {hint && <p>{hint}</p>}
      </div>
      <div className="setting-control">{children}</div>
    </div>
  );
}
function ModelSettings({
  ollamaStatus,
  speedModel,
  balancedModel,
  onSpeedModelChange,
  onBalancedModelChange,
  onManageModels,
}: SettingsPageProps) {
  const models = ollamaStatus?.models ?? [];
  const running = ollamaStatus?.running ?? false;
  const names = models.map((model) => model.name);
  function select(
    label: string,
    value: string,
    change: (model: string) => void,
  ) {
    return (
      <select
        className="page-select"
        aria-label={label}
        value={value}
        onChange={(event) => change(event.target.value)}
        disabled={!running || !names.length}
      >
        {!names.includes(value) && (
          <option value={value}>{value} (not installed)</option>
        )}
        {names.map((name) => (
          <option key={name} value={name}>
            {name}
          </option>
        ))}
      </select>
    );
  }
  return (
    <section>
      <div className="settings-section-heading">
        <h2>Local models</h2>
        <p>Choose from the models installed in Ollama.</p>
      </div>
      <div className="runtime-summary">
        <Cpu size={20} />
        <div>
          <strong>
            {ollamaStatus === null
              ? "Checking Ollama…"
              : running
                ? "Ollama is connected"
                : "Ollama is unavailable"}
          </strong>
          <p>
            {running
              ? `${models.length} installed model${models.length === 1 ? "" : "s"}${ollamaStatus?.version ? ` · Version ${ollamaStatus.version}` : ""}`
              : "Open Ollama on this computer to use local models."}
          </p>
        </div>
        <span className={`status-dot ${running ? "" : "offline"}`} />
      </div>
      <SettingRow
        label="Speed"
        hint="For quick conversations and background tasks."
      >
        {select("Speed model", speedModel, onSpeedModelChange)}
      </SettingRow>
      <SettingRow
        label="Balanced"
        hint="For everyday chat and more involved questions."
      >
        {select("Balanced model", balancedModel, onBalancedModelChange)}
      </SettingRow>
      <div className="section-heading">
        <h2>Installed models</h2>
        {onManageModels && (
          <button className="page-button" onClick={onManageModels}>
            <Download size={15} />
            Download models
          </button>
        )}
      </div>
      {models.length ? (
        <div className="installed-models">
          {models.map((model) => (
            <div key={model.name}>
              <span>{model.name}</span>
              <span className="muted">{formatBytes(model.size)}</span>
            </div>
          ))}
        </div>
      ) : (
        <p className="page-notice">
          No installed models found.{" "}
          {onManageModels
            ? "Choose Download models to install one on this computer."
            : "Install a model with Ollama, then return here to choose it."}
        </p>
      )}
      <p className="page-footnote">
        Model selections save automatically. Performance depends on model size,
        memory, and your hardware. Larger models can require more time and disk
        space.
      </p>
      {running && names.length > 0 && (
        <ModelProfileSettings speedModel={speedModel} balancedModel={balancedModel} />
      )}
    </section>
  );
}
function HardwareSettings({ status }: { status: OllamaStatus | null }) {
  const gpu = status?.gpu;
  const usedPercent =
    gpu && gpu.vramTotalMib > 0
      ? Math.min(
          100,
          Math.max(
            0,
            ((gpu.vramTotalMib - gpu.vramFreeMib) / gpu.vramTotalMib) * 100,
          ),
        )
      : 0;
  return (
    <section>
      <div className="settings-section-heading">
        <h2>Hardware</h2>
        <p>Resources detected on this computer.</p>
      </div>
      <SettingRow label="Graphics processor">
        <span>
          {status === null
            ? "Checking…"
            : (gpu?.name ?? "No supported GPU detected")}
        </span>
      </SettingRow>
      {gpu && (
        <>
          <SettingRow label="Available GPU memory">
            <span>
              {(gpu.vramFreeMib / 1024).toFixed(1)} GB of{" "}
              {(gpu.vramTotalMib / 1024).toFixed(1)} GB
            </span>
          </SettingRow>
          <div className="hardware-meter">
            <progress
              value={usedPercent}
              max={100}
              aria-label="GPU memory used"
            />
            <span>{Math.round(usedPercent)}% in use</span>
          </div>
        </>
      )}
      <p className="page-notice">
        Ollama manages model placement across GPU and system memory. Hardware
        detection here does not confirm that a particular model is running on
        the GPU.
      </p>
    </section>
  );
}
type Provider = "duckduckgo" | "searxng";
interface SearchConfig {
  provider: string;
  privacy_mode: "standard" | "strict";
  enabled: boolean;
  searxng_url?: string;
}
const PROVIDERS: Record<Provider, string> = {
  duckduckgo: "DuckDuckGo",
  searxng: "SearxNG",
};
function SearchSettings() {
  const [config, setConfig] = useState<SearchConfig>({
    provider: "duckduckgo",
    privacy_mode: "standard",
    enabled: false,
    searxng_url: "http://localhost:8080",
  });
  const [searxngUrl, setSearxngUrl] = useState("http://localhost:8080");
  const [loading, setLoading] = useState(true);
  const [loadFailed, setLoadFailed] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [retry, setRetry] = useState(0);
  const supportedProvider =
    config.provider === "duckduckgo" || config.provider === "searxng";
  useEffect(() => {
    let cancelled = false;
    invoke<SearchConfig>("get_search_config")
      .then((value) => {
        if (!cancelled) {
          setConfig(value);
          setSearxngUrl(value.searxng_url ?? "http://localhost:8080");
          setLoadFailed(false);
          setError(null);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setError("Couldn’t load search settings. Please try again.");
          setLoadFailed(true);
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [retry]);
  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (saving || !supportedProvider) return;
    setSaving(true);
    setError(null);
    setSaved(false);
    try {
      const result = await invoke<SearchConfig>("update_search_config", {
        provider: config.provider,
        privacyMode: config.privacy_mode,
        enabled: config.enabled,
        apiKey: null,
        searxngUrl: searxngUrl.trim() || null,
      });
      setConfig(result);
      setSearxngUrl(result.searxng_url ?? searxngUrl);
      setSaved(true);
    } catch {
      setError(
        "Couldn’t save search settings. Check the local connection and try again.",
      );
    } finally {
      setSaving(false);
    }
  }
  return (
    <section>
      <div className="settings-section-heading">
        <h2>Web search</h2>
        <p>
          Turn Web on in the chat composer to search. Queries are sent to your
          chosen provider.
        </p>
      </div>
      {error && (
        <div className="page-notice error" role="alert">
          {error}
          {loadFailed && (
            <button
              className="page-button"
              onClick={() => {
                setLoading(true);
                setRetry((value) => value + 1);
              }}
            >
              Retry
            </button>
          )}
        </div>
      )}
      {loading ? (
        <p role="status" className="muted">
          Loading settings…
        </p>
      ) : (
        !loadFailed && (
          <form onSubmit={save}>
            {!supportedProvider && (
              <p className="page-notice" role="status">
                Your saved provider is unavailable in this version. Choose
                DuckDuckGo or SearxNG to use web search.
              </p>
            )}
            <SettingRow
              label="Search provider"
              hint="Queries are sent to this service."
            >
              <select
                className="page-select"
                aria-label="Search provider"
                value={config.provider}
                onChange={(event) => {
                  setConfig((value) => ({
                    ...value,
                    provider: event.target.value,
                  }));
                  setSaved(false);
                }}
              >
                {!supportedProvider && (
                  <option value={config.provider} disabled>
                    {config.provider} (unavailable)
                  </option>
                )}
                {Object.entries(PROVIDERS).map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </select>
            </SettingRow>
            {config.provider === "searxng" && (
              <SettingRow
                label="SearxNG address"
                hint="The address of your self-hosted search service."
              >
                <input
                  className="page-input"
                  type="url"
                  required
                  aria-label="SearxNG address"
                  placeholder="http://localhost:8080"
                  value={searxngUrl}
                  onChange={(event) => {
                    setSearxngUrl(event.target.value);
                    setSaved(false);
                  }}
                />
              </SettingRow>
            )}
            <div className="settings-save">
              <button
                className="page-button primary"
                type="submit"
                disabled={
                  saving ||
                  !supportedProvider ||
                  (config.provider === "searxng" && !searxngUrl.trim())
                }
              >
                {saving ? (
                  <LoaderCircle size={15} className="spin" />
                ) : saved ? (
                  <Check size={15} />
                ) : null}
                {saving ? "Saving…" : saved ? "Saved" : "Save changes"}
              </button>
              <span aria-live="polite">
                {saved ? "Your search settings are up to date." : ""}
              </span>
            </div>
          </form>
        )
      )}
    </section>
  );
}

function formatBytes(bytes: number) {
  return bytes >= 1024 ** 3
    ? `${(bytes / 1024 ** 3).toFixed(1)} GB`
    : `${Math.round(bytes / 1024 ** 2)} MB`;
}
