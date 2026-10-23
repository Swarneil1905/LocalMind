// Shared types used across the application.

export type ModelMode = "speed" | "balanced" | "boost";

export interface WebSource {
  kind?: "web" | "knowledge";
  citation?: string;
  chunk_id?: string;
  source_id?: string;
  project_id?: string | null;
  file_path?: string;
  location?: string;
  title: string;
  url: string;
  snippet: string;
  published_at?: string | null;
  retrieved_at?: string;
}

export interface Message {
  id: string;
  role: "user" | "assistant";
  content: string;
  /** Chain-of-thought reasoning extracted from <think> tokens (qwen3, etc.) */
  thinking?: string;
  /** True while the model is still inside its <think> block (streaming only) */
  isThinking?: boolean;
  /** true when the message contains an error from the sidecar or Ollama */
  error?: boolean;
  cancelled?: boolean;
  sources?: WebSource[];
}

// Model names sent to the Python sidecar.
// Speed: fast sub-2B model for routing, memory extraction, project summaries.
// Balanced: main chat model with reasoning.
// Boost: placeholder until cloud API key support lands (Phase 2).
export const MODEL_MAP: Record<ModelMode, string> = {
  speed: "qwen2.5:1.5b",
  balanced: "qwen3:8b",
  boost: "qwen3:8b",
};
