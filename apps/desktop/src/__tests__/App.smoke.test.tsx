// Smoke test — Phase 0
// Verifies the App component mounts without throwing and renders key landmarks.
// No behaviour is tested here; that belongs to Phase 2+.

import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import App from "../App";

describe("App smoke test", () => {
  it("renders without crashing", () => {
    render(<App />);
  });

  it("renders the LocalMind brand name in the sidebar", () => {
    render(<App />);
    expect(screen.getByText("LocalMind")).toBeDefined();
  });

  it("renders the composer textarea", () => {
    render(<App />);
    // Placeholder is a textarea attribute, not a text node — use getByPlaceholderText
    expect(screen.getByPlaceholderText("Message LocalMind")).toBeDefined();
  });
});
