import { useRef, useState } from "react";

export interface RepairOperation {
  id: string; source_id: string; state: string; error: string | null; old_value: string; new_value: string;
  items: { id: string; target_kind: string; target_id: string; reason: string; fact: string;
    before_text: string; after_text: string | null; diff: string; validation: string;
    checks: string[]; selection: string }[];
}

export function RepairReview({ sources, repairs, busy, run }: {
  sources: { id: string; path: string; state: string }[];
  repairs: RepairOperation[];
  busy: boolean;
  run: (operation: string, payload?: object) => Promise<boolean>;
}) {
  const [source, setSource] = useState("");
  const [oldValue, setOldValue] = useState("");
  const [newValue, setNewValue] = useState("");
  const operation = useRef<{ key: string; id: string } | null>(null);
  const changed = sources.filter(item => item.state === "changed");
  return <section className="project-dependencies" aria-label="Repair previews">
    <h4>Prepare a repair preview</h4>
    <p>Replace one exact value in confirmed dependent files or tasks. Candidates are prepared on separate copies. Accepting a preview only saves your review choice; applying edits is not available yet.</p>
    {changed.length ? <form className="dependency-form" onSubmit={async event => {
      event.preventDefault();
      const key = JSON.stringify([source, oldValue, newValue]);
      if (operation.current?.key !== key) operation.current = { key, id: crypto.randomUUID() };
      await run("prepare", { operation_id: operation.current.id, source_id: source, old_value: oldValue, new_value: newValue });
    }}>
      <label className="dependency-field">Changed source<select className="page-input" value={source} onChange={event => setSource(event.target.value)} required><option value="">Choose a changed source</option>{changed.map(item => <option value={item.id} key={item.id}>{item.path}</option>)}</select></label>
      <label className="dependency-field">Old value in the captured quote<input className="page-input" value={oldValue} onChange={event => setOldValue(event.target.value)} placeholder="2026-11-18" required maxLength={240} /></label>
      <label className="dependency-field">New value in the current source<input className="page-input" value={newValue} onChange={event => setNewValue(event.target.value)} placeholder="2026-11-25" required maxLength={240} /></label>
      <button className="page-button" disabled={busy || !source || !oldValue.trim() || !newValue.trim()}>Prepare copies for review</button>
    </form> : <p className="page-footnote">Rescan tracked files after changing a source to prepare a preview.</p>}
    {repairs.map(repair => <article key={repair.id} className="dependency-card" aria-label="Repair operation">
      <h4>{repair.old_value} → {repair.new_value}</h4>
      <p>Preview: {repair.state}. Originals remain unchanged.</p>
      {repair.error && <p role="alert">{repair.error}</p>}
      {repair.state === "failed" && <button className="page-button" disabled={busy} onClick={() => {
        operation.current = null;
        void run("prepare", { operation_id: crypto.randomUUID(), source_id: repair.source_id, old_value: repair.old_value, new_value: repair.new_value });
      }}>Prepare a new preview</button>}
      {repair.items.map(item => <section key={item.id} className="dependency-card" aria-label={`Repair for ${item.target_id}`}>
        <h4>{item.target_kind === "task" ? "Task" : "File"}: {item.target_id}</h4>
        <p>{item.reason}</p><blockquote>{item.fact}</blockquote>
        <p><strong>{item.validation === "passed" ? "Checks passed" : "Blocked"}</strong> · Review: {item.selection}</p>
        <ul>{item.checks.map(check => <li key={check}>{check}</li>)}</ul>
        {item.diff && <pre tabIndex={0} aria-label={`Proposed diff for ${item.target_id}`}>{item.diff}</pre>}
        {item.after_text !== null && <details><summary>Inspect full before and after</summary><h5>Before</h5><pre tabIndex={0}>{item.before_text}</pre><h5>After</h5><pre tabIndex={0}>{item.after_text}</pre></details>}
        {repair.state === "ready" && <div className="dependency-actions">
          <button className="page-button" disabled={busy || item.validation !== "passed" || item.selection === "accepted"} onClick={() => void run("selection", { operation_id: repair.id, item_id: item.id, selection: "accepted" })}>Accept proposal</button>
          <button className="page-button" disabled={busy || item.selection === "rejected"} onClick={() => void run("selection", { operation_id: repair.id, item_id: item.id, selection: "rejected" })}>Reject proposal</button>
        </div>}
      </section>)}
    </article>)}
  </section>;
}
