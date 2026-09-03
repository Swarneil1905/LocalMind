// Smoke test - Phase 0
// Verifies the App component mounts without throwing and renders key landmarks.
// No behaviour is tested here; that belongs to Phase 2+.

import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
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
    // "LocalMind" now appears in both the sidebar header and the chat empty
    // state, so use findAllByText and assert at least one match exists.
    expect((await screen.findAllByText("LocalMind")).length).toBeGreaterThan(0);
  });

  it("renders the composer textarea", async () => {
    render(<App />);
    // Placeholder is a textarea attribute, not a text node - use findByPlaceholderText
    // (async, waits for the post-loading render) rather than getByPlaceholderText.
    expect(await screen.findByPlaceholderText("Message LocalMind...")).toBeDefined();
  });
});
