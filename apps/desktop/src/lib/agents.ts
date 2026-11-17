import type { ProviderId } from "./providers";

// Cloud model ids name their maker. Anything run through Ollama keeps the Ollama mark.
const MAKERS: [RegExp, ProviderId][] = [
  [/^(gpt|chatgpt|o\d)/i, "openai"],
  [/^claude/i, "anthropic"],
  [/^gemini/i, "gemini"],
  [/^grok/i, "xai"],
];

/** Which logo an answer gets: the model's maker for a cloud id, else the active provider. */
export function agentFor(model: string | undefined, active: ProviderId): ProviderId {
  if (active === "ollama" || !model) return active;
  return MAKERS.find(([pattern]) => pattern.test(model))?.[1] ?? active;
}

// Provider lists also hold speech, embedding, image and moderation models that cannot chat.
const NOT_CHAT = /(tts|embed|imagen|image|veo|audio|aqa|live|moderation|whisper|transcribe|dall-e|davinci|babbage|realtime|robotics)/i;
export const isChatModel = (id: string) => !NOT_CHAT.test(id);
