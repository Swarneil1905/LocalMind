import { invoke } from "@tauri-apps/api/core";

export type CloudProviderId = "openai" | "anthropic" | "gemini" | "deepseek" | "openrouter" | "groq" | "mistral" | "xai";
export type ProviderId = "ollama" | CloudProviderId;
export type CloudScope = "messages" | "memory" | "knowledge" | "attachments" | "connectors" | "web";
export interface CloudProvider {
  id: CloudProviderId;
  label: string;
  connected: boolean;
  credential_error?: string;
  model: string;
  vision: boolean;
  input_price_per_million: number | null;
  output_price_per_million: number | null;
  approved_scopes: CloudScope[];
  consent_id?: string | null;
  consent_model?: string | null;
}
export interface ProviderStatus {
  local_only: boolean;
  monthly_limit_usd: number | null;
  usage: { month: string; spent_usd: number; reserved_usd: number; unknown_requests: number; account_wide_spend: null };
  providers: CloudProvider[];
}
export interface ProviderModel { id: string; name: string; vision: boolean | null }

export function providerRequest<T>(method: string, path: string, body?: unknown): Promise<T> {
  return invoke<T>("provider_request", { method, path, body: body ?? null });
}

export function providerError(cause: unknown): string {
  const text = String(cause);
  try {
    const parsed = JSON.parse(text);
    if (typeof parsed?.detail?.message === "string") return parsed.detail.message;
    if (typeof parsed?.message === "string") return parsed.message;
  } catch { /* The bridge also returns plain connection errors. */ }
  return text;
}

export const SCOPE_LABELS: Record<CloudScope, string> = {
  messages: "This message and the previous messages in this chat",
  memory: "Relevant saved memories",
  knowledge: "Relevant excerpts from your indexed documents",
  attachments: "Images attached to this chat",
  connectors: "Relevant content from connected apps",
  web: "Web search queries and retrieved excerpts",
};
