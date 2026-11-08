// Builds the capture page for the Web UI Polish phase 3 proof screenshots: node scripts/qa/web-ui-polish-p3/build.mjs
// Run from the repository root. Prints the file address; add a hash naming the page: settings, about, alerts, new,
// file, report or session.
import { build } from "esbuild";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const out = resolve("artifacts/web-ui-polish-p3");
mkdirSync(out, { recursive: true });
const result = await build({
  entryPoints: ["scripts/qa/web-ui-polish-p3/phone.tsx"], bundle: true, write: false,
  outfile: resolve(out, "phone.js"), format: "iife", jsx: "automatic", loader: { ".svg": "dataurl", ".png": "dataurl" },
  define: { "process.env.NODE_ENV": '"production"', "import.meta.env": "{}",
    __MOBILE_COMMIT__: '"capture"', __MOBILE_BUILD_TIME__: '"2026-11-08T12:00:00Z"' },
  // Vite's ?raw imports, as the phone's own build resolves them.
  plugins: [{ name: "raw-text", setup(builder) {
    builder.onResolve({ filter: /\?raw$/ }, args => ({ path: resolve(args.resolveDir, args.path.slice(0, -4)), namespace: "raw-text" }));
    builder.onLoad({ filter: /.*/, namespace: "raw-text" }, args => ({ contents: readFileSync(args.path, "utf8"), loader: "text" }));
  } }],
});
const script = result.outputFiles.find(file => file.path.endsWith(".js"));
const style = result.outputFiles.find(file => file.path.endsWith(".css"));
if (!script || !style) throw new Error("The phone build did not produce both script and stylesheet");
const html = `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Web UI Polish phase 3 - phone</title><style>${style.text}</style></head><body><div id="root"></div><script>${script.text.replaceAll("</script", "<\/script")}</script></body></html>`;
writeFileSync(resolve(out, "phone.html"), html);
console.log(pathToFileURL(resolve(out, "phone.html")).href);
