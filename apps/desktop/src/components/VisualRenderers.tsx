import { useEffect, useId, useMemo, useState } from "react";
import { ReactFlow, Background, Controls, applyNodeChanges, applyEdgeChanges, type Node, type Edge } from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import DOMPurify from "dompurify";
import { descent, tinyNetwork, type VisualSpec } from "../lib/visuals";

export function Diagram({ spec, onChange, highlighted }: { spec: VisualSpec; onChange: (spec: VisualSpec) => void; highlighted?: string }) {
  const [selected, setSelected] = useState("");
  const nodes: Node[] = (spec.nodes || []).map(node => ({ id: node.id, data: { label: node.label }, position: { x: node.x, y: node.y }, selected: node.id === selected, style: { background: "var(--surface)", color: "var(--text)", border: "1px solid var(--border)", outline: node.id === highlighted ? "3px solid #bd782b" : undefined } }));
  const edges: Edge[] = spec.edges || [];
  const saveNodes = (next: Node[]) => onChange({ ...spec, nodes: next.map(node => ({ id: node.id, label: String(node.data.label), x: node.position.x, y: node.position.y })), edges: edges.filter(edge => next.some(node => node.id === edge.source) && next.some(node => node.id === edge.target)), steps: spec.steps?.filter(step => next.some(node => node.id === step.node)) });
  const chosen = spec.nodes?.find(node => node.id === selected);
  return <><div className="visual-graph"><ReactFlow nodes={nodes} edges={edges} fitView minZoom={0.15} maxZoom={3}
    onNodeClick={(_, node) => setSelected(node.id)}
    onNodesChange={changes => { const edits = changes.filter(change => change.type !== "select" && change.type !== "dimensions"); if (edits.length) saveNodes(applyNodeChanges(edits, nodes)); }}
    onEdgesChange={changes => { const edits = changes.filter(change => change.type !== "select"); if (edits.length) onChange({ ...spec, edges: applyEdgeChanges(edits, edges).map(({ id, source, target }) => ({ id, source, target })) }); }}
    onConnect={connection => { if (edges.length < 160) onChange({ ...spec, edges: [...edges, { id: crypto.randomUUID(), source: connection.source, target: connection.target }] }); }}>
    <Background /><Controls /></ReactFlow></div>
    <div className="visual-toolbar"><button onClick={() => onChange({ ...spec, nodes: spec.nodes?.map((node, index) => ({ ...node, x: (index % 3) * 240, y: Math.floor(index / 3) * 130 })) })}>Arrange nodes</button>
    <button disabled={nodes.length >= 80} onClick={() => saveNodes([...nodes, { id: crypto.randomUUID(), data: { label: "New node" }, position: { x: 0, y: nodes.length * 70 } }])}>Add node</button>
    {chosen && <><label>Node label <input maxLength={200} value={chosen.label} onChange={event => onChange({ ...spec, nodes: spec.nodes?.map(node => node.id === selected ? { ...node, label: event.target.value } : node) })} /></label><button onClick={() => saveNodes(nodes.filter(node => node.id !== selected))}>Remove node</button></>}</div>
    <details><summary>Text equivalent</summary><ul>{spec.nodes?.map(node => <li key={node.id}>{node.label}</li>)}</ul><ul>{edges.map(edge => <li key={edge.id}>{spec.nodes?.find(node => node.id === edge.source)?.label} → {spec.nodes?.find(node => node.id === edge.target)?.label}</li>)}</ul></details></>;
}

export function MermaidView({ source }: { source: string }) {
  const id = useId().replace(/[^a-z0-9]/gi, ""), [result, setResult] = useState({ svg: "", error: "" });
  useEffect(() => {
    let active = true;
    void import("mermaid").then(async ({ default: mermaid }) => {
      mermaid.initialize({ startOnLoad: false, securityLevel: "strict", maxTextSize: 12000, maxEdges: 160, suppressErrorRendering: true, htmlLabels: false, flowchart: { htmlLabels: false } });
      const rendered = await mermaid.render(`visual${id}`, source);
      const svg = DOMPurify.sanitize(rendered.svg, { USE_PROFILES: { svg: true }, FORBID_TAGS: ["foreignObject", "a", "image", "script", "style"], FORBID_ATTR: ["href", "xlink:href", "style"] });
      if (active) setResult({ svg, error: "" });
    }).catch(() => { if (active) setResult({ svg: "", error: "This Mermaid source could not be rendered. Edit the source or ask for a corrected diagram." }); });
    return () => { active = false; };
  }, [id, source]);
  return <>{result.error ? <p role="alert">{result.error}</p> : <div className="mermaid-visual" role="img" aria-label="Mermaid diagram" dangerouslySetInnerHTML={{ __html: result.svg }} />}<details><summary>Diagram source</summary><pre>{source}</pre></details></>;
}

// Use the original series index for both the plot and legend, even when a series is hidden.
const chartStyles = [
  { color: "#368477", dash: "none", point: "M-4 0a4 4 0 1 0 8 0a4 4 0 1 0-8 0" },
  { color: "#a5762b", dash: "8 4", point: "M-4-4H4V4H-4Z" },
  { color: "#6785b9", dash: "2 3", point: "M0-5L5 4H-5Z" },
  { color: "#bd6378", dash: "8 3 2 3", point: "M0-5L5 0 0 5-5 0Z" },
  { color: "#9273b4", dash: "12 4", point: "M-2-5H2V-2H5V2H2V5H-2V2H-5V-2H-2Z" },
  { color: "#748531", dash: "4 3", point: "M0 5L5-4H-5Z" },
  { color: "#b76c38", dash: "12 3 2 3 2 3", point: "M-5-3L-3-5 0-2 3-5 5-3 2 0 5 3 3 5 0 2-3 5-5 3-2 0Z" },
  { color: "#738392", dash: "4 3 2 3", point: "M-5 0L-2.5-4.3H2.5L5 0 2.5 4.3H-2.5Z" },
];

export function Chart({ spec }: { spec: VisualSpec }) {
  const [hidden, setHidden] = useState<string[]>([]);
  const series = spec.series || [], visible = series.map((entry, index) => ({ entry, index })).filter(({ index }) => !hidden.includes(String(index)));
  const points = series.flatMap(entry => entry.points), xs = points.map(p => p[0]), ys = points.map(p => p[1]);
  const minX = Math.min(0, ...xs), maxX = Math.max(1, ...xs), minY = Math.min(0, ...ys), maxY = Math.max(1, ...ys);
  const x = (n: number) => 50 + (n-minX) / (maxX-minX) * 500, y = (n: number) => 330 - (n-minY) / (maxY-minY) * 280;
  return <><p>{spec.synthetic ? "Illustrative data" : "Supplied data (model interpretation)"} · {spec.source}</p>
    <div className="visual-toolbar">{series.map((entry, index) => {
      const style = chartStyles[index % chartStyles.length];
      return <label key={index}><input type="checkbox" checked={!hidden.includes(String(index))} onChange={event => setHidden(event.target.checked ? hidden.filter(item => item !== String(index)) : [...hidden, String(index)])} />
        <svg width="32" height="16" viewBox="0 0 32 16" fill={style.color} aria-hidden="true" focusable="false" style={{ display: "inline-block", verticalAlign: "middle", margin: "0 4px" }}>
          {spec.chart === "line" && <line x1="0" y1="8" x2="32" y2="8" stroke={style.color} strokeWidth={2} strokeDasharray={style.dash} />}
          {spec.chart === "bar" ? <rect x="12" y="2" width="8" height="12" /> : <path d={style.point} transform="translate(16 8)" />}
        </svg>{entry.name}</label>;
    })}</div>
    <svg className="visual-chart" viewBox="0 0 620 400" role="img" aria-label={spec.title}><path d="M50 40V330H570" fill="none" stroke="currentColor" />
      <text x="240" y="390" fill="currentColor">{spec.x_label}</text><text x="10" y="22" fill="currentColor">{spec.y_label}</text>
      <text x="50" y="355" fill="currentColor">{minX}</text><text x="530" y="355" fill="currentColor">{maxX}</text><text x="5" y="60" fill="currentColor">{maxY}</text><text x="5" y="330" fill="currentColor">{minY}</text>
      {visible.map(({ entry, index }) => {
        const style = chartStyles[index % chartStyles.length];
        return <g key={index} fill={style.color}>{spec.chart === "line" && <polyline fill="none" stroke={style.color} strokeWidth={2} strokeDasharray={style.dash} points={entry.points.map(p => `${x(p[0])},${y(p[1])}`).join(" ")} />}{entry.points.map((point, p) => spec.chart === "bar" ? <rect key={p} x={x(point[0]) + index * 8 - 5} y={Math.min(y(0), y(point[1]))} width={7} height={Math.max(1, Math.abs(y(0)-y(point[1])))}><title>{entry.name}: {point.join(", ")}</title></rect> : <path key={p} d={style.point} transform={`translate(${x(point[0])} ${y(point[1])})`}><title>{entry.name}: {point.join(", ")}</title></path>)}</g>;
      })}</svg>
    <details><summary>Source data</summary><table><thead><tr><th>Series</th><th>{spec.x_label}</th><th>{spec.y_label}</th></tr></thead><tbody>{series.flatMap((entry, i) => entry.points.map((point, j) => <tr key={`${i}-${j}`}><td>{entry.name}</td><td>{point[0]}</td><td>{point[1]}</td></tr>))}</tbody></table></details></>;
}

export function LearningWidget({ spec }: { spec: VisualSpec }) {
  const [input, setInput] = useState(Math.max(-5, Math.min(5, spec.parameters?.input ?? 1)));
  const [weight, setWeight] = useState(Math.max(-3, Math.min(3, spec.parameters?.weight ?? 0.5)));
  const [rate, setRate] = useState(spec.parameters?.rate ?? 0.1);
  const [target, setTarget] = useState(spec.parameters?.target ?? 1);
  const [principal, setPrincipal] = useState(spec.parameters?.principal ?? 100);
  const [years, setYears] = useState(spec.parameters?.years ?? 20);
  const network = tinyNetwork(input, weight, target);
  const series = useMemo(() => spec.widget === "compound-growth" ? Array.from({ length: years + 1 }, (_, year) => [year, principal * (1 + rate) ** year] as [number, number]) : descent(input, rate).map((n, step) => [step, n*n] as [number, number]), [spec.widget, input, rate, principal, years]);
  return <><p>Runs on this device. Adjusting controls makes no model or API calls.</p>
    {spec.widget !== "compound-growth" && <label>Input {input.toFixed(2)}<input type="range" min={-5} max={5} step={0.05} value={input} onChange={event => setInput(Number(event.target.value))} /></label>}
    {spec.widget === "backpropagation" ? <><label>First-layer weight {weight.toFixed(2)}<input type="range" min={-3} max={3} step={0.05} value={weight} onChange={event => setWeight(Number(event.target.value))} /></label>
      <label>Target {target.toFixed(2)}<input type="range" min={-5} max={5} step={0.05} value={target} onChange={event => setTarget(Number(event.target.value))} /></label>
      <p>input → tanh(weight × input) → × 2 → output → ½(output − target)²</p><dl>{Object.entries(network).map(([key, value]) => <div key={key}><dt>{key}</dt><dd>{value.toFixed(6)}</dd></div>)}</dl>
      <p>The gradient multiplies the loss derivative, output weight, tanh derivative and input. A negative gradient means increasing the first weight lowers the loss locally.</p></> : <><label>{spec.widget === "compound-growth" ? "Annual growth rate" : "Learning rate"} {rate.toFixed(2)}<input type="range" min={0.01} max={spec.widget === "compound-growth" ? 0.2 : 0.9} step={0.01} value={rate} onChange={event => setRate(Number(event.target.value))} /></label>
      {spec.widget === "compound-growth" && <><label>Starting units {principal}<input type="range" min={1} max={1000000} value={principal} onChange={event => setPrincipal(Number(event.target.value))} /></label><label>Years {years}<input type="range" min={1} max={50} value={years} onChange={event => setYears(Number(event.target.value))} /></label></>}
      <p>{spec.widget === "compound-growth" ? "Annual compounding with no contributions, fees or inflation." : "Minimize x² by repeatedly applying x ← x − rate × 2x. A smaller rate takes smaller steps."}</p>
      <Chart spec={{ schema_version: 1, type: "chart", title: spec.title, chart: "line", synthetic: true, source: "Computed from the controls above", x_label: spec.widget === "compound-growth" ? "Years" : "Steps", y_label: spec.widget === "compound-growth" ? "Units" : "Loss", series: [{ name: "Value", points: series }] }} /></>}
    <button onClick={() => { setInput(spec.parameters?.input ?? 1); setWeight(spec.parameters?.weight ?? 0.5); setRate(spec.parameters?.rate ?? 0.1); setTarget(spec.parameters?.target ?? 1); setPrincipal(spec.parameters?.principal ?? 100); setYears(spec.parameters?.years ?? 20); }}>Reset controls</button></>;
}
