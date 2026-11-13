import { useEffect, useState } from "react";
import { FEEDBACK_CATEGORIES, qualityRequest, type FeedbackRecord } from "../lib/quality";
import { downloadBlob } from "../lib/visuals";

export function ResponseFeedback({ conversationId, content }: { conversationId: string; content: string }) {
  const [category, setCategory] = useState<keyof typeof FEEDBACK_CATEGORIES>("unhelpful");
  const [correction, setCorrection] = useState(""), [notice, setNotice] = useState(""), [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  async function save() {
    if (busy) return;
    setBusy(true); setError(""); setNotice("");
    try {
      await qualityRequest("POST", "feedback", { conversation_id: conversationId, content, category, correction });
      setNotice("Saved locally. Review or delete it in Settings → Responses.");
    } catch { setError("Couldn't save feedback. Wait for the response to finish saving, then try again."); }
    finally { setBusy(false); }
  }
  return <details className="response-details">
    <summary>Give feedback</summary>
    <p>This optional note stays on your device. It is not sent to a provider or used for training.</p>
    <form onSubmit={e => { e.preventDefault(); void save(); }}>
      <label>Feedback category<select className="page-select" value={category} disabled={busy} onChange={e => setCategory(e.target.value as typeof category)}>
        {Object.entries(FEEDBACK_CATEGORIES).map(([key, label]) => <option key={key} value={key}>{label}</option>)}
      </select></label>
      <label>Correction or note (optional)<textarea className="page-input" maxLength={4000} value={correction} disabled={busy} onChange={e => setCorrection(e.target.value)} /></label>
      <button className="page-button" disabled={busy}>{busy ? "Saving feedback…" : "Save feedback locally"}</button>
    </form>
    {notice && <p role="status">{notice}</p>}{error && <p role="alert">{error}</p>}
  </details>;
}

export function FeedbackSettings() {
  const [records, setRecords] = useState<FeedbackRecord[]>([]), [selected, setSelected] = useState<string[]>([]);
  const [error, setError] = useState(""), [preview, setPreview] = useState("");
  const [counts, setCounts] = useState<Partial<Record<keyof typeof FEEDBACK_CATEGORIES, number>>>({});
  const [busy, setBusy] = useState(false), [reload, setReload] = useState(0);
  useEffect(() => {
    let active = true;
    qualityRequest<{ records: FeedbackRecord[]; counts?: Partial<Record<keyof typeof FEEDBACK_CATEGORIES, number>> }>("GET", "feedback").then(value => {
      if (active) { setError(""); setRecords(value.records); setCounts(value.counts || {}); setSelected([]); setPreview(""); }
    }).catch(() => { if (active) setError("Couldn't load saved feedback."); });
    return () => { active = false; };
  }, [reload]);
  async function remove(id: string) {
    if (busy) return;
    setBusy(true); setError(""); setPreview("");
    try {
      await qualityRequest("POST", "feedback/delete", { id });
      const category = records.find(item => item.id === id)?.category;
      if (category) setCounts(previous => ({ ...previous, [category]: Math.max(0, (previous[category] || 0) - 1) }));
      setRecords(items => items.filter(item => item.id !== id)); setSelected(items => items.filter(item => item !== id));
    } catch { setError("Couldn't delete feedback. Try again."); }
    finally { setBusy(false); }
  }
  async function prepareExport() {
    setBusy(true); setError(""); setPreview("");
    try { setPreview(JSON.stringify(await qualityRequest("POST", "feedback/export", { ids: selected }), null, 2)); }
    catch { setError("Couldn't prepare the selected feedback. Refresh and try again."); }
    finally { setBusy(false); }
  }
  return <section aria-label="Local answer feedback">
    <h3>Local answer feedback</h3><p>Review your saved notes. Export includes only selected categories and corrections, not conversation text or identifiers. Review the preview for private information before sharing it.</p>
    <button className="page-button" disabled={busy} onClick={() => { setError(""); setReload(reload + 1); }}>Refresh feedback</button>
    {error && <p role="alert">{error}</p>}
    {!!records.length && <><p>Showing up to 500 recent notes. Counts include all saved feedback.</p><ul aria-label="Feedback categories">{Object.entries(counts).filter(([, count]) => count && count > 0).map(([category, count]) => <li key={category}>{FEEDBACK_CATEGORIES[category as keyof typeof FEEDBACK_CATEGORIES]}: {count}</li>)}</ul></>}
    {!records.length && <p>No saved feedback.</p>}
    {records.map(record => <article key={record.id} className="memory-record">
      <label><input type="checkbox" disabled={busy} checked={selected.includes(record.id)} onChange={e => { setPreview(""); setSelected(ids => e.target.checked ? [...ids, record.id] : ids.filter(id => id !== record.id)); }} />{FEEDBACK_CATEGORIES[record.category]}</label>
      <p>{record.correction || "No correction supplied."}</p>
      <button className="page-button" disabled={busy} onClick={() => void remove(record.id)}>Delete feedback</button>
    </article>)}
    <button className="page-button" disabled={busy || !selected.length || selected.length > 200} onClick={() => void prepareExport()}>Preview selected export</button>
    {preview && <><label>Review and redact export<textarea className="page-input" rows={10} value={preview} onChange={e => setPreview(e.target.value)} /></label>
      <button className="page-button" onClick={() => downloadBlob(new Blob([preview], { type: "application/json" }), "localmind-feedback.json")}>Download reviewed export</button></>}
  </section>;
}
