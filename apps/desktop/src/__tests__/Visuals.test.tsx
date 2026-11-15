import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { descent, diagramSvg, parseVisual, tinyNetwork } from "../lib/visuals";
import { AttachmentPreview } from "../components/ChatAttachments";
import { Chart } from "../components/VisualRenderers";
import userEvent from "@testing-library/user-event";

afterEach(cleanup);
it.each(["line", "scatter", "bar"] as const)("distinguishes eight %s series and preserves their legend styles when toggled", async chart => {
  const user = userEvent.setup();
  const series = Array.from({ length: 8 }, (_, index) => ({ name: `Series ${index + 1}`, points: [[1, index + 1], [2, index + 2]] as [number, number][] }));
  render(<Chart spec={{ schema_version: 1, type: "chart", title: "Eight series", chart, source: "Fixture", synthetic: true, x_label: "Day", y_label: "Count", series }} />);
  const plot = screen.getByRole("img", { name: "Eight series" });
  const groups = () => Array.from(plot.children).filter(element => element.tagName.toLowerCase() === "g");
  const styles = () => groups().map(group => ({ color: group.getAttribute("fill"), line: group.querySelector("polyline")?.getAttribute("stroke-dasharray"), point: group.querySelector("path")?.getAttribute("d") }));
  const original = styles();
  expect(original).toHaveLength(8);
  expect(new Set(original.map(style => style.color)).size).toBe(8);
  if (chart !== "bar") expect(new Set(original.map(style => style.point)).size).toBe(8);
  const keys = series.map(({ name }) => screen.getByRole("checkbox", { name }).closest("label")!.querySelector("svg")!);
  keys.forEach((key, index) => {
    expect(key).not.toBeNull();
    expect(key.getAttribute("aria-hidden")).toBe("true");
    expect(key.getAttribute("fill")).toBe(original[index].color);
    if (chart === "line") expect(key.querySelector("line")?.getAttribute("stroke-dasharray")).toBe(original[index].line);
    if (chart !== "bar") expect(key.querySelector("path")?.getAttribute("d")).toBe(original[index].point);
  });
  const first = screen.getByRole("checkbox", { name: "Series 1" });
  first.focus();
  await user.keyboard(" ");
  expect((first as HTMLInputElement).checked).toBe(false);
  expect(styles()).toEqual(original.slice(1));
  expect(screen.getAllByRole("row")).toHaveLength(17);
  await user.keyboard(" ");
  expect((first as HTMLInputElement).checked).toBe(true);
  expect(styles()).toEqual(original);
});
it("keeps a chart series color when another series is hidden", () => {
  render(<Chart spec={{ schema_version: 1, type: "chart", title: "Trend", chart: "line", source: "Fixture", synthetic: true, x_label: "Day", y_label: "Count", series: [{ name: "First", points: [[1, 2]] }, { name: "Second", points: [[1, 3]] }] }} />);
  const point = screen.getByText("Second: 1, 3").parentElement!;
  const color = point.parentElement!.getAttribute("fill");
  fireEvent.click(screen.getByRole("checkbox", { name: "First" }));
  expect(screen.getByText("Second: 1, 3").parentElement!.parentElement!.getAttribute("fill")).toBe(color);
});
describe("trusted visual calculations", () => {
  it("matches a numerical derivative across saturated and unsaturated neurons", () => {
    for (const input of [-4, -.2, 0, 1, 3]) for (const weight of [-2, .4, 1.5]) {
      const epsilon = 1e-5;
      const numerical = (tinyNetwork(input, weight+epsilon, .8).loss-tinyNetwork(input, weight-epsilon, .8).loss)/(2*epsilon);
      expect(tinyNetwork(input, weight, .8).gradient).toBeCloseTo(numerical, 6);
    }
  });
  it("gradient steps reduce this convex objective for an appropriate learning rate", () => {
    const values = descent(4, .1);
    expect(values[values.length-1] ** 2).toBeLessThan(.001);
  });
  it("exports inert SVG text without scripts or foreignObject", () => {
    const svg = diagramSvg({ schema_version: 1, type: "diagram", title: '<script>alert(1)</script>', nodes: [{ id: "a", label: '<img onerror="bad">', x: 0, y: 0 }], edges: [] });
    expect(svg).not.toContain("<script>"); expect(svg).not.toContain("<img"); expect(svg).not.toContain("foreignObject");
    expect(svg).toContain("&lt;script&gt;");
  });
  it("rejects executable schemas before opening a workspace", () => {
    expect(() => parseVisual(JSON.stringify({ schema_version: 1, type: "html", title: "App", source: "while(true){}" }))).toThrow();
  });
  it("exports full labels, edge direction and the complete graph bounds", () => {
    const label = "A long label that must survive export without losing its final words";
    const svg = diagramSvg({ schema_version: 1, type: "diagram", title: "Large graph", nodes: [{ id: "a", label, x: 0, y: 0 }, { id: "b", label: "End", x: 6000, y: 5000 }], edges: [{ id: "ab", source: "a", target: "b" }] });
    const parsed = new DOMParser().parseFromString(svg, "image/svg+xml");
    expect(parsed.querySelector("text")?.textContent).toBe(label);
    expect(svg).toContain('marker-end="url(#arrow)"');
    expect(Number(parsed.documentElement.getAttribute("width"))).toBeLessThanOrEqual(4096);
    expect(Number(parsed.documentElement.getAttribute("viewBox")?.split(" ")[2])).toBeGreaterThan(6000);
  });
});
describe("attachment preview", () => {
  it("zooms without changing the source image", () => {
    render(<AttachmentPreview item={{ filename: "Screenshot", data_url: "data:image/png;base64,YQ==" }} onClose={() => {}} />);
    fireEvent.change(screen.getByRole("slider"), { target: { value: "2" } });
    expect((screen.getByRole("img") as HTMLImageElement).style.width).toBe("200%");
  });
  it("shows the actual document excerpts and scan limitations", () => {
    render(<AttachmentPreview item={{ filename: "Notes", kind: "document", limitation: "Text only", pages: [{ page: 2, text: "Exact excerpt", truncated: true, scanned: false }] }} onClose={() => {}} />);
    expect(screen.getByText("Exact excerpt")).toBeTruthy(); expect(screen.getByText("Page 2 · excerpt truncated")).toBeTruthy();
    expect(screen.queryByRole("img")).toBeNull();
  });
});
