import { useEffect, useId, useRef, useState } from "react";
import { Check, ChevronDown, Cpu, Gauge, Zap } from "lucide-react";
import type { ModelMode } from "../types";

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
}

export function ModelPicker({ value, onChange, modelName, models, disabled }: Props) {
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const id = useId();
  const expanded = open && !disabled;
  const selected = MODES.find(mode => mode.value === value) ?? MODES[1];
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

  return <div className="model-picker" ref={root} onBlur={event => {
    if (!event.currentTarget.contains(event.relatedTarget as Node)) setOpen(false);
  }}>
    <button type="button" className="model-picker-trigger" ref={trigger}
      aria-label={`Model mode: ${selected.label}, ${modelName}`}
      aria-expanded={expanded} aria-controls={expanded ? id : undefined} disabled={disabled}
      title={`${selected.label} · ${modelName}`} onClick={() => setOpen(current => !current)}>
      <Cpu size={15} aria-hidden="true" />
      <span className="model-picker-name">{modelName}</span>
      <span className="model-picker-mode">{selected.label}</span>
      <ChevronDown size={13} aria-hidden="true" />
    </button>
    {expanded && <section className="model-picker-panel" id={id} aria-label="Choose response mode">
      <div className="picker-heading"><strong>Response mode</strong><span>Local models</span></div>
      {MODES.map(({ value: mode, label, description, Icon }) => <button type="button" key={mode}
        className="model-option" aria-label={`${label}: ${description}`} aria-pressed={value === mode} onClick={() => {
          onChange(mode); setOpen(false); trigger.current?.focus({ preventScroll: true });
        }}>
        <Icon size={17} aria-hidden="true" />
        <span><strong>{label}</strong><small>{description}</small>
          <code>{models?.[mode] || (mode === value ? modelName : "Configured in Settings")}</code></span>
        {value === mode && <Check size={16} aria-hidden="true" />}
      </button>)}
      <p>Model availability depends on your downloads. Change preset models in Settings.</p>
    </section>}
  </div>;
}
