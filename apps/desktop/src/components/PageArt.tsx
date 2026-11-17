import type { ReactNode } from "react";
export type ArtKind = "projects" | "tasks" | "memory" | "media" | "knowledge" | "overview" | "settings";

/** Small flat illustrations for page banners and empty states. Colors come from theme tokens (see pages-ui.css). */
export function PageArt({ kind, className = "" }: { kind: ArtKind; className?: string }) {
  return <svg className={`page-art ${className}`} viewBox="0 0 200 140" aria-hidden="true" focusable="false">
    {ART[kind]}
  </svg>;
}

const ART: Record<ArtKind, ReactNode> = {
  projects: <>
    <rect x="22" y="40" width="156" height="86" rx="12" className="a-surface3" />
    <path d="M22 52a12 12 0 0 1 12-12h34l12 14h86a12 12 0 0 1 12 12v60a12 12 0 0 1-12 12H34a12 12 0 0 1-12-12z" className="a-surface2 a-line" />
    <rect x="36" y="74" width="60" height="38" rx="8" className="a-accent-dim" />
    <rect x="104" y="74" width="60" height="38" rx="8" className="a-warm-dim" />
    <rect x="44" y="84" width="30" height="5" rx="2.5" className="a-accent" />
    <rect x="44" y="95" width="42" height="4" rx="2" className="a-text3" />
    <rect x="112" y="84" width="26" height="5" rx="2.5" className="a-warm" />
    <rect x="112" y="95" width="40" height="4" rx="2" className="a-text3" />
    <circle cx="164" cy="30" r="14" className="a-accent" />
    <path d="M158 30h12M164 24v12" className="a-stroke-on" />
  </>,
  tasks: <>
    <rect x="44" y="18" width="112" height="108" rx="14" className="a-surface2 a-line" />
    <rect x="58" y="34" width="14" height="14" rx="4" className="a-accent" />
    <path d="m61 41 3 3 5-6" className="a-stroke-on" />
    <rect x="80" y="38" width="56" height="6" rx="3" className="a-text3" />
    <rect x="58" y="62" width="14" height="14" rx="4" className="a-accent" />
    <path d="m61 69 3 3 5-6" className="a-stroke-on" />
    <rect x="80" y="66" width="40" height="6" rx="3" className="a-text3" />
    <rect x="58" y="90" width="14" height="14" rx="4" className="a-surface3 a-line" />
    <rect x="80" y="94" width="62" height="6" rx="3" className="a-surface3" />
    <circle cx="156" cy="112" r="16" className="a-warm" />
    <path d="m148 112 6 6 11-12" className="a-stroke-on" />
  </>,
  memory: <>
    <path d="M60 70 100 40M100 40l46 30M60 70l40 44M146 70l-46 44M60 70h86" className="a-link" />
    <circle cx="100" cy="76" r="26" className="a-accent-dim a-line" />
    <circle cx="100" cy="76" r="11" className="a-accent" />
    <circle cx="60" cy="70" r="12" className="a-surface2 a-line" />
    <circle cx="100" cy="38" r="10" className="a-warm" />
    <circle cx="146" cy="70" r="13" className="a-cool" />
    <circle cx="100" cy="116" r="9" className="a-surface3 a-line" />
    <circle cx="170" cy="30" r="4" className="a-accent-dim" />
    <circle cx="34" cy="108" r="5" className="a-warm-dim" />
  </>,
  media: <>
    <rect x="30" y="30" width="110" height="80" rx="12" className="a-surface3" transform="rotate(-6 85 70)" />
    <rect x="50" y="24" width="124" height="88" rx="12" className="a-surface2 a-line" />
    <rect x="58" y="32" width="108" height="72" rx="8" className="a-accent-dim" />
    <circle cx="146" cy="50" r="9" className="a-warm" />
    <path d="m58 104 34-34 24 22 14-12 36 24z" className="a-accent" />
    <circle cx="164" cy="112" r="16" className="a-surface a-line" />
    <path d="m160 105 12 7-12 7z" className="a-text3" />
  </>,
  knowledge: <>
    <rect x="52" y="26" width="86" height="102" rx="10" className="a-surface3" transform="rotate(-5 95 77)" />
    <rect x="62" y="20" width="88" height="106" rx="10" className="a-surface2 a-line" />
    <rect x="74" y="36" width="44" height="6" rx="3" className="a-accent" />
    <rect x="74" y="52" width="64" height="4" rx="2" className="a-text3" />
    <rect x="74" y="64" width="58" height="4" rx="2" className="a-text3" />
    <rect x="74" y="76" width="64" height="4" rx="2" className="a-text3" />
    <rect x="74" y="92" width="40" height="14" rx="4" className="a-warm-dim" />
    <circle cx="148" cy="98" r="18" className="a-surface a-line" />
    <circle cx="148" cy="98" r="9" className="a-accent-dim a-stroke-accent" />
    <path d="m155 105 9 9" className="a-stroke-accent" />
  </>,
  overview: <>
    <rect x="24" y="30" width="72" height="50" rx="12" className="a-accent-dim" />
    <rect x="104" y="30" width="72" height="50" rx="12" className="a-surface2 a-line" />
    <rect x="24" y="88" width="72" height="30" rx="12" className="a-surface2 a-line" />
    <rect x="104" y="88" width="72" height="30" rx="12" className="a-warm-dim" />
    <circle cx="44" cy="52" r="8" className="a-accent" />
    <rect x="58" y="46" width="28" height="5" rx="2.5" className="a-accent" />
    <rect x="58" y="58" width="22" height="4" rx="2" className="a-text3" />
    <path d="M118 66l12-14 10 8 14-18" className="a-stroke-cool" />
  </>,
  settings: <>
    <rect x="28" y="28" width="144" height="84" rx="14" className="a-surface2 a-line" />
    <rect x="46" y="48" width="108" height="6" rx="3" className="a-surface3" />
    <rect x="46" y="48" width="64" height="6" rx="3" className="a-accent" />
    <circle cx="110" cy="51" r="9" className="a-surface a-line" />
    <rect x="46" y="74" width="108" height="6" rx="3" className="a-surface3" />
    <rect x="46" y="74" width="36" height="6" rx="3" className="a-warm" />
    <circle cx="82" cy="77" r="9" className="a-surface a-line" />
    <rect x="46" y="96" width="30" height="8" rx="4" className="a-accent-dim" />
  </>,
};
