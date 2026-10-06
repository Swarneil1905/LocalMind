import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useCallback, useEffect, useRef, useState } from "react";

export type SidecarStatus = "starting" | "ready" | "failed";
interface RuntimeStatus {
  status: SidecarStatus;
  error: string | null;
}
export function useSidecarStatus() {
  const [runtime, setRuntime] = useState<RuntimeStatus>({
    status: "starting",
    error: null,
  });
  const mounted = useRef(false);
  const revision = useRef(0);
  useEffect(() => {
    let cancelled = false;
    mounted.current = true;
    const offs: (() => void)[] = [];
    const keep = (off: () => void) => {
      if (cancelled) off();
      else offs.push(off);
    };
    const poll = async () => {
      const version = revision.current;
      try {
        const current = await invoke<RuntimeStatus>("get_runtime_status");
        if (!cancelled && version === revision.current && current?.status)
          setRuntime(current);
      } catch {
        // Older development hosts expose only boolean readiness.
        try {
          const ready = await invoke<boolean>("get_sidecar_status");
          if (!cancelled && version === revision.current && ready)
            setRuntime({ status: "ready", error: null });
        } catch {
          /* Retry on the next poll. */
        }
      }
    };
    void poll();
    const timer = setInterval(() => void poll(), 2000);
    void listen("sidecar-ready", () => {
      if (!cancelled) {
        revision.current++;
        setRuntime({ status: "ready", error: null });
      }
    })
      .then(keep)
      .catch(() => {});
    void listen<string>("sidecar-failed", (event) => {
      if (!cancelled) {
        revision.current++;
        setRuntime({ status: "failed", error: String(event.payload) });
      }
    })
      .then(keep)
      .catch(() => {});
    return () => {
      mounted.current = false;
      cancelled = true;
      clearInterval(timer);
      offs.forEach((off) => off());
    };
  }, []);
  const restart = useCallback(async () => {
    revision.current++;
    setRuntime({ status: "starting", error: null });
    try {
      await invoke("restart_sidecar");
    } catch (error) {
      if (mounted.current)
        setRuntime({ status: "failed", error: String(error) });
    }
  }, []);
  return { ...runtime, restart };
}
