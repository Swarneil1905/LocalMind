import { Compass, Lightbulb, PenLine } from "lucide-react";

const STARTERS = [
  { label: "Think it through", text: "Help me think through an idea: ", Icon: Lightbulb },
  { label: "Find the words", text: "Help me write a clear reply to: ", Icon: PenLine },
  { label: "Make a plan", text: "Help me break this goal into steps: ", Icon: Compass },
];

export function ChatStarters({ onChoose }: { onChoose: (prompt: string) => void }) {
  return <div className="starter-prompts" aria-label="Ways to start a conversation">
    {STARTERS.map(({ label, text, Icon }) => <button type="button" key={label} onClick={() => onChoose(text)}>
      <Icon size={16} strokeWidth={1.6} aria-hidden="true" />
      <span><strong>{label}</strong></span>
    </button>)}
  </div>;
}
