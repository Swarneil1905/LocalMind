import type { AppUpdater } from "../hooks/useAppUpdater";
import { useState } from "react";
import { openUrl } from "@tauri-apps/plugin-opener";

const RELEASES_URL = "https://github.com/Swarneil1905/LocalMind/releases/latest";

export function UpdateActions({ updater }: { updater: AppUpdater }) {
  if (updater.phase === "installing") return <p role="status">Downloading and installing. LocalMind will restart when ready.</p>;
  if (updater.confirming) return (
    <div>
      <p>Finish any active responses and save unsent drafts before continuing. LocalMind will close and restart.</p>
      <button className="page-button primary" onClick={() => void updater.install()}>Install and restart</button>{" "}
      <button className="secondary-button" onClick={updater.cancelInstall}>Cancel</button>
    </div>
  );
  return (
    <div>
      <button className="secondary-button" disabled={updater.phase === "checking"} onClick={() => void updater.check()}>
        {updater.phase === "checking" ? "Checking…" : "Check for updates"}
      </button>{" "}
      {updater.phase === "available" && <button className="page-button primary" onClick={updater.requestInstall}>Update LocalMind</button>}
    </div>
  );
}

export function UpdateSettings({ updater }: { updater: AppUpdater }) {
  const [linkError, setLinkError] = useState(false);
  async function openReleases() {
    setLinkError(false);
    try { await openUrl(RELEASES_URL); } catch { setLinkError(true); }
  }
  return (
    <section aria-label="App updates">
      <div className="settings-section-heading">
        <h2>Updates</h2>
        <p>Install published LocalMind releases. Checking for updates works even when the local AI service is unavailable.</p>
      </div>
      <p>Installed version: {updater.version ?? "Unavailable"}</p>
      {updater.phase === "current" && <p role="status">No newer release is available.</p>}
      {updater.update && <p>Available version: {updater.update.version}</p>}
      {updater.error && <p role="alert">{updater.error}</p>}
      <UpdateActions updater={updater} />
      {updater.update?.body && <details className="update-release-notes"><summary>Release notes</summary><p>{updater.update.body}</p></details>}
      <p className="page-footnote">Updates are checked automatically when LocalMind opens. Downloads come from GitHub and are verified before installation. Code pushed to GitHub is only available here after a new release is published.</p>
      <a href={RELEASES_URL} onClick={event => { event.preventDefault(); void openReleases(); }}>Open Releases for a manual installer</a>
      {linkError && <p role="alert">Couldn’t open your browser. Copy this address: {RELEASES_URL}</p>}
    </section>
  );
}
