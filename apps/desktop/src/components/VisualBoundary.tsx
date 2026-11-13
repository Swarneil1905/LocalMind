import { Component, type ReactNode } from "react";

/** A renderer failure must not unmount the transcript or its composer. */
export class VisualBoundary extends Component<{ children: ReactNode; onClose: () => void }, { failed: boolean; retried: boolean }> {
  state = { failed: false, retried: false };
  static getDerivedStateFromError() { return { failed: true }; }
  render() {
    if (!this.state.failed) return this.props.children;
    return <section role="alert" className="visual-card">
      <p>This visual could not be displayed. Your answer and source are still available.</p>
      {!this.state.retried && <button onClick={() => this.setState({ failed: false, retried: true })}>Retry visual once</button>}
      <button onClick={this.props.onClose}>Close failed visual</button>
    </section>;
  }
}
