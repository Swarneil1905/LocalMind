import { invoke } from "@tauri-apps/api/core";

export function qualityRequest<T>(method: "GET" | "POST", path: string, body?: unknown) {
  return invoke<T>("quality_request", { method, path: `/quality/${path}`, body: body ?? null });
}

export interface ResponsePreferences {
  length: "balanced" | "brief" | "detailed";
  depth: "standard" | "introductory" | "technical";
  tone: "neutral" | "warm" | "direct";
}
export const DEFAULT_PREFERENCES: ResponsePreferences = { length: "balanced", depth: "standard", tone: "neutral" };
export const FEEDBACK_CATEGORIES = {
  incorrect: "Incorrect", off_topic: "Off topic", too_long: "Too long",
  too_brief: "Too brief", unhelpful: "Not useful", useful: "Useful",
};
export interface FeedbackRecord { id: string; category: keyof typeof FEEDBACK_CATEGORIES; correction: string; updated_at: string }
