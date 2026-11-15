import openai from "../assets/brands/openai.svg";
import claude from "../assets/brands/claude-color.svg";
import gemini from "../assets/brands/gemini-color.svg";
import deepseek from "../assets/brands/deepseek-color.svg";
import openrouter from "../assets/brands/openrouter.svg";
import groq from "../assets/brands/groq.svg";
import mistral from "../assets/brands/mistral-color.svg";
import xai from "../assets/brands/xai.svg";
import ollama from "../assets/brands/ollama.svg";
import gmail from "../assets/brands/gmail.svg";
import whatsapp from "../assets/brands/whatsapp.svg";
import imessage from "../assets/brands/imessage.svg";

// Full-color files render as images. One-color files are drawn as a mask so they take
// the theme's text color (or the brand color) in both light and dark mode.
const LOGOS: Record<string, { src: string; tint?: string }> = {
  openai: { src: openai, tint: "var(--text)" },
  anthropic: { src: claude },
  gemini: { src: gemini },
  deepseek: { src: deepseek },
  openrouter: { src: openrouter, tint: "var(--text)" },
  groq: { src: groq, tint: "#f55036" },
  mistral: { src: mistral },
  xai: { src: xai, tint: "var(--text)" },
  ollama: { src: ollama, tint: "var(--text)" },
  gmail: { src: gmail, tint: "#ea4335" },
  whatsapp_web: { src: whatsapp, tint: "#25d366" },
  imessage: { src: imessage, tint: "#34da50" },
};

export function BrandLogo({ id, label, size = 22 }: { id: string; label: string; size?: number }) {
  const logo = LOGOS[id];
  if (!logo) return <span className="brand-logo brand-fallback" style={{ width: size, height: size }} aria-hidden="true">{label.slice(0, 1)}</span>;
  if (!logo.tint) return <img className="brand-logo" src={logo.src} width={size} height={size} alt="" aria-hidden="true" />;
  return <span className="brand-logo" aria-hidden="true" style={{ width: size, height: size, backgroundColor: logo.tint,
    WebkitMaskImage: `url("${logo.src}")`, maskImage: `url("${logo.src}")` }} />;
}

export const PROVIDER_SITES: Record<string, string> = {
  openai: "api.openai.com", anthropic: "api.anthropic.com", gemini: "generativelanguage.googleapis.com",
  deepseek: "api.deepseek.com", openrouter: "openrouter.ai", groq: "api.groq.com", mistral: "api.mistral.ai", xai: "api.x.ai",
};
