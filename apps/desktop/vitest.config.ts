// Vitest configuration
// Spec reference: Section 25, Step 8 (CI quality gates)

import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  test: {
    // Use jsdom to simulate a browser DOM for React component tests
    environment: "jsdom",
    // Make vitest globals (describe, it, expect) available without importing
    globals: true,
    // Run setup file before each test suite (adds jest-dom matchers if needed later)
    setupFiles: [],
    include: ["src/**/*.{test,spec}.{ts,tsx}"],
  },
});
