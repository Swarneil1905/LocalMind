import { invoke } from "@tauri-apps/api/core";
import { useCallback, useEffect, useRef, useState } from "react";

interface MiniState { active: boolean; pinned: boolean }

export function useMiniWindow() {
  const [state, setState] = useState<MiniState>({ active: false, pinned: false });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const pending = useRef(false);
  const version = useRef(0);
  useEffect(() => {
    let disposed = false;
    const initial = version.current;
    void invoke<MiniState>("get_mini_state").then((value) => {
      if (!disposed && initial === version.current && value) setState(value);
    }).catch(() => {});
    return () => { disposed = true; };
  }, []);
  const change = useCallback(async (command: string, args?: Record<string, unknown>) => {
    if (pending.current) return false;
    pending.current = true;
    version.current++;
    setBusy(true);
    setError(null);
    try {
      const value = await invoke<MiniState | null>(command, args);
      if (value) setState(value);
      return true;
    } catch {
      setError("Couldn't change the window. Your conversation is still here. Try again.");
      return false;
    } finally {
      pending.current = false;
      setBusy(false);
    }
  }, []);
  return { ...state, busy, error,
    setCompact: useCallback((compact: boolean) => change("set_mini_mode", { compact }), [change]),
    setPinned: useCallback((pinned: boolean) => change("pin_mini_window", { pinned }), [change]),
    minimize: useCallback(() => change("minimize_mini_window"), [change]),
  };
}
