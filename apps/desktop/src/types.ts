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
// balanced defaults to deepseek-r1:7b - a reasoning model that thinks before answering.
// Speed uses a small fast model. Boost placeholder until cloud API key support lands.
export const MODEL_MAP: Record<ModelMode, string> = {
  speed: "qwen2.5:1.5b",
  balanced: "deepseek-r1:7b",
  boost: "deepseek-r1:7b",
};
