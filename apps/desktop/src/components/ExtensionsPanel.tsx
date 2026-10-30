import { useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import "./extensions.css";

interface Planner { enabled: boolean; status: string; error: string; evidence: string; manifest: { version: string; permissions: string[] } }
interface Catalog { planner: Planner; recipe: { registered: boolean; permissions: string[]; evidence: string }; images: { status: string; reason: string; evidence: string } }
function request<T>(operation: string, payload?: Record<string, unknown>) {
  return invoke<T>("extension_request", { operation, payload: payload ?? null });
}

export function ExtensionsPanel() {
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [reviewed, setReviewed] = useState(false);
  const [tasks, setTasks] = useState("");
  const [plan, setPlan] = useState<string[]>([]);
  const [packageText, setPackage] = useState("");
  const [receipt, setReceipt] = useState("");
  const epoch = useRef(0);
  useEffect(() => {
    const current = ++epoch.current;
    void request<Catalog>("status").then(value => { if (current === epoch.current) setCatalog(value); })
      .catch(() => { if (current === epoch.current) setError("Extensions are unavailable. Reopen Connections to retry."); });
    return () => { epoch.current += 1; };
  }, []);

  async function scopedRequest<T>(operation: string, payload?: Record<string, unknown>): Promise<T> {
    const current = epoch.current;
    const result = await request<T>(operation, payload);
    if (current !== epoch.current) throw new Error("Extension panel was closed");
    return result;
  }
  async function action(work: () => Promise<void>) {
    if (busy) return;
    setBusy(true); setError("");
    const current = epoch.current;
    try { await work(); }
    catch { if (current === epoch.current) { setError("The request failed or was rejected. Check the input and permissions, then retry."); setPlan([]); setReceipt(""); } }
    finally {
      const state = await scopedRequest<Catalog>("status").catch(() => null);
      if (current === epoch.current) { setCatalog(state); setBusy(false); }
    }
  }
  return <div className="extension-grid" aria-label="Built-in extensions">
    {error && <p className="page-notice error" role="alert">{error}</p>}
    {catalog && <>
      <section className="integration-card" aria-labelledby="planner-title">
        <header><h2 id="planner-title">Local planner</h2><span className="page-badge">Local · Built-in</span></header>
        <p>Turn a short list into a validated task preview. Nothing is saved to your projects from this panel.</p>
        <p role="status">{catalog.planner.error || (catalog.planner.enabled ? "Planner enabled" : "Planner disabled")}</p>
        <details><summary>Permissions and verification</summary><p>Version {catalog.planner.manifest.version}. Access: {catalog.planner.manifest.permissions.join(", ")}. No file, credential or network access.</p><p className="muted">{catalog.planner.evidence}</p></details>
        {!catalog.planner.enabled && <label className="extension-check"><input type="checkbox" checked={reviewed} disabled={busy} onChange={event => setReviewed(event.target.checked)} /> Allow task previews on this device</label>}
        <button className="page-button" disabled={busy || (!catalog.planner.enabled && !reviewed)} onClick={() => void action(async () => {
          await scopedRequest("planner-state", { enabled: !catalog.planner.enabled }); setPlan([]); setReviewed(false);
        })}>{catalog.planner.enabled ? "Disable planner" : "Enable planner"}</button>
        {catalog.planner.enabled && <><label className="extension-field">Tasks, one per line<textarea rows={3} maxLength={2900} value={tasks} disabled={busy} onChange={event => setTasks(event.target.value)} /></label>
          <button className="page-button" disabled={busy || !tasks.trim()} onClick={() => void action(async () => {
            const result = await scopedRequest<{ tasks: { title: string }[]; saved: boolean }>("planner-call", { name: "plan_tasks", arguments: { tasks: tasks.split("\n").filter(line => line.trim()).map(title => ({ title: title.trim(), due_at: null })) } });
            if (result.saved !== false) throw new Error("Unexpected action result");
            setPlan(result.tasks.map(task => task.title));
          })}>Preview tasks</button></>}
        {plan.length > 0 && <div aria-label="Task preview"><p>Preview only · Not saved</p><ol>{plan.map((title, index) => <li key={index}>{title}</li>)}</ol></div>}
      </section>
      <section className="integration-card" aria-labelledby="recipe-title">
        <header><h2 id="recipe-title">Repair recipe</h2><span className="page-badge">Local · Research</span></header>
        <p>Export the built-in recipe, then import it to check a repair on temporary synthetic copies. Your files and chats are never included.</p>
        <p role="status">{catalog.recipe.registered ? "Synthetic recipe registered" : "No recipe registered"}</p>
        <details><summary>Permissions and verification</summary><p>{catalog.recipe.permissions.join(", ")}. Only the shipped recipe is accepted; imports cannot add code or new paths.</p><p className="muted">{catalog.recipe.evidence}</p></details>
        <button className="page-button" disabled={busy} onClick={() => void action(async () => { const result = await scopedRequest<{ package: string }>("recipe-export"); setPackage(result.package); setReceipt(""); })}>Export synthetic recipe</button>
        <label className="extension-field">Open recipe JSON<input type="file" accept=".json,application/json" disabled={busy} onChange={async event => {
          const file = event.target.files?.[0];
          if (!file) return;
          if (file.size > 16384) { setError("Recipe packages must be smaller than 16 KiB."); return; }
          const current = epoch.current;
          try { const text = await file.text(); if (current === epoch.current) { setPackage(text); setReceipt(""); } }
          catch { if (current === epoch.current) setError("Could not read the selected recipe."); }
        }} /></label>
        <button className="page-button" disabled={busy} onClick={() => void action(async () => {
          const result = await scopedRequest<{ package: string }>("recipe-export");
          const url = URL.createObjectURL(new Blob([result.package], { type: "application/json" }));
          const link = document.createElement("a"); link.href = url; link.download = "localmind-synthetic-recipe.json"; link.click();
          setTimeout(() => URL.revokeObjectURL(url), 1000);
        })}>Save synthetic recipe JSON</button>
        <label className="extension-field">Recipe JSON<textarea rows={5} maxLength={16384} value={packageText} disabled={busy} onChange={event => { setPackage(event.target.value); setReceipt(""); }} /></label>
        <button className="page-button" disabled={busy || !packageText.trim()} onClick={() => void action(async () => {
          const result = await scopedRequest<{ checks: Record<string, boolean>; synthetic: boolean }>("recipe-import", { package: packageText });
          const expected = ["exactly_one_match", "markdown_valid", "original_unchanged", "expected_output"];
          if (result.synthetic !== true || Object.keys(result.checks).length !== expected.length || !expected.every(name => result.checks[name] === true)) throw new Error("Checks failed");
          setReceipt("All four synthetic checks passed. Original files were not changed.");
        })}>Import and run checks</button>
        {receipt && <p role="status">{receipt}</p>}
      </section>
      <section className="integration-card" aria-labelledby="images-title"><header><h2 id="images-title">Image generation</h2><span className="page-badge">Local · Not installed</span></header><p>{catalog.images.reason}</p><button className="page-button" disabled>Not available in this beta</button><details><summary>Research evidence</summary><p>{catalog.images.evidence}</p></details></section>
    </>}
  </div>;
}
