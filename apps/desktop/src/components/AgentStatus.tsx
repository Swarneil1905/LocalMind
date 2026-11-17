import { useEffect, useState } from "react";
import { Check } from "lucide-react";

export type AgentPhase = "thinking" | "searching" | "reading";

const STEPS: Record<"local" | "web", { phase: AgentPhase; label: string; done: string }[]> = {
  local: [
    { phase: "thinking", label: "Thinking", done: "Thought it through" },
  ],
  web: [
    { phase: "searching", label: "Searching the web", done: "Searched the web" },
    { phase: "reading", label: "Reading sources", done: "Read sources" },
  ],
};

/** Live status for a response that has no text yet: a shimmering label, plus finished steps. */
export function AgentStatus({ phase, sources, web }: { phase: AgentPhase; sources: number; web: boolean }) {
  const [seconds, setSeconds] = useState(0);
  useEffect(() => {
    const started = Date.now();
    const timer = setInterval(() => setSeconds(Math.floor((Date.now() - started) / 1000)), 1000);
    return () => clearInterval(timer);
  }, []);
  const steps = STEPS[web ? "web" : "local"];
  const current = Math.max(0, steps.findIndex(step => step.phase === phase));
  const label = phase === "reading" && sources
    ? `Reading ${sources} ${sources === 1 ? "source" : "sources"}`
    : steps[current].label;
  return (
    <div className="agent-status" role="status" aria-live="polite">
      {steps.slice(0, current).map(step => (
        <p className="agent-step is-done" key={step.phase}><Check size={13} aria-hidden="true" />{step.done}</p>
      ))}
      <p className="agent-step is-active">
        <span className="shimmer-text">{label}</span>
        {seconds >= 3 && <span className="agent-elapsed">{seconds}s</span>}
      </p>
    </div>
  );
}
