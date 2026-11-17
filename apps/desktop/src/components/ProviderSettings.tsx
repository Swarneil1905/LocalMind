import { useState } from "react";
import { useProviders } from "../hooks/useProviders";
import { providerError, providerRequest, type CloudProvider, type ProviderModel } from "../lib/providers";
import { isChatModel } from "../lib/agents";
import { BrandLogo, PROVIDER_SITES } from "./BrandLogo";
import "./providers.css";

export function ProviderSettings() {
  const { status, error, refresh } = useProviders();
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);
  const [limitDraft, setLimit] = useState<string | null>(null);
  const [adding, setAdding] = useState<CloudProvider | null>(null);
  const limit = limitDraft ?? status?.monthly_limit_usd?.toString() ?? "";
  const changed = async () => {
    await refresh();
    window.dispatchEvent(new Event("localmind-providers-changed"));
  };
  const policy = async (body: unknown) => {
    setBusy(true); setNotice("");
    try { await providerRequest("PATCH", "/providers/policy", body); await changed(); }
    catch (cause) { setNotice(providerError(cause)); }
    finally { setBusy(false); }
  };
  if (!status) return <section><h2>Cloud models</h2><p role={error ? "alert" : "status"}>{error || "Loading cloud model settings…"}</p><button className="secondary-button" onClick={() => void refresh()}>Retry</button></section>;
  const connected = status.providers.filter(provider => provider.connected);
  const available = status.providers.filter(provider => !provider.connected);
  const limitInvalid = limit !== "" && (!Number.isFinite(Number(limit)) || Number(limit) < 0);
  return <section className="provider-settings">
    <div className="settings-section-heading"><h2>Cloud models</h2>
      <p>Bring your own API key for a stronger model. You pay the provider directly, and your key stays in this computer’s credential store.</p></div>

    <div className="setting-row">
      <div><strong id="local-only-label">Keep chats on this device</strong>
        <p>While this is on, nothing goes to a cloud model. It does not turn off web search or connected apps.</p></div>
      <label className="switch"><input type="checkbox" role="switch" aria-labelledby="local-only-label" checked={status.local_only} disabled={busy}
        onChange={event => void policy({ local_only: event.target.checked })} /><span aria-hidden="true" /></label>
    </div>

    <div className="setting-row">
      <div><strong>Monthly spending limit</strong>
        <p>Spent ${status.usage.spent_usd.toFixed(2)} this month{status.usage.reserved_usd > 0 ? `, $${status.usage.reserved_usd.toFixed(2)} on hold` : ""}. This counts requests made here only. Add a limit at your provider too.</p></div>
      <div className="inline-field">
        <input type="number" min="0" step="0.01" aria-label="Monthly API limit" value={limit} onChange={event => setLimit(event.target.value)} placeholder="No limit" />
        <button className="secondary-button" disabled={busy || limitInvalid}
          onClick={() => void policy({ monthly_limit_usd: limit === "" ? null : Number(limit) })}>Save limit</button>
      </div>
    </div>
    {notice && <p role="alert">{notice}</p>}

    <div className="provider-group">
      <h3>Connected</h3>
      <div className="provider-list">
        <div className="provider-card provider-static">
          <div className="provider-summary"><span className="provider-name"><BrandLogo id="ollama" label="Ollama" />Ollama<small>On this device</small></span>
            <span className="provider-state is-ready">Always available</span></div>
        </div>
        {connected.map(provider => <ProviderAccount key={provider.id} provider={provider} localOnly={status.local_only} onChange={changed} />)}
      </div>
    </div>

    {available.length > 0 && <div className="provider-group">
      <h3>Add a provider</h3>
      <p className="muted">Pick one and paste its API key. You can add more later.</p>
      <div className="provider-grid">
        {available.map(provider => <button type="button" key={provider.id} className="provider-tile" onClick={() => setAdding(provider)}
          aria-label={`Connect ${provider.label}`}>
          <BrandLogo id={provider.id} label={provider.label} size={28} />
          <span><strong>{provider.label}</strong><small>{PROVIDER_SITES[provider.id] ?? ""}</small></span>
          <span className="provider-tile-add" aria-hidden="true">+</span>
        </button>)}
      </div>
    </div>}
    {adding && <ConnectDialog provider={adding} onClose={() => setAdding(null)} onDone={async () => { setAdding(null); await changed(); }} />}
  </section>;
}

function ProviderAccount({ provider, localOnly, onChange }: { provider: CloudProvider; localOnly: boolean; onChange: () => Promise<void> }) {
  const [key, setKey] = useState("");
  const [model, setModel] = useState(provider.model);
  const [vision, setVision] = useState(provider.vision);
  const [inputPrice, setInputPrice] = useState(provider.input_price_per_million?.toString() ?? "");
  const [outputPrice, setOutputPrice] = useState(provider.output_price_per_million?.toString() ?? "");
  const [models, setModels] = useState<ProviderModel[]>([]);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const path = `/providers/${provider.id}`;
  const run = async (work: () => Promise<unknown>, success: string) => {
    setBusy(true); setMessage("");
    try { await work(); setMessage(success); await onChange(); }
    catch (cause) { setMessage(providerError(cause)); }
    finally { setBusy(false); }
  };
  const selectModel = (id: string) => {
    setModel(id);
    setVision(models.find(item => item.id === id)?.vision === true);
    setInputPrice(""); setOutputPrice("");
  };
  const state = !provider.connected ? "Not connected" : provider.model ? `Ready · ${provider.model}` : "Key saved · choose a model";
  const pricesInvalid = [inputPrice, outputPrice].some(value => value !== "" && (!Number.isFinite(Number(value)) || Number(value) < 0));
  return <details className="provider-card" open={provider.connected && !provider.model}>
    <summary><span className="provider-name"><BrandLogo id={provider.id} label={provider.label} />{provider.label}</span>
      <span className={`provider-state ${provider.connected && provider.model ? "is-ready" : ""}`}>{state}</span></summary>
    <fieldset disabled={busy}>
      <legend className="sr-only">{provider.label} settings</legend>
      {provider.credential_error && <p role="alert">{provider.credential_error}</p>}

      <div className="provider-step"><h4>1. API key</h4>
        <div className="inline-field">
          <input type="password" value={key} autoComplete="off" spellCheck={false} aria-label={`${provider.label} API key`} onChange={event => setKey(event.target.value)} placeholder={provider.connected ? "Paste a new key to replace it" : "Paste your API key"} />
          <button type="button" className="secondary-button" disabled={!key.trim()}
            onClick={() => { const entered = key; setKey(""); void run(() => providerRequest("PUT", `${path}/credential`, { key: entered }), "Key saved in your credential store."); }}>Save key</button>
          {provider.connected && <button type="button" className="secondary-button" onClick={() => void run(() => providerRequest("DELETE", `${path}/credential`), "Disconnected. The saved key and permission are gone.")}>Disconnect</button>}
        </div>
      </div>

      <div className="provider-step"><h4>2. Model</h4>
        {localOnly && provider.connected && <p className="provider-blocked" role="note">“Keep chats on this device” is on, so LocalMind can’t reach {provider.label} to list models. Turn it off above, then load the list.</p>}
        <div className="inline-field">
          <input value={model} aria-label={`${provider.label} model ID`} onChange={event => selectModel(event.target.value)} placeholder="Model ID, or load the list" />
          <button type="button" className="secondary-button" disabled={!provider.connected || localOnly} onClick={() => void run(async () => {
            const response = await providerRequest<{ models: ProviderModel[]; truncated: boolean }>("GET", `${path}/models`);
            setModels(response.models.filter(item => isChatModel(item.id) || item.id === model));
            if (response.truncated) throw new Error("Only some models are shown. Type the model ID if yours is missing.");
          }, "Models loaded.")}>Load available models</button>
        </div>
        {models.length > 0 && <select aria-label={`${provider.label} available model`} value={model} onChange={event => selectModel(event.target.value)}>
          <option value="">Choose a model</option>{models.map(item => <option value={item.id} key={item.id}>{item.name}{item.vision === true ? " · Images" : item.vision === false ? " · Text" : ""}</option>)}
        </select>}
      </div>

      <details className="provider-advanced"><summary>Images and prices</summary>
        <label className="switch-line"><span className="switch"><input type="checkbox" role="switch" checked={vision} onChange={event => setVision(event.target.checked)} /><span aria-hidden="true" /></span>
          This model can read images</label>
        <p className="muted">Turn this on only if the provider’s docs say so.</p>
        <div className="provider-prices"><label>Input, USD per million tokens<input type="number" min="0" step="any" value={inputPrice} onChange={event => setInputPrice(event.target.value)} /></label>
          <label>Output, USD per million tokens<input type="number" min="0" step="any" value={outputPrice} onChange={event => setOutputPrice(event.target.value)} /></label></div>
        <p className="muted">Used for the spending estimate. Leave blank if you don’t know.</p>
      </details>

      <div className="provider-buttons"><button type="button" className="primary-button" disabled={!model.trim() || pricesInvalid}
        onClick={() => void run(() => providerRequest("PATCH", `${path}/config`, { model: model.trim(), vision, input_price_per_million: inputPrice === "" ? null : Number(inputPrice), output_price_per_million: outputPrice === "" ? null : Number(outputPrice) }), "Saved. LocalMind will ask before the first message goes to this model.")}>Save model</button>
        <button type="button" className="secondary-button" disabled={localOnly || !provider.connected || !model.trim()} onClick={() => void run(() => providerRequest("POST", `${path}/check`, { model: model.trim() }), "Connected. The model is available, and no chat was sent.")}>Check connection</button>
        {provider.consent_id && <button type="button" className="secondary-button" onClick={() => void run(() => providerRequest("DELETE", `${path}/consent`), "Permission removed. LocalMind will ask again before sending.")}>Revoke chat permission</button>}
      </div>
      {message && <p role="status" className="provider-message">{message}</p>}
    </fieldset>
  </details>;
}

function ConnectDialog({ provider, onClose, onDone }: { provider: CloudProvider; onClose: () => void; onDone: () => Promise<void> }) {
  const [key, setKey] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true); setError("");
    try { await providerRequest("PUT", `/providers/${provider.id}/credential`, { key: key.trim() }); setKey(""); await onDone(); }
    catch (cause) { setError(providerError(cause)); }
    finally { setBusy(false); }
  };
  return <div className="connect-overlay" onClick={event => { if (event.target === event.currentTarget) onClose(); }}>
    <div className="connect-dialog" role="dialog" aria-modal="true" aria-labelledby="connect-title"
      onKeyDown={event => { if (event.key === "Escape") onClose(); }}>
      <div className="connect-head"><BrandLogo id={provider.id} label={provider.label} size={32} />
        <div><h3 id="connect-title">Connect {provider.label}</h3><p className="muted">Your key is kept in this computer’s credential store.</p></div></div>
      <label className="connect-field">API key
        <input autoFocus type="password" value={key} autoComplete="off" spellCheck={false} aria-label={`${provider.label} API key`}
          onChange={event => setKey(event.target.value)} onKeyDown={event => { if (event.key === "Enter" && key.trim() && !busy) void save(); }} placeholder="Paste your API key" /></label>
      {error && <p role="alert" className="provider-message">{error}</p>}
      <div className="provider-buttons connect-actions">
        <button type="button" className="secondary-button" onClick={onClose}>Cancel</button>
        <button type="button" className="primary-button" disabled={!key.trim() || busy} onClick={() => void save()}>Save key</button>
      </div>
    </div>
  </div>;
}
