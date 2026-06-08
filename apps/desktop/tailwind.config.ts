import type { Config } from "tailwindcss";

// Spec reference: Section 16 (Design System)
// Colors are CSS custom properties — do not hardcode hex values in components.
// Font sizes, border radii, and shadows match the spec exactly.

const config: Config = {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  darkMode: "class",
  theme: {
    // Override Tailwind defaults to match spec font scale (Section 16)
    fontSize: {
      xs:   ["11px", { lineHeight: "1.3" }],
      sm:   ["12px", { lineHeight: "1.5" }],
      base: ["13px", { lineHeight: "1.5" }],
      md:   ["14px", { lineHeight: "1.5" }],
      lg:   ["16px", { lineHeight: "1.5" }],
      xl:   ["18px", { lineHeight: "1.3" }],
      "2xl":["22px", { lineHeight: "1.3" }],
    },
    // Override Tailwind defaults to match spec border radius scale
    borderRadius: {
      none: "0px",
      sm:   "3px",
      DEFAULT: "6px",
      md:   "6px",
      lg:   "10px",
      full: "9999px",
    },
    extend: {
      colors: {
        bg:           "var(--bg)",
        surface:      "var(--surface)",
        surface2:     "var(--surface-2)",
        surface3:     "var(--surface-3)",
        bdr:          "var(--border)",
        "bdr-subtle": "var(--border-subtle)",
        fg:           "var(--text)",
        fg2:          "var(--text-2)",
        fg3:          "var(--text-3)",
        accent:       "var(--accent)",
        "accent-h":   "var(--accent-hover)",
        success:      "var(--success)",
        warning:      "var(--warning)",
        danger:       "var(--error)",
        info:         "var(--info)",
      },
      boxShadow: {
        sm: "0 1px 3px rgba(0,0,0,0.3)",
        md: "0 4px 12px rgba(0,0,0,0.4)",
        lg: "0 8px 24px rgba(0,0,0,0.5)",
      },
    },
  },
  plugins: [],
};

export default config;
