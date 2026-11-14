import { Download, X } from "lucide-react";
import type { AppUpdater } from "../hooks/useAppUpdater";
import { UpdateActions } from "./UpdateSettings";

export function UpdateBanner({ updater }: { updater: AppUpdater }) {
  if (updater.dismissed || (!updater.update && !updater.error)) return null;
  return (
    <div className="app-notice update-notice" aria-label="App update">
      <Download size={17} aria-hidden="true" />
      <span role={updater.error ? "alert" : "status"}>
        {updater.error ?? `LocalMind ${updater.update?.version} is available.`}
      </span>
      <UpdateActions updater={updater} />
      {updater.phase !== "installing" && <button className="icon-button" aria-label="Dismiss update" onClick={updater.dismiss}><X size={16} /></button>}
    </div>
  );
}
