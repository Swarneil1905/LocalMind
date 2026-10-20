import { defineConfig, searchForWorkspaceRoot } from "vite";
import react from "@vitejs/plugin-react";
// @ts-expect-error this configuration runs in Node; the app has no Node type declarations
import { resolve } from "node:path";

// @ts-expect-error process is a nodejs global
const host = process.env.TAURI_DEV_HOST;
// @ts-expect-error process is a nodejs global
const appRoot = process.cwd();
// The desktop package also has a pnpm workspace file. Search above it for shared dependencies.
const sharedWorkspaceRoot = searchForWorkspaceRoot(resolve(appRoot, ".."));

// https://vite.dev/config/
export default defineConfig(async () => ({
  plugins: [react()],

  // Vite options tailored for Tauri development and only applied in `tauri dev` or `tauri build`
  //
  // 1. prevent Vite from obscuring rust errors
  clearScreen: false,
  // 2. tauri expects a fixed port, fail if that port is not available
  server: {
    port: 1420,
    strictPort: true,
    host: host || false,
    fs: {
      // pnpm resolves bundled fonts through the workspace's shared dependencies.
      allow: [appRoot, `${sharedWorkspaceRoot}/node_modules`],
    },
    hmr: host
      ? {
          protocol: "ws",
          host,
          port: 1421,
        }
      : undefined,
    watch: {
      // 3. tell Vite to ignore watching `src-tauri`
      ignored: ["**/src-tauri/**"],
    },
  },
}));
