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
    let cancelled = false;
    const refresh = () =>
      void invoke<OllamaStatus>("get_ollama_status")
        .then((s) => {
          if (!cancelled) setStatus(s);
        })
        .catch(() => {});
    refresh();
    const timer = setInterval(refresh, 10000);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, []);

  return status;
}
