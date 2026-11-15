export interface DiagramNode { id: string; label: string; x: number; y: number }
export interface DiagramEdge { id: string; source: string; target: string }
export interface VisualSpec {
  schema_version: 1; type: "diagram" | "mermaid" | "chart" | "widget" | "image"; title: string;
  attachment_id?: string;
  nodes?: DiagramNode[]; edges?: DiagramEdge[]; steps?: { node: string; text: string }[];
  source?: string; chart?: "line" | "bar" | "scatter";
  series?: { name: string; points: [number, number][] }[];
  x_label?: string; y_label?: string; synthetic?: boolean;
  widget?: "gradient-descent" | "compound-growth" | "backpropagation";
  parameters?: Record<string, number>;
}
export interface VisualDocument {
  id: string; conversation_id: string; source_message_id: string;
  versions: { version: number; parent: number | null; created_at: string; spec: VisualSpec }[];
}
export function parseVisual(source: string): VisualSpec {
  if (source.length > 100_000) throw new Error("This visual is too large.");
  const value = JSON.parse(source);
  if (!value || value.schema_version !== 1 || !["diagram", "mermaid", "chart", "widget", "image"].includes(value.type) || typeof value.title !== "string" || value.title.length > 200) throw new Error("Unsupported visual format.");
  return value as VisualSpec; // Detailed validation occurs in the local service before rendering.
}
/** Models often add colour and layout lines that only restyle a diagram. Drop them so the
 * diagram still renders (and passes the service's no-styling rule) instead of failing. */
export function cleanMermaid(source: string): string {
  const flowchart = /^\s*(flowchart|graph)\b/i.test(source);
  return source.split("\n")
    .filter(line => !/^\s*(style|classDef|linkStyle|click)\b/i.test(line) && !/^\s*%%/.test(line)
      && !(flowchart && /^\s*class\s/i.test(line)))
    .map(line => line.replace(/\\n/g, " ").replace(/:::\w+/g, ""))
    .join("\n").trim();
}

export function tinyNetwork(input: number, weight: number, target: number) {
  // Two scalar layers: tanh(w*x), then a fixed output weight of 2.
  const hidden = Math.tanh(input * weight), output = 2 * hidden;
  const loss = (output - target) ** 2 / 2;
  return { hidden, output, loss, gradient: (output - target) * 2 * (1 - hidden ** 2) * input };
}
export function descent(initial: number, rate: number, steps = 30) {
  const values = [initial];
  for (let i = 0; i < steps; i++) values.push(values[i] - rate * 2 * values[i]);
  return values;
}
export function escapeXml(value: string): string {
  return value.replace(/[&<>"']/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&apos;" })[character]!);
}
/** Deliberately export plain shapes and text, never foreignObject or application markup. */
export function diagramSvg(spec: VisualSpec): string {
  const nodes = spec.nodes || [], edges = spec.edges || [];
  const minX = Math.min(0, ...nodes.map(node => node.x)) - 30, minY = Math.min(0, ...nodes.map(node => node.y)) - 30;
  const lines = (label: string) => label.match(/.{1,26}/gu) || [label];
  const heightOf = (node: DiagramNode) => Math.max(50, lines(node.label).length * 16 + 20);
  const viewWidth = Math.max(600, ...nodes.map(node => node.x + 230)) - minX;
  const viewHeight = Math.max(400, ...nodes.map(node => node.y + heightOf(node) + 30)) - minY;
  const scale = Math.min(1, 4096 / viewWidth, 4096 / viewHeight);
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${Math.max(1, Math.round(viewWidth*scale))}" height="${Math.max(1, Math.round(viewHeight*scale))}" viewBox="${minX} ${minY} ${viewWidth} ${viewHeight}" role="img"><title>${escapeXml(spec.title)}</title><defs><marker id="arrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto"><path d="M0 0L8 4L0 8Z" fill="#34695e"/></marker></defs><rect x="${minX}" y="${minY}" width="${viewWidth}" height="${viewHeight}" fill="white"/>` +
    edges.map(edge => {
      const a = nodes.find(node => node.id === edge.source)!, b = nodes.find(node => node.id === edge.target)!;
      const ax = a.x + 100, ay = a.y + heightOf(a)/2, bx = b.x + 100, by = b.y + heightOf(b)/2;
      const dx = bx-ax, dy = by-ay;
      const ar = Math.max(Math.abs(dx)/100, Math.abs(dy)/(heightOf(a)/2), 1);
      const br = Math.max(Math.abs(dx)/100, Math.abs(dy)/(heightOf(b)/2), 1);
      return `<path d="M${ax+dx/ar},${ay+dy/ar} L${bx-dx/br},${by-dy/br}" marker-end="url(#arrow)" stroke="#34695e" fill="none"/>`;
    }).join("") +
    nodes.map(node => `<rect x="${node.x}" y="${node.y}" width="200" height="${heightOf(node)}" rx="8" fill="#eff7f2" stroke="#34695e"/><text font-family="sans-serif" font-size="13">${lines(node.label).map((line, index) => `<tspan x="${node.x + 8}" y="${node.y + 24 + index*16}">${escapeXml(line)}</tspan>`).join("")}</text>`).join("") + "</svg>";
}
export function downloadBlob(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob), anchor = document.createElement("a");
  anchor.href = url; anchor.download = filename; anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 30_000);
}
