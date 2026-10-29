import { useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";

interface Preview {
  executable: string;
  arguments: string[];
  scope: string;
  approval: string;
  enabled: boolean;
  running?: boolean;
  tools: { name: string; description: string; inputSchema: unknown }[];
}
function request<T>(operation: string, payload?: Record<string, unknown>) {
  return invoke<T>("mcp_request", { operation, payload: payload ?? null });
}

export function McpPanel() {
  const [preview, setPreview] = useState<Preview | null>(null);
  const [reviewed, setReviewed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const generation = useRef(0);
  useEffect(() => {
    let live = true;
    void request<Preview>("preview").then(value => { if (live) setPreview(value); })
      .catch(() => { if (live) setMessage("Local tools are unavailable. Reopen Connections when the service is ready."); });
    return () => { live = false; generation.current += 1; void request("stop").catch(() => {}); };
  }, []);

  async function run(operation: string, payload?: Record<string, unknown>) {
    const current = ++generation.current;
    setBusy(true); setMessage("");
    try {
      const result = await request<Preview & { text?: string }>(operation, payload);
      if (current !== generation.current) return;
      if (operation === "call") setMessage(result.text || "Tool completed.");
      else { setPreview(result); if (operation === "stop") setReviewed(false); }
    } catch {
      if (current !== generation.current) return;
      setMessage("The tool stopped or failed. Review and enable it again to retry.");
      const state = await request<Preview>("preview").catch(() => null);
      if (current === generation.current) { setPreview(state); setReviewed(false); }
    } finally { if (current === generation.current) setBusy(false); }
  }
  return <section className="voice-card" aria-labelledby="local-tools-heading">
    <h2 id="local-tools-heading">Local tools <span className="page-badge">MCP pilot</span></h2>
    <p>Try the owned read-only test server. This does not connect third-party tools or let chat run commands.</p>
    {preview && <>
      <p>{preview.scope}</p>
      <dl><dt>Executable</dt><dd><code style={{ overflowWrap: "anywhere" }}>{preview.executable}</code></dd>
        <dt>Arguments</dt><dd><code>{JSON.stringify(preview.arguments)}</code></dd></dl>
      <ul>{preview.tools.map(tool => <li key={tool.name}><strong>{tool.name}</strong>: {tool.description}
        <details><summary>Input schema</summary><pre style={{ whiteSpace: "pre-wrap" }}>{JSON.stringify(tool.inputSchema, null, 2)}</pre></details></li>)}</ul>
      <p role="status">{preview.enabled ? "Enabled for this Connections session" : preview.running ? "Tool access is disabled. Process cleanup is pending; retry Stop." : "Disabled. No server is running."}</p>
      {!preview.enabled && !preview.running && <><label><input type="checkbox" checked={reviewed} disabled={busy} onChange={event => setReviewed(event.target.checked)} /> I reviewed the executable and read-only scope.</label>
        <button className="page-button" disabled={!reviewed || busy} onClick={() => void run("enable", { approval: preview.approval })}>Enable test server</button></>}
      {preview.enabled && <><button className="page-button" disabled={busy} onClick={() => void run("call", { name: "read_fixture", arguments: { path: "sample.txt" } })}>Read sample</button>
        <button className="page-button" disabled={busy} onClick={() => void run("call", { name: "wait_fixture", arguments: { seconds: 4 } })}>Test waiting</button></>}
      {(preview.enabled || preview.running || busy) && <button className="page-button" onClick={() => void run("stop")}>Stop and disable</button>}
    </>}
    {message && <p role="status">{message}</p>}
    <p className="muted">Tool output is untrusted text. Closing this page stops the server. Permissions reset on restart.</p>
  </section>;
}
