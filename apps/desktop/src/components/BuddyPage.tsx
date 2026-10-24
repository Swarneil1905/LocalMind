import { useMemo, useState } from "react";
import { ArrowRight } from "lucide-react";
import { syntheticRecorder, type RecorderScenario } from "../lib/syntheticRecorder";
import { VoiceRecorder } from "./VoiceRecorder";
import "./supporting-pages.css";

export function BuddyPage({
  onNavigateToChat,
}: {
  onNavigateToChat?: () => void;
}) {
  const [scenario, setScenario] = useState<RecorderScenario>("granted");
  const recorder = useMemo(() => syntheticRecorder(scenario), [scenario]);
  return (
    <div className="workspace-page">
      <div className="workspace-page-inner">
        <header className="page-heading">
          <div>
            <h1>Voice</h1>
            <p>Try the recording and transcript controls.</p>
          </div>
          <span className="page-badge">Simulation</span>
        </header>
        <section>
          <p>No microphone audio is captured. The transcript is sample text; speech recognition and spoken replies are not connected yet.</p>
          <label className="voice-field">Simulation scenario<select className="page-input" value={scenario} onChange={event => setScenario(event.target.value as RecorderScenario)}>
            <option value="granted">Allowed</option><option value="denied">Permission denied</option><option value="missing">No input device</option>
          </select></label>
          <VoiceRecorder key={scenario} recorder={recorder} />
          {onNavigateToChat && (
            <button className="page-button primary" onClick={onNavigateToChat}>
              Open chat <ArrowRight size={15} />
            </button>
          )}
        </section>
      </div>
    </div>
  );
}
