// ESLint flat config — requires ESLint 9+
// Spec reference: Section 25, Step 8 (CI quality gates)

import js from "@eslint/js";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import globals from "globals";

export default tseslint.config(
  // Base JS recommended rules
  js.configs.recommended,

  // TypeScript recommended rules (type-aware disabled for speed in CI)
  ...tseslint.configs.recommended,

  // React Hooks rules
  {
    plugins: { "react-hooks": reactHooks },
    rules: reactHooks.configs.recommended.rules,
  },

  // Project-wide settings
  {
    languageOptions: {
      globals: {
        ...globals.browser,
        ...globals.es2022,
      },
    },
    rules: {
      // Allow unused vars that start with _ (conventional ignore prefix)
      "@typescript-eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_" },
      ],
      // Allow explicit any in early-phase code; tighten in Phase 2
      "@typescript-eslint/no-explicit-any": "warn",
    },
  },

  // Ignore built output and config files that don't need linting
  {
    ignores: ["dist/", "node_modules/", "*.config.js", "*.config.ts"],
  },
);
