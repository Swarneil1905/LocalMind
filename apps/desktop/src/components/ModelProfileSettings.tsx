import { invoke } from "@tauri-apps/api/core";
import { useEffect, useState } from "react";
import "./model-profile-settings.css";

interface Profile {
  model: string;
  preset: string;
  digest: string;
  context_limit: number;
  minimum_input_budget?: number;
  settings_notice?: string;
  context_window: number;
  response_tokens: number;
  temperature: number;
  top_p: number;
  top_k: number;
  think: boolean | string | null;
  thinking_values: (boolean | string)[];
}

type NumericField = "context_window" | "response_tokens" | "temperature" | "top_p" | "top_k";
type ProfileDraft = Omit<Profile, NumericField> & Record<NumericField, number | "">;

export function ModelProfileSettings({
  speedModel, balancedModel,
}: { speedModel: string; balancedModel: string }) {
  const [preset, setPreset] = useState("balanced");
  const model = preset === "speed" ? speedModel : balancedModel;
  return (
    <details className="model-profile-settings">
      <summary>Response settings</summary>
      <p className="page-footnote">Adjust context and thinking for each chat mode. Changes apply to your next message.</p>
      <label className="model-profile-field">
        Chat mode
        <select className="page-select" value={preset} onChange={(e) => setPreset(e.target.value)}>
          <option value="speed">Speed</option>
          <option value="balanced">Balanced</option>
        </select>
      </label>
      {/* Remount on identity changes so late responses cannot replace another model's settings. */}
      <ProfileEditor key={`${model}:${preset}`} model={model} preset={preset} />
    </details>
  );
}

function ProfileEditor({ model, preset }: { model: string; preset: string }) {
  const [profile, setProfile] = useState<ProfileDraft | null>(null);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [retry, setRetry] = useState(0);
  const minimumInputBudget = profile?.minimum_input_budget ?? 3072;
  useEffect(() => {
    let active = true;
    invoke<Profile>("model_profile", { model, preset, changes: null })
      .then((value) => {
        if (!value?.digest) throw new Error("Model settings are unavailable.");
        if (active) setProfile(value);
      })
      .catch((reason: unknown) => {
        if (active) setError(String(reason));
      })
      .finally(() => { if (active) setBusy(false); });
    return () => { active = false; };
  }, [model, preset, retry]);

  async function save(restore = false) {
    if (!profile || busy) return;
    if (!restore && [profile.context_window, profile.response_tokens, profile.temperature, profile.top_p, profile.top_k]
      .some((value) => value === "" || !Number.isFinite(value))) {
      setError("Enter a number in each response setting.");
      return;
    }
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const result = restore
        ? await invoke<Profile>("restore_model_profile", { model, preset })
        : await invoke<Profile>("model_profile", {
          model, preset, changes: {
            context_window: profile.context_window,
            response_tokens: profile.response_tokens,
            temperature: profile.temperature,
            top_p: profile.top_p,
            top_k: profile.top_k,
            think: profile.think,
          },
        });
      setProfile(result);
      setNotice(restore ? "Previous settings restored." : "Response settings saved.");
    } catch (reason) {
      setError(String(reason));
    } finally {
      setBusy(false);
    }
  }
  const field = (key: NumericField,
    label: string, min: number, max: number, step = 1) => profile && (
    <label className="model-profile-field">
      {label}
      <input className="page-input" type="number" required min={min} max={max} step={step}
        value={profile[key]} disabled={busy}
        onChange={(e) => {
          setNotice("");
          setProfile({ ...profile, [key]: e.target.value === "" ? "" : Number(e.target.value) });
        }} />
    </label>
  );
  return (
    <div>
      {error && <p className="page-notice error" role="alert">{error}</p>}
      {!profile && (busy ? <p role="status">Loading model settings…</p> :
        <button className="page-button" onClick={() => {
          setError(""); setBusy(true); setRetry((value) => value + 1);
        }}>Retry model settings</button>)}
      {profile && (
        <form onSubmit={(e) => { e.preventDefault(); void save(); }}>
          <p className="page-footnote">{model} · Installed revision {profile.digest.replace("sha256:", "").slice(0, 12)}</p>
          {profile.settings_notice && <p className="page-notice" role="status">{profile.settings_notice}</p>}
          <div className="model-profile-grid">
            {field("context_window", "Context capacity (tokens)", minimumInputBudget + 128, profile.context_limit)}
            {field("response_tokens", "Maximum response (tokens)", 128, Math.min(8192, Number(profile.context_window) - minimumInputBudget))}
            {field("temperature", "Temperature", 0, 2, 0.05)}
            {field("top_p", "Top-p", 0.01, 1, 0.01)}
            {field("top_k", "Top-k", 1, 100)}
            <label className="model-profile-field">
              Thinking
              <select className="page-select" disabled={busy || !profile.thinking_values.length}
                value={JSON.stringify(profile.think)} onChange={(e) => {
                  setProfile({ ...profile, think: JSON.parse(e.target.value) });
                  setNotice("");
                }}>
                <option value="null">Model default</option>
                {profile.thinking_values.map((value) => (
                  <option key={JSON.stringify(value)} value={JSON.stringify(value)}>
                    {value === true ? "On" : value === false ? "Off" : value}
                  </option>
                ))}
              </select>
            </label>
          </div>
          <p className="page-footnote">Larger contexts use more memory. Thinking can delay the first answer. At least {minimumInputBudget.toLocaleString()} tokens are reserved for your message, instructions, and context. Unsupported thinking controls remain unavailable.</p>
          <div className="settings-save">
            <button className="page-button primary" disabled={busy} type="submit">{busy ? "Saving…" : "Save response settings"}</button>
            <button className="page-button" disabled={busy} type="button" onClick={() => void save(true)}>Restore previous settings</button>
          </div>
          <p role="status">{notice}</p>
        </form>
      )}
    </div>
  );
}
