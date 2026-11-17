import { useEffect, useId, useRef, useState } from "react";
import { Check, ChevronDown, ChevronRight, Gauge, Search, Settings2, Zap } from "lucide-react";
import type { ModelMode } from "../types";
import { providerError, providerRequest, type CloudProvider, type ProviderId, type ProviderModel } from "../lib/providers";
import { isChatModel } from "../lib/agents";
import { BrandLogo } from "./BrandLogo";

const MODES = [
  { value: "speed", label: "Fast", description: "Quick drafts; check instructions and facts", Icon: Zap },
  { value: "balanced", label: "Smart", description: "More room for detailed answers", Icon: Gauge },
] as const;

interface Props {
  value: ModelMode;
  onChange: (mode: ModelMode) => void;
  modelName: string;
  models?: Partial<Record<ModelMode, string>>;
  disabled: boolean;
  provider?: ProviderId;
  providers?: CloudProvider[];
  localOnly?: boolean;
  installed?: string[];
  onSelectLocal?: (model: string) => void;
  onSelectProvider?: (provider: ProviderId) => void;
  onManageCloud?: () => void;
}

export function ModelPicker({ value, onChange, modelName, models, disabled, provider = "ollama", providers = [],
  localOnly = false, installed = [], onSelectLocal, onSelectProvider, onManageCloud }: Props) {
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const id = useId();
  const expanded = open && !disabled;
  const selected = MODES.find(mode => mode.value === value) ?? MODES[1];
  const local = provider === "ollama";
  const cloud = providers.find(item => item.id === provider);
  const connected = providers.filter(item => item.connected && item.model);
  const presets = new Set(Object.values(models ?? {}));
  const others = installed.filter(name => !presets.has(name));
  // Do not reopen an interrupted picker when generation or voice work finishes.
  if (disabled && open) setOpen(false);

  useEffect(() => {
    if (!expanded) return;
    root.current?.querySelector<HTMLButtonElement>("[aria-pressed='true']")?.focus({ preventScroll: true });
    const outside = (event: PointerEvent) => {
      if (!root.current?.contains(event.target as Node)) setOpen(false);
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      event.preventDefault();
      event.stopPropagation();
      setOpen(false);
      trigger.current?.focus({ preventScroll: true });
    };
    document.addEventListener("pointerdown", outside);
    document.addEventListener("keydown", escape, true);
    return () => {
      document.removeEventListener("pointerdown", outside);
      document.removeEventListener("keydown", escape, true);
    };
  }, [expanded]);

  const close = () => { setOpen(false); trigger.current?.focus({ preventScroll: true }); };
  const label = local ? `Model mode: ${selected.label}, ${modelName}` : `Cloud model: ${cloud?.label ?? provider}, ${modelName}`;

  return <div className="model-picker" ref={root} onBlur={event => {
    if (!event.currentTarget.contains(event.relatedTarget as Node)) setOpen(false);
  }}>
    <button type="button" className="model-picker-trigger" ref={trigger} aria-label={label}
      aria-expanded={expanded} aria-controls={expanded ? id : undefined} disabled={disabled}
      title={modelName} onClick={() => setOpen(current => !current)}>
      <BrandLogo id={provider} label={cloud?.label ?? "Ollama"} size={16} />
      <span className="model-picker-name">{modelName}</span>
      {local && <span className="model-picker-mode">{selected.label}</span>}
      <ChevronDown size={13} aria-hidden="true" />
    </button>
    {expanded && <section className="model-picker-panel" id={id} aria-label="Choose a model">
      <h2 className="picker-group">On this device</h2>
      {MODES.map(({ value: mode, label: name, description, Icon }) => {
        const active = local && value === mode;
        return <button type="button" key={mode} className="model-option" aria-label={`${name}: ${description}`} aria-pressed={active}
          onClick={() => { onChange(mode); onSelectProvider?.("ollama"); close(); }}>
          <Icon size={16} aria-hidden="true" />
          <span><strong>{name}</strong><small>{description}</small>
            <code>{models?.[mode] || (active ? modelName : "Configured in Settings")}</code></span>
          {active && <Check size={15} aria-hidden="true" />}
        </button>;
      })}
      {others.length > 0 && <details className="picker-more">
        <summary>Other installed models <span>{others.length}</span></summary>
        {others.map(name => <button type="button" key={name} className="model-row" aria-pressed={local && modelName === name}
          onClick={() => { onSelectLocal?.(name); onSelectProvider?.("ollama"); close(); }}>
          <span>{name}</span>{local && modelName === name && <Check size={14} aria-hidden="true" />}
        </button>)}
      </details>}

      <h2 className="picker-group">{localOnly ? "Cloud (blocked by Keep chats on this device)" : "Cloud models"}</h2>
      {connected.map(item => <CloudAccount key={item.id} provider={item} active={provider === item.id} localOnly={localOnly}
        onPick={() => { onSelectProvider?.(item.id); close(); }} />)}
      {connected.length === 0 && <p className="picker-empty">No cloud account yet. Add an API key to use OpenAI, Claude, Gemini and more.</p>}
      <button type="button" className="model-row picker-manage" onClick={() => { onManageCloud?.(); close(); }}>
        <Settings2 size={14} aria-hidden="true" /><span>{connected.length ? "Manage cloud models" : "Set up cloud models"}</span>
      </button>
    </section>}
  </div>;
}

function CloudAccount({ provider, active, localOnly, onPick }: { provider: CloudProvider; active: boolean; localOnly: boolean; onPick: () => void }) {
  const [browsing, setBrowsing] = useState(false);
  const [models, setModels] = useState<ProviderModel[] | null>(null);
  const [query, setQuery] = useState("");
  const [note, setNote] = useState("");
  const load = async () => {
    setBrowsing(true);
    if (models) return;
    try {
      const response = await providerRequest<{ models: ProviderModel[] }>("GET", `/providers/${provider.id}/models`);
      setModels(response.models.filter(item => isChatModel(item.id) || item.id === provider.model));
    } catch (cause) { setNote(providerError(cause)); }
  };
  const choose = async (item: ProviderModel) => {
    try {
      await providerRequest("PATCH", `/providers/${provider.id}/config`, { model: item.id, vision: item.vision === true });
      window.dispatchEvent(new Event("localmind-providers-changed"));
      onPick();
    } catch (cause) { setNote(providerError(cause)); }
  };
  const shown = (models ?? []).filter(item => `${item.name} ${item.id}`.toLowerCase().includes(query.trim().toLowerCase())).slice(0, 60);
  return <div className="picker-cloud">
    <button type="button" className="model-option" aria-pressed={active} disabled={localOnly} onClick={onPick}
      aria-label={`${provider.label}, ${provider.model}`}>
      <BrandLogo id={provider.id} label={provider.label} size={18} />
      <span><strong>{provider.label}</strong><code>{provider.model}</code></span>
      <small className="picker-tag">{provider.vision ? "Images" : "Text"}</small>
      {active && <Check size={15} aria-hidden="true" />}
    </button>
    {!localOnly && <button type="button" className="picker-browse" aria-expanded={browsing}
      onClick={() => browsing ? setBrowsing(false) : void load()}>
      <ChevronRight size={12} aria-hidden="true" />Browse {provider.label} models</button>}
    {browsing && <div className="picker-browser">
      <label className="picker-search"><Search size={13} aria-hidden="true" />
        <input value={query} onChange={event => setQuery(event.target.value)} placeholder="Filter models" aria-label={`Filter ${provider.label} models`} /></label>
      {note && <p className="picker-empty" role="alert">{note}</p>}
      {!note && !models && <p className="picker-empty">Loading models…</p>}
      <div className="picker-list">
        {shown.map(item => <button type="button" key={item.id} className="model-row" aria-pressed={item.id === provider.model} onClick={() => void choose(item)}>
          <span>{item.name !== item.id ? <>{item.name}<code>{item.id}</code></> : item.id}</span>
          {item.vision === true && <small className="picker-tag">Images</small>}
          {item.id === provider.model && <Check size={14} aria-hidden="true" />}
        </button>)}
        {models && shown.length === 0 && <p className="picker-empty">No model matches.</p>}
      </div>
    </div>}
  </div>;
}
