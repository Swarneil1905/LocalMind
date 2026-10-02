import { ArrowRight, MicOff } from "lucide-react";
import "./supporting-pages.css";

export function BuddyPage({
  onNavigateToChat,
}: {
  onNavigateToChat?: () => void;
}) {
  return (
    <div className="workspace-page">
      <div className="workspace-page-inner">
        <header className="page-heading">
          <div>
            <h1>Voice</h1>
            <p>Voice conversations with your local assistant.</p>
          </div>
          <span className="page-badge">Not available</span>
        </header>
        <section className="page-empty-state">
          <div className="empty-icon">
            <MicOff size={25} strokeWidth={1.5} />
          </div>
          <h2>Voice isn’t connected yet</h2>
          <p>
            This version supports text conversations. No microphone audio is
            being captured.
          </p>
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
