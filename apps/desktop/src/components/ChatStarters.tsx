import { ArrowUpRight, Compass, Lightbulb, PenLine } from "lucide-react";

const STARTERS = [
  { label: "Think it through", detail: "Explore an idea", text: "Help me think through an idea: ", Icon: Lightbulb },
  { label: "Find the words", detail: "Make a first draft", text: "Help me write a clear reply to: ", Icon: PenLine },
  { label: "Make a plan", detail: "Find your next step", text: "Help me break this goal into steps: ", Icon: Compass },
];

export function ChatStarters({ onChoose }: { onChoose: (prompt: string) => void }) {
  return <div className="starter-prompts" aria-label="Ways to start a conversation">
    {STARTERS.map(({ label, detail, text, Icon }) => <button type="button" key={label} onClick={() => onChoose(text)}>
      <Icon size={19} strokeWidth={1.6} aria-hidden="true" />
      <span><strong>{label}</strong><small>{detail}</small></span>
      <ArrowUpRight size={14} aria-hidden="true" />
    </button>)}
  </div>;
}
