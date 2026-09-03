// Tracks readiness of the Python sidecar (the FastAPI service that actually
// talks to Ollama/the models). On launch, Rust starts Ollama, then spawns the
// sidecar, then health-checks it - that last step can take several seconds.
// Until it succeeds, every chat/memory/etc. command fails with
// "Sidecar not ready yet", even though Ollama itself may already show as
// running. This hook lets the UI gate on the sidecar specifically instead of
// only on Ollama, so the composer can't be used into a guaranteed error.

import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useEffect, useState } from "react";

export type SidecarStatus = "starting" | "ready" | "failed";

export function useSidecarStatus(): { status: SidecarStatus; error: string | null } {
  const [status, setStatus] = useState<SidecarStatus>("starting");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    let unlistenReady: (() => void) | undefined;
    let unlistenFailed: (() => void) | undefined;

    invoke<boolean>("get_sidecar_status")
      .then((ready) => {
        if (!cancelled && ready === true) setStatus("ready");
      })
      .catch(() => {});

    listen("sidecar-ready", () => {
      if (!cancelled) setStatus("ready");
    }).then((fn) => {
      if (cancelled) fn();
      else unlistenReady = fn;
    });

    listen<string>("sidecar-failed", (event) => {
      if (!cancelled) {
        setStatus("failed");
        setError(typeof event.payload === "string" ? event.payload : "Unknown error");
      }
    }).then((fn) => {
      if (cancelled) fn();
      else unlistenFailed = fn;
    });

    return () => {
      cancelled = true;
      unlistenReady?.();
      unlistenFailed?.();
    };
  }, []);

  return { status, error };
}
