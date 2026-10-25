import { createRef } from "react";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AnswerContent, safeWebUrl } from "../components/AnswerContent";
import { Composer } from "../components/Composer";
import { MessageList } from "../components/MessageList";
import type { Message, WebSource } from "../types";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const source: WebSource = {
  title: "Synthetic transport report",
  url: "https://news.example.invalid/report",
  snippet: "A synthetic source fixture.",
  published_at: "2026-10-19T14:00:00Z",
};
const answer = (content: string, extra: Partial<Message> = {}): Message => ({
  id: "answer",
  role: "assistant",
  content,
  ...extra,
});
const question: Message = { id: "question", role: "user", content: "Explain this" };

describe("answer presentation", () => {
  it("renders inline and display math with accessible MathML and keeps malformed formulas readable", () => {
    const { container } = render(<AnswerContent content={"The loss is $x^2$.\n\n$$\n\\sum_{i=1}^{n} x_i\n$$\n\n$\\frac{$"} />);
    expect(container.querySelectorAll(".katex").length).toBe(2);
    expect(container.querySelector(".katex-display math")).not.toBeNull();
    expect(container.querySelector(".katex-error")?.textContent).toContain("\\frac{");
  });

  it("keeps partial streamed formulas readable until their closing delimiter arrives", () => {
    const { container, rerender } = render(<AnswerContent content="The loss is $x^" />);
    expect(container.textContent).toContain("$x^");
    rerender(<AnswerContent content="The loss is $x^2$." />);
    expect(container.querySelector("math")).not.toBeNull();
  });

  it("copies exact code separately without adding the toolbar or changing inline code", async () => {
    const user = userEvent.setup();
    const copy = vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue();
    render(<AnswerContent content={'Use `x[1]`.\n\n```python\nprint("hello")\nvalue = 3\n```'} sources={[source]} />);
    expect(screen.queryByRole("link")).toBeNull();
    screen.getByRole("button", { name: "Copy code" }).focus();
    await user.keyboard("{Enter}");
    expect(copy).toHaveBeenCalledWith('print("hello")\nvalue = 3');
    expect(screen.getByRole("status").textContent).toBe("Copied");
    expect(screen.getByLabelText("python block").tabIndex).toBe(0);
  });

  it("only marks references as citations when number and URL match the source list", () => {
    const { container } = render(<AnswerContent
      content={"A supported detail [1]. An unmatched detail [9]. **More [1].**\n\n[1](https://different.example.invalid/claim)\n\n`[1]`"}
      sources={[source]}
    />);
    expect(screen.getAllByRole("link", { name: `Source 1: ${source.title}` })).toHaveLength(2);
    expect(container.querySelectorAll(".source-citation")).toHaveLength(2);
    expect(screen.getByRole("link", { name: "1" }).className).toBe("");
    expect(container.textContent).toContain("[9]");
    expect(container.querySelector("code")?.textContent).toBe("[1]");
  });

  it("does not load images or activate unsafe links, including links hidden in math", () => {
    const { container } = render(<AnswerContent content={
      "[File](file:///C:/private.txt) [Command](javascript:alert(1)) [App](localmind://delete)\n\n" +
      "![Diagram](https://images.example.invalid/image.png)\n\n" +
      '$\\href{https://tracking.example.invalid}{hidden}$\n\n<img src="https://tracking.example.invalid/pixel">'
    } />);
    expect(container.querySelector("img")).toBeNull();
    expect(screen.getAllByRole("link")).toHaveLength(1);
    expect(screen.getByRole("link", { name: "Diagram" }).getAttribute("rel")).toBe("noopener noreferrer");
    expect(container.querySelector("script")).toBeNull();
  });

  it("rejects credentials and executable or ambiguous URL formats", () => {
    for (const value of ["https://user:secret@example.invalid", "//example.invalid", "data:text/html,x", "file:///tmp/a", "https://example.invalid\n/path"]) {
      expect(safeWebUrl(value)).toBeUndefined();
    }
    expect(safeWebUrl("https://example.invalid/path?q=one#two")).toBe("https://example.invalid/path?q=one#two");
  });

  it("puts wide tables in a keyboard-focusable scrolling region", () => {
    render(<AnswerContent content={"| Model | Notes |\n| --- | --- |\n| Small | Fits locally |"} />);
    expect(screen.getByRole("region", { name: "Response table" }).tabIndex).toBe(0);
    expect(screen.getAllByRole("columnheader")).toHaveLength(2);
  });
});

describe("transcript interactions", () => {
  it("keeps the reader's position when more tokens arrive and follows again only after jumping", () => {
    const { rerender } = render(<MessageList messages={[question, answer("First")] } isStreaming />);
    const log = screen.getByRole("log");
    Object.defineProperties(log, {
      scrollHeight: { configurable: true, value: 1600 },
      clientHeight: { configurable: true, value: 500 },
    });
    log.scrollTop = 300;
    fireEvent.scroll(log);
    rerender(<MessageList messages={[question, answer("First, then more tokens.")] } isStreaming />);
    expect(log.scrollTop).toBe(300);
    fireEvent.click(screen.getByRole("button", { name: "Latest response" }));
    expect(log.scrollTop).toBe(1600);
    Object.defineProperty(log, "scrollHeight", { configurable: true, value: 1800 });
    rerender(<MessageList messages={[question, answer("First, then more tokens. Finished.")] } isStreaming={false} />);
    expect(log.scrollTop).toBe(1800);
    expect(screen.queryByRole("button", { name: "Latest response" })).toBeNull();
    expect(log.getAttribute("aria-live")).toBe("off");
  });

  it("supports keyboard retry after an empty stopped response", async () => {
    const user = userEvent.setup();
    const retry = vi.fn();
    render(<MessageList messages={[question, answer("", { cancelled: true })]} isStreaming={false} onRegenerate={retry} />);
    screen.getByRole("button", { name: "Try again" }).focus();
    await user.keyboard("{Enter}");
    expect(retry).toHaveBeenCalledOnce();
    expect(screen.queryByRole("button", { name: "Copy response" })).toBeNull();
  });

  it("announces copy failures without claiming the answer was copied", async () => {
    const user = userEvent.setup();
    vi.spyOn(navigator.clipboard, "writeText").mockRejectedValue(new Error("Permission denied"));
    render(<MessageList messages={[answer("Keep this text")]} isStreaming={false} />);
    await user.click(screen.getByRole("button", { name: "Copy response" }));
    await waitFor(() => expect(screen.getAllByRole("status").some((node) => node.textContent?.includes("Copy failed"))).toBe(true));
    expect(screen.queryByText("Copied")).toBeNull();
  });

  it("displays source dates and excludes unsafe source URLs without renumbering citations", () => {
    render(<MessageList messages={[answer("A claim [2].", { sources: [
      { ...source, title: "Unsafe", url: "file:///private" },
      source,
    ] })]} isStreaming={false} />);
    expect(screen.getByRole("link", { name: `Source 2: ${source.title}` })).toBeDefined();
    expect(screen.queryByText("Unsafe")).toBeNull();
    fireEvent.click(screen.getByText("1 source"));
    expect(screen.getByText(/Published Oct 19, 2026/)).toBeDefined();
  });
});

function composerProps() {
  return {
    textareaRef: createRef<HTMLTextAreaElement>(),
    value: "A draft",
    onChange: vi.fn(),
    onSend: vi.fn().mockResolvedValue(true),
    onStop: vi.fn(),
    isStreaming: false,
    disabled: false,
    memoryEnabled: true,
    onMemoryToggle: vi.fn(),
    knowledgeEnabled: false,
    onKnowledgeToggle: vi.fn(),
    webSearchEnabled: false,
    onWebSearchToggle: vi.fn(),
    modelMode: "balanced" as const,
    onModelModeChange: vi.fn(),
    modelName: "qwen3:8b",
    onOpenKnowledge: vi.fn(),
  };
}

describe("composer keyboard controls", () => {
  it("sends with Enter while preserving Shift+Enter and IME composition", () => {
    const props = composerProps();
    render(<Composer {...props} />);
    const input = screen.getByRole("textbox", { name: "Message LocalMind" });
    fireEvent.keyDown(input, { key: "Enter", shiftKey: true });
    fireEvent.keyDown(input, { key: "Enter", isComposing: true });
    fireEvent.keyDown(input, { key: "Enter", keyCode: 229 });
    expect(props.onSend).not.toHaveBeenCalled();
    fireEvent.keyDown(input, { key: "Enter" });
    expect(props.onSend).toHaveBeenCalledWith("A draft");
  });

  it("stops on Escape and keeps the next draft available while generation is running", () => {
    const props = composerProps();
    render(<Composer {...props} isStreaming />);
    const input = screen.getByRole("textbox", { name: "Message LocalMind" });
    fireEvent.keyDown(input, { key: "Escape" });
    expect(props.onStop).toHaveBeenCalledOnce();
    expect((input as HTMLTextAreaElement).value).toBe("A draft");
    expect((input as HTMLTextAreaElement).disabled).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: "Tools" }));
    expect(screen.getByRole("button", { name: "Memory" }).hasAttribute("disabled")).toBe(true);
    expect(screen.getByRole("button", { name: "Stop generation" }).hasAttribute("disabled")).toBe(false);
  });

  it("names context toggles independently of their visible labels and does not send when unavailable", async () => {
    const props = composerProps();
    const user = userEvent.setup();
    render(<Composer {...props} disabled />);
    fireEvent.click(screen.getByRole("button", { name: "Tools" }));
    expect(screen.getByRole("button", { name: "Memory" }).getAttribute("aria-pressed")).toBe("true");
    screen.getByRole("button", { name: "Web" }).focus();
    await user.keyboard("{Enter}");
    expect(props.onWebSearchToggle).toHaveBeenCalledOnce();
    fireEvent.keyDown(screen.getByRole("textbox"), { key: "Enter" });
    expect(props.onSend).not.toHaveBeenCalled();
  });
});


describe("chat tools disclosure", () => {
  it("opens by keyboard, updates context, and returns focus on Escape without stopping a reply", async () => {
    const props = composerProps();
    const user = userEvent.setup();
    render(<Composer {...props} />);
    const trigger = screen.getByRole("button", { name: "Tools" });
    trigger.focus();
    await user.keyboard("{Enter}{Tab}{Tab}{Tab}{Enter}");
    expect(props.onWebSearchToggle).toHaveBeenCalledOnce();
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("region", { name: "Chat tools" })).toBeNull();
    expect(document.activeElement).toBe(trigger);
    expect(props.onStop).not.toHaveBeenCalled();
  });
  it("dismisses on outside pointer input and disables context changes during a stream", () => {
    const props = composerProps();
    render(<Composer {...props} isStreaming />);
    fireEvent.click(screen.getByRole("button", { name: "Tools" }));
    expect(screen.getByRole("button", { name: "Web" }).hasAttribute("disabled")).toBe(true);
    fireEvent.pointerDown(screen.getByRole("textbox"));
    expect(screen.queryByRole("region", { name: "Chat tools" })).toBeNull();
    expect(props.onStop).not.toHaveBeenCalled();
  });
});
