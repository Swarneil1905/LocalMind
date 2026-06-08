// Fetches Ollama status from Rust via the get_ollama_status Tauri command.
// Called once on app mount; the sidebar uses this to show the live dot.

import { invoke } from "@tauri-apps/api/core";
import { useEffect, useState } from "react";

export interface OllamaModel {
  name: string;
  size: number;
}

export interface GpuInfo {
  name: string;
  vramTotalMib: number;
  vramFreeMib: number;
}

export interface OllamaStatus {
  running: boolean;
  version: string | null;
  models: OllamaModel[];
  gpu: GpuInfo | null;
}

export function useOllama(): OllamaStatus | null {
  const [status, setStatus] = useState<OllamaStatus | null>(null);

  useEffect(() => {
    invoke<OllamaStatus>("get_ollama_status")
      .then((s) => setStatus(s))
      .catch((e) => console.error("[useOllama] failed to get status:", e));
  }, []);

  return status;
}
