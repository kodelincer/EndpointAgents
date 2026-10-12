/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The bundle is served by WebView2 from a local folder mapping, so all paths stay relative.
export default defineConfig({
  base: "./",
  plugins: [react()],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    assetsInlineLimit: 0,
    target: "chrome110",
  },
  test: {
    environment: "node",
  },
});
