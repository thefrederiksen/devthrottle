// Builds the capture page for the Web UI Polish phase 2 proof screenshots: node scripts/qa/web-ui-polish-p2/build.mjs
// Run from the repository root. Prints the file address; add a hash: session, schedule, directors or voice.
import { build } from "esbuild";
import { mkdirSync, writeFileSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const out = resolve("artifacts/web-ui-polish-p2");
mkdirSync(out, { recursive: true });
for (const page of ["cockpit"]) {
  const result = await build({
    entryPoints: [`scripts/qa/web-ui-polish-p2/${page}.tsx`], bundle: true, write: false,
    outfile: resolve(out, `${page}.js`), format: "iife", jsx: "automatic", loader: { ".svg": "dataurl", ".png": "dataurl" },
    define: { "process.env.NODE_ENV": '"production"', "import.meta.env": "{}",
      __COCKPIT_COMMIT__: '"capture"', __COCKPIT_BUILD_TIME__: '"2026-11-07T15:30:00Z"' },
    plugins: [{ name: "raw-text", setup(builder) {
      builder.onResolve({ filter: /\?raw$/ }, args => ({ path: resolve(args.resolveDir, args.path.slice(0, -4)), namespace: "raw-text" }));
      builder.onLoad({ filter: /.*/, namespace: "raw-text" }, args => ({ contents: readFileSync(args.path, "utf8"), loader: "text" }));
    } }],
  });
  const script = result.outputFiles.find(file => file.path.endsWith(".js"));
  const style = result.outputFiles.find(file => file.path.endsWith(".css"));
  if (!script || !style) throw new Error(`The ${page} build did not produce both script and stylesheet`);
  const html = `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Web UI Polish phase 2 - ${page}</title><style>${style.text}</style></head><body><div id="root"></div><script>${script.text.replaceAll("</script", "<\\/script")}</script></body></html>`;
  writeFileSync(resolve(out, `${page}.html`), html);
  console.log(pathToFileURL(resolve(out, `${page}.html`)).href);
}
