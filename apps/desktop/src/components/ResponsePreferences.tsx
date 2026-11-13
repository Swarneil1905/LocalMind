import { useEffect, useState } from "react";
import { DEFAULT_PREFERENCES, qualityRequest, type ResponsePreferences as Preferences } from "../lib/quality";
import { FeedbackSettings } from "./ResponseFeedback";

export function ResponsePreferences() {
  const [value, setValue] = useState<Preferences | null>(null);
  const [busy, setBusy] = useState(false), [error, setError] = useState(""), [notice, setNotice] = useState("");
  const [reload, setReload] = useState(0);
  useEffect(() => {
    let active = true;
    qualityRequest<Preferences>("GET", "preferences").then(result => { if (active) { setError(""); setValue(result); } })
      .catch(() => { if (active) setError("Couldn't load response preferences."); });
    return () => { active = false; };
  }, [reload]);
  async function save(next: Preferences) {
    if (busy) return;
    setBusy(true); setError(""); setNotice("");
    try { setValue(await qualityRequest<Preferences>("POST", "preferences", next)); setNotice("Preferences saved for your next message."); }
    catch { setError("Couldn't save preferences. Your previous choices are still active."); }
    finally { setBusy(false); }
  }
  return <section>
    <div className="settings-section-heading"><h2>Response preferences</h2><p>Choose a default style. A specific request in chat takes precedence. Model support varies.</p></div>
    {error && <p role="alert">{error} {!value && <button onClick={() => { setError(""); setReload(reload + 1); }}>Retry preferences</button>}</p>}
    {!value && !error && <p role="status">Loading preferences…</p>}
    {value && <form onSubmit={event => { event.preventDefault(); void save(value); }}>
      <div className="model-profile-grid">
        <label className="model-profile-field">Answer length<select className="page-select" disabled={busy} value={value.length} onChange={e => setValue({ ...value, length: e.target.value as Preferences["length"] })}>
          <option value="balanced">Balanced</option><option value="brief">Brief</option><option value="detailed">Detailed</option></select></label>
        <label className="model-profile-field">Explanation depth<select className="page-select" disabled={busy} value={value.depth} onChange={e => setValue({ ...value, depth: e.target.value as Preferences["depth"] })}>
          <option value="standard">Standard</option><option value="introductory">Introductory</option><option value="technical">Technical</option></select></label>
        <label className="model-profile-field">Tone<select className="page-select" disabled={busy} value={value.tone} onChange={e => setValue({ ...value, tone: e.target.value as Preferences["tone"] })}>
          <option value="neutral">Neutral</option><option value="warm">Warm</option><option value="direct">Direct</option></select></label>
      </div>
      <button className="page-button primary" disabled={busy}>Save preferences</button>{" "}
      <button className="page-button" type="button" disabled={busy} onClick={() => void save(DEFAULT_PREFERENCES)}>Reset preferences</button>
    </form>}
    <p role="status">{notice}</p>
    <p className="page-footnote">Stored in this profile. Cloud chat includes these style instructions in its approved request. Changing preferences does not train or replace the model.</p>
    <FeedbackSettings />
  </section>;
}
