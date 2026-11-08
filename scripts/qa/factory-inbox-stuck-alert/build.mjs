// Builds the two capture pages for the issue 4746 proof screenshots: node scripts/qa/factory-inbox-stuck-alert/build.mjs
// Run from the repository root. Prints the two file addresses to open (Cockpit at desktop width, phone at 390px).
import { build } from "esbuild";
import { mkdirSync, writeFileSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const out = resolve("artifacts/factory-inbox-stuck-alert");
mkdirSync(out, { recursive: true });
for (const page of ["cockpit", "phone"]) {
  const result = await build({
    entryPoints: [`scripts/qa/factory-inbox-stuck-alert/${page}.tsx`], bundle: true, write: false,
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
  const html = `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Stuck inbox alert - ${page}</title><style>${style.text}</style></head><body><div id="root"></div><script>${script.text.replaceAll("</script", "<\\/script")}</script></body></html>`;
  writeFileSync(resolve(out, `${page}.html`), html);
  console.log(pathToFileURL(resolve(out, `${page}.html`)).href);
}
