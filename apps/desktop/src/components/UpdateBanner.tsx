import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { Download, X } from "lucide-react";

interface UpdateInfo {
  version: string;
  currentVersion: string;
  body: string | null;
}
export function UpdateBanner() {
  const [update, setUpdate] = useState<UpdateInfo | null>(null);
  const [downloading, setDownloading] = useState(false);
  const [error, setError] = useState(false);
  useEffect(() => {
    let disposed = false;
    const timer = setTimeout(() => {
      void invoke<UpdateInfo | null>("check_for_updates")
        .then((info) => {
          if (!disposed) setUpdate(info);
        })
        .catch(() => {});
    }, 5000);
    return () => {
      disposed = true;
      clearTimeout(timer);
    };
  }, []);
  if (!update) return null;
  async function install() {
    setDownloading(true);
    setError(false);
    try {
      await invoke("install_update");
    } catch {
      setError(true);
      setDownloading(false);
    }
  }
  return (
    <div className="app-notice update-notice" role="status">
      <Download size={17} />
      <span>
        {downloading
          ? "Downloading update. LocalMind will restart when it is ready."
          : error
            ? "The update could not be installed. Your current version is still available."
            : `LocalMind ${update.version} is available.`}
      </span>
      {!downloading && (
        <>
          <button className="secondary-button" onClick={() => void install()}>
            {error ? "Retry update" : "Update & restart"}
          </button>
          <button
            className="icon-button"
            aria-label="Dismiss update"
            onClick={() => setUpdate(null)}
          >
            <X size={16} />
          </button>
        </>
      )}
    </div>
  );
}
