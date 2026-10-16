// The phase 3 proof harness (mission #4070): serves main.tsx, which mounts the SHARED live Chat from client-core inside
// one shell's own stylesheet, fed by the same fake stream the tests use. Run by capture.mjs; never shipped.
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";

const repo = fileURLToPath(new URL("../../../../../../", import.meta.url));

export default defineConfig({
  root: fileURLToPath(new URL(".", import.meta.url)),
  server: { port: 5199, strictPort: true, fs: { allow: [repo] } },
  esbuild: { jsx: "automatic" },
  logLevel: "warn",
});
