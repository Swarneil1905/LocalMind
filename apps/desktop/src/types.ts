// Shared types used across the application.

export type ModelMode = "speed" | "balanced" | "boost";

export interface Message {
  id: string;
  role: "user" | "assistant";
  content: string;
  /** true when the message contains an error from the sidecar or Ollama */
  error?: boolean;
}

// Model names sent to the Python sidecar.
// Boost falls back to Balanced in Phase 1 (no cloud API key support yet).
export const MODEL_MAP: Record<ModelMode, string> = {
  speed: "qwen2.5:1.5b",
  balanced: "qwen2.5:7b",
  boost: "qwen2.5:7b",
};
