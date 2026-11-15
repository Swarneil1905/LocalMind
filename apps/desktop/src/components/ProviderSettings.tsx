import { useState } from "react";
import { useProviders } from "../hooks/useProviders";
import { providerError, providerRequest, type CloudProvider, type ProviderModel } from "../lib/providers";
import "./providers.css";

export function ProviderSettings() {
  const { status, error, refresh } = useProviders();
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);
  const [limitDraft, setLimit] = useState<string | null>(null);
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
  return <section className="provider-settings">
    <div className="settings-section-heading"><h2>Cloud models</h2>
      <p>Use your own API account. API charges are separate from ChatGPT, Claude, Gemini or DeepSeek subscriptions. Keys stay in this computer’s credential store.</p></div>
    <label className="provider-toggle"><input type="checkbox" checked={status.local_only} disabled={busy}
      onChange={event => void policy({ local_only: event.target.checked })} /> Keep chat on this device</label>
    <p className="page-footnote">When enabled, cloud chat, connection checks and model lookups are blocked. This setting does not disable separately enabled web search or connected apps.</p>
    <div className="provider-budget">
      <h3>Monthly API limit for this installation</h3>
      <p>Recorded: ${status.usage.spent_usd.toFixed(2)} · Reserved: ${status.usage.reserved_usd.toFixed(2)} · Requests with unknown cost: {status.usage.unknown_requests}.</p>
      <label>Limit in USD<input type="number" min="0" step="0.01" aria-label="Monthly API limit" value={limit} onChange={event => setLimit(event.target.value)} placeholder="No local limit" /></label>
      <button className="secondary-button" disabled={busy || (limit !== "" && (!Number.isFinite(Number(limit)) || Number(limit) < 0))}
        onClick={() => void policy({ monthly_limit_usd: limit === "" ? null : Number(limit) })}>Save limit</button>
      <p className="page-footnote">This is an estimate for requests made here, not your account-wide bill. Configure model prices below before using a limit. Set a provider-side spending limit as well.</p>
    </div>
    {notice && <p role="alert">{notice}</p>}
    {status.providers.map(provider => <ProviderAccount key={provider.id} provider={provider} localOnly={status.local_only} onChange={changed} />)}
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
  return <fieldset className="provider-account" disabled={busy}>
    <legend>{provider.label} · {provider.connected ? "Key saved" : "Not connected"}</legend>
    {provider.credential_error && <p role="alert">{provider.credential_error}</p>}
    <label>API key<input type="password" value={key} autoComplete="off" spellCheck={false} aria-label={`${provider.label} API key`} onChange={event => setKey(event.target.value)} placeholder={provider.connected ? "Enter a replacement key" : "Paste your API key"} /></label>
    <div className="provider-buttons"><button type="button" className="secondary-button" disabled={!key.trim()}
      onClick={() => { const entered = key; setKey(""); void run(() => providerRequest("PUT", `${path}/credential`, { key: entered }), "Key saved in your credential store."); }}>Save key</button>
      {provider.connected && <button type="button" className="secondary-button" onClick={() => void run(() => providerRequest("DELETE", `${path}/credential`), "Disconnected. The saved key and permission were removed.")}>Disconnect</button>}
    </div>
    <button type="button" className="secondary-button" disabled={!provider.connected || localOnly} onClick={() => void run(async () => {
      const response = await providerRequest<{ models: ProviderModel[]; truncated: boolean }>("GET", `${path}/models`);
      setModels(response.models);
      if (response.truncated) throw new Error("Some models are shown. Enter a model ID if yours is missing.");
    }, "Available models loaded.")}>Load available models</button>
    {models.length > 0 && <label>Available model<select aria-label={`${provider.label} available model`} value={model} onChange={event => selectModel(event.target.value)}>
      <option value="">Choose a model</option>{models.map(item => <option value={item.id} key={item.id}>{item.name}{item.vision === true ? " · Images" : item.vision === false ? " · Text" : " · Capabilities unconfirmed"}</option>)}
    </select></label>}
    <label>Model ID<input value={model} aria-label={`${provider.label} model ID`} onChange={event => selectModel(event.target.value)} placeholder="Choose or enter an accessible model ID" /></label>
    <label className="provider-toggle"><input type="checkbox" checked={vision} onChange={event => setVision(event.target.checked)} /> Image input supported by this model</label>
    <p className="page-footnote">Enable image input only if the model’s documentation confirms it. Image understanding does not imply image generation.</p>
    <div className="provider-prices"><label>Input USD / million tokens<input type="number" min="0" step="any" value={inputPrice} onChange={event => setInputPrice(event.target.value)} /></label>
      <label>Output USD / million tokens<input type="number" min="0" step="any" value={outputPrice} onChange={event => setOutputPrice(event.target.value)} /></label></div>
    <p className="page-footnote">Enter current provider prices for estimates, including image-token charges where applicable. Blank means unknown. Cached tokens and special pricing may differ.</p>
    <div className="provider-buttons"><button type="button" className="secondary-button" disabled={!model.trim() || [inputPrice, outputPrice].some(value => value !== "" && (!Number.isFinite(Number(value)) || Number(value) < 0))}
      onClick={() => void run(() => providerRequest("PATCH", `${path}/config`, { model: model.trim(), vision, input_price_per_million: inputPrice === "" ? null : Number(inputPrice), output_price_per_million: outputPrice === "" ? null : Number(outputPrice) }), "Model settings saved. A changed model needs your permission before its next cloud send.")}>Save model</button>
      <button type="button" className="secondary-button" disabled={localOnly || !provider.connected || !model.trim()} onClick={() => void run(() => providerRequest("POST", `${path}/check`, { model: model.trim() }), "Connection and model access confirmed. No chat was generated.")}>Check connection</button>
      {provider.consent_id && <button type="button" className="secondary-button" onClick={() => void run(() => providerRequest("DELETE", `${path}/consent`), "Permission revoked. Sending again will ask for your permission.")}>Revoke chat permission</button>}
    </div>
    {message && <p role="status">{message}</p>}
  </fieldset>;
}
