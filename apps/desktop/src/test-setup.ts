// Test environment setup
// Mocks Tauri APIs that require window.__TAURI_INTERNALS__ (not present in jsdom).
// All components that call invoke() or listen() will get silent no-ops in tests.

import { vi } from "vitest";

vi.mock("@tauri-apps/api/event", () => ({
  listen: vi.fn(() => Promise.resolve(() => {})),
  emit: vi.fn(() => Promise.resolve()),
}));

vi.mock("@tauri-apps/api/core", () => ({
  invoke: vi.fn(() => Promise.resolve(null)),
}));
