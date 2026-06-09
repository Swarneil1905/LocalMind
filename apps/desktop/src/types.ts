// Shared types used across the application.

export type ModelMode = "speed" | "balanced" | "boost";

export interface Message {
  id: string;
  role: "user" | "assistant";
  content: string;
  /** Chain-of-thought reasoning extracted from DeepSeek R1 <think> tokens */
  thinking?: string;
  /** True while the model is still inside its <think> block (streaming only) */
  isThinking?: boolean;
  /** true when the message contains an error from the sidecar or Ollama */
  error?: boolean;
}

// Model names sent to the Python sidecar.
// Speed: fast sub-2B active-param model for routing, memory extraction, project summaries.
// Balanced: main chat model with reasoning. deepseek-r1:7b as fallback default.
// Boost: placeholder until cloud API key support lands (Phase 2).
export const MODEL_MAP: Record<ModelMode, string> = {
  speed: "maternion/lfm2.5",
  balanced: "deepseek-r1:7b",
  boost: "deepseek-r1:7b",
};
