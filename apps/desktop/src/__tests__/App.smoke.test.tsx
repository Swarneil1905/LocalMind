// Smoke test - Phase 0
// Verifies the App component mounts without throwing and renders key landmarks.
// No behaviour is tested here; that belongs to Phase 2+.

import { describe, it, expect } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import App from "../App";

describe("App smoke test", () => {
  it("renders without crashing", () => {
    render(<App />);
  });

  it("renders the LocalMind brand name in the sidebar", async () => {
    render(<App />);
    // App shows a "Connecting to local AI..." overlay until the mocked
    // invoke("list_ollama_models") promise resolves. Use the async findBy*
    // query (retries under the hood) instead of asserting immediately,
    // otherwise this runs before that microtask settles.
    // The sidebar starts collapsed (icon-only) by default, and the brand
    // name span only renders when expanded - expand it first.
    const expandToggle = await screen.findByTitle("Expand sidebar");
    fireEvent.click(expandToggle);
    expect((await screen.findAllByText("LocalMind")).length).toBeGreaterThan(0);
  });

  it("renders the composer textarea", async () => {
    render(<App />);
    // App defaults to the Today page; the composer only renders on the Chat
    // page, so navigate there first via the sidebar nav button.
    const chatNavButton = await screen.findByTitle("Chat");
    fireEvent.click(chatNavButton);
    // Placeholder is a textarea attribute, not a text node - use findByPlaceholderText
    // (async, waits for the post-loading render) rather than getByPlaceholderText.
    expect(await screen.findByPlaceholderText("Message LocalMind...")).toBeDefined();
  });
});
