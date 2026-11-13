// Shared types used across the application.

export type ModelMode = "speed" | "balanced" | "boost";

export interface WebSource {
  kind?: "web" | "knowledge" | "document";
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
  /** Failure notice kept separate from any partial answer. */
  errorMessage?: string;
  cancelled?: boolean;
  sources?: WebSource[];
  diagnostics?: ResponseDiagnostics;
  attachments?: import("./lib/attachments").ChatAttachment[];
  attachmentDraftId?: string;
  conversation_id?: string;
  usage?: { input_tokens: number | null; output_tokens: number | null; cost_usd: number | null; cost_is_estimate?: boolean };
}

export interface ResponseDiagnostics {
  client_total_seconds?: number;
  request_seconds?: number;
  request_first_answer_seconds?: number | null;
  client_first_answer_seconds?: number | null;
  phase_seconds?: Record<string, number>;
  model_load_seconds?: number | null;
  prompt_eval_seconds?: number | null;
  outcome?: "completed" | "failed" | "cancelled";
  model: string;
  digest?: string | null;
  preset?: string | null;
  context_window: number | null;
  response_tokens: number | null;
  history_dropped: number;
  references_dropped: number;
  references_shortened: number;
  total_seconds: number | null;
  first_token_seconds: number | null;
  output_tokens: number | null;
  input_tokens: number | null;
  tokens_per_second: number | null;
  stop_reason: string;
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
