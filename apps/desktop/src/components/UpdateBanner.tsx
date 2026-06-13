/**
 * UpdateBanner
 *
 * Checks for a new LocalMind release on mount (after first-run setup completes)
 * and shows a slim dismissable banner at the top of the window. Clicking
 * "Update & Restart" downloads the new version and relaunches the app.
 */

import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";

interface UpdateInfo {
  version: string;
  currentVersion: string;
  body: string | null;
}

type UpdateState =
  | { phase: "idle" }
  | { phase: "available"; info: UpdateInfo }
  | { phase: "downloading" }
  | { phase: "dismissed" };

export function UpdateBanner() {
  const [state, setState] = useState<UpdateState>({ phase: "idle" });

  useEffect(() => {
    // Check ~5 s after launch so it doesn't race with first-run setup
    const timer = setTimeout(() => {
      invoke<UpdateInfo | null>("check_for_updates")
        .then((info) => {
          if (info) setState({ phase: "available", info });
        })
        .catch(() => {
          // Silently ignore — updater errors shouldn't break the app
        });
    }, 5_000);
    return () => clearTimeout(timer);
  }, []);

  if (state.phase !== "available" && state.phase !== "downloading") return null;

  const isDownloading = state.phase === "downloading";
  const version = state.phase === "available" ? state.info.version : "";

  const handleUpdate = () => {
    setState({ phase: "downloading" });
    invoke("install_update").catch(() => {
      // If install fails, go back to available so the user can retry
      setState({
        phase: "available",
        info: (state as { phase: "downloading"; info?: UpdateInfo }).info ?? {
          version,
          currentVersion: "",
          body: null,
        },
      });
    });
  };

  return (
    <div
      style={{
        position: "fixed",
        top: 0,
        left: 0,
        right: 0,
        zIndex: 10000,
        backgroundColor: "var(--accent)",
        color: "#fff",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        gap: 12,
        padding: "7px 16px",
        fontSize: 13,
        fontWeight: 500,
        boxShadow: "0 2px 8px rgba(0,0,0,0.25)",
      }}
    >
      {isDownloading ? (
        <span>Downloading update… app will restart automatically.</span>
      ) : (
        <>
          <span>
            LocalMind {version} is available
          </span>
          <button
            onClick={handleUpdate}
            style={{
              background: "rgba(255,255,255,0.2)",
              border: "1px solid rgba(255,255,255,0.4)",
              borderRadius: 5,
              color: "#fff",
              fontSize: 12,
              fontWeight: 600,
              padding: "3px 10px",
              cursor: "pointer",
            }}
          >
            Update &amp; Restart
          </button>
          <button
            onClick={() => setState({ phase: "dismissed" })}
            aria-label="Dismiss"
            style={{
              background: "none",
              border: "none",
              color: "rgba(255,255,255,0.7)",
              fontSize: 16,
              cursor: "pointer",
              lineHeight: 1,
              padding: "0 2px",
            }}
          >
            ×
          </button>
        </>
      )}
    </div>
  );
}
