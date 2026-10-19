import { build } from "esbuild";
import { mkdirSync, writeFileSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const out = resolve("artifacts/license-alarm-layout");
mkdirSync(out, { recursive: true });
const result = await build({
  entryPoints: ["scripts/qa/license-alarm-layout.tsx"], bundle: true, write: false,
  outfile: resolve(out, "app.js"), format: "iife", jsx: "automatic", loader: { ".svg": "dataurl", ".png": "dataurl" },
  define: { "process.env.NODE_ENV": '"production"', "import.meta.env": "{}",
    __COCKPIT_COMMIT__: '"layout-test"', __COCKPIT_BUILD_TIME__: '"2026-10-19T08:00:00Z"' },
  plugins: [{ name: "raw-text", setup(builder) {
    builder.onResolve({ filter: /\?raw$/ }, args => ({ path: resolve(args.resolveDir, args.path.slice(0, -4)), namespace: "raw-text" }));
    builder.onLoad({ filter: /.*/, namespace: "raw-text" }, args => ({ contents: readFileSync(args.path, "utf8"), loader: "text" }));
  } }],
});
const script = result.outputFiles.find(file => file.path.endsWith(".js"));
const style = result.outputFiles.find(file => file.path.endsWith(".css"));
if (!script || !style) throw new Error("The test build did not produce both script and stylesheet");
const html = `<!doctype html><html><head><meta charset="utf-8"><title>License alarm layout test</title><style>${style.text}</style></head><body><div id="root"></div><script>${script.text.replaceAll("</script", "<\\/script")}</script></body></html>`;
writeFileSync(resolve(out, "index.html"), html);
console.log(pathToFileURL(resolve(out, "index.html")).href);
