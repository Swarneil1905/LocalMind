import { useCallback, useEffect, useRef, useState } from "react";
import { getVersion } from "@tauri-apps/api/app";
import { invoke } from "@tauri-apps/api/core";

export interface UpdateInfo {
  version: string;
  currentVersion: string;
  body: string | null;
}

export function useAppUpdater() {
  const [version, setVersion] = useState<string | null>(null);
  const [update, setUpdate] = useState<UpdateInfo | null>(null);
  const [phase, setPhase] = useState<"idle" | "checking" | "current" | "available" | "installing" | "error">("idle");
  const [error, setError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [dismissed, setDismissed] = useState(false);
  const busy = useRef(false);
  const checked = useRef(false);
  const mounted = useRef(false);

  const check = useCallback(async () => {
    if (busy.current) return;
    busy.current = true;
    checked.current = true;
    setPhase("checking");
    setError(null);
    setConfirming(false);
    try {
      const info = await invoke<UpdateInfo | null>("check_for_updates");
      if (!mounted.current) return;
      setUpdate(info);
      if (info) setVersion(info.currentVersion);
      setDismissed(false);
      setPhase(info ? "available" : "current");
    } catch {
      if (!mounted.current) return;
      setPhase("error");
      setError("Couldn’t check for updates. Check your internet connection and try again.");
    } finally {
      busy.current = false;
    }
  }, []);

  useEffect(() => {
    mounted.current = true;
    void getVersion().then(value => { if (mounted.current) setVersion(value); }).catch(() => {});
    const timer = setTimeout(() => { if (!checked.current) void check(); }, 5000);
    return () => { mounted.current = false; clearTimeout(timer); };
  }, [check]);

  async function install() {
    if (busy.current || !update || !confirming) return;
    busy.current = true;
    setPhase("installing");
    setConfirming(false);
    setError(null);
    try {
      await invoke("install_update", { expectedVersion: update.version });
      // The native command restarts the app after a successful installation.
    } catch {
      if (mounted.current) {
        setPhase("error");
        setError("Couldn’t install the update. Check for updates again or download the installer from Releases.");
      }
    } finally {
      busy.current = false;
    }
  }

  return { version, update, phase, error, confirming, dismissed, check, install,
    requestInstall: () => { if (!busy.current && phase === "available") setConfirming(true); },
    cancelInstall: () => setConfirming(false),
    dismiss: () => { setDismissed(true); setConfirming(false); },
  };
}

export type AppUpdater = ReturnType<typeof useAppUpdater>;
