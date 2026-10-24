import { useState } from "react";

export interface WriteReceipt { item_id: string; state: string; error: string | null; updated_at: string }
export interface ApplyCapabilities { tasks: boolean; files: boolean; reason: string }

export function RepairWriteControls({ operationId, itemId, target, kind, accepted, receipt, capabilities, busy, run }: {
  operationId: string; itemId: string; target: string; kind: string; accepted: boolean;
  receipt?: WriteReceipt; capabilities?: ApplyCapabilities; busy: boolean;
  run: (operation: string, payload?: object) => Promise<boolean>;
}) {
  const [confirming, setConfirming] = useState<"apply" | "restore" | null>(null);
  const enabled = kind === "task" ? capabilities?.tasks : capabilities?.files;
  if (!receipt && !accepted) return null;
  return <div className="dependency-card" aria-label={`Apply review for ${target}`}>
    {receipt ? <>
      <p role="status">Write receipt: {receipt.state.replace(/_/g, " ")}</p>
      {receipt.error && <p role="alert">{receipt.error}</p>}
      <p className="page-footnote">Original bytes and the intended revision are saved in this profile’s repair journal. A recovery check reads the current item without changing it.</p>
      <button className="page-button" disabled={busy} onClick={() => void run("recover", { operation_id: operationId, item_id: itemId, confirm: true })}>Check recovery</button>
      {enabled && receipt.state === "applied" && <button className="page-button" disabled={busy} onClick={() => setConfirming("restore")}>Restore original…</button>}
      {receipt.state === "not_applied" && <p>No write reached the original. Prepare a new preview to try again.</p>}
    </> : enabled ? <button className="page-button primary" disabled={busy} onClick={() => setConfirming("apply")}>Apply this change…</button> : <p role="status">{capabilities?.reason || "Apply availability could not be verified. Refresh dependencies."}</p>}
    {confirming && <div role="group" aria-label="Confirm repair write">
      <p>{confirming === "apply" ? "Apply the reviewed change to" : "Restore the saved original of"} <strong>{target}</strong>? Only this item will change. Conflicting edits block the write.</p>
      <div className="dependency-actions">
        <button className="page-button primary" disabled={busy || !enabled} onClick={async () => {
          if (await run(confirming, { operation_id: operationId, item_id: itemId, confirm: true })) setConfirming(null);
        }}>{confirming === "apply" ? "Confirm apply" : "Confirm restore"}</button>
        <button className="page-button" disabled={busy} onClick={() => setConfirming(null)}>Cancel write</button>
      </div>
    </div>}
  </div>;
}
