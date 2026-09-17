// The one build-time environment variable the shared client reads (the sign-in site base, used by
// auth/enrollRequest.ts). Declared locally so the package type-checks on its own without depending on
// the Vite client types - each shell owns its own Vite config and augments import.meta.env there.
interface ImportMetaEnv {
  readonly VITE_DT_SITE_BASE?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}

// Side-effect stylesheet imports (issue #1288): a shared component can import its own .css so its
// styles travel with it into every shell. Declared here so the package type-checks on its own with
// noUncheckedSideEffectImports, without depending on the Vite client types each shell owns.
declare module "*.css" {}

// A file imported as text (Vite's ?raw suffix): the dev report host bundles the note-taking script this way,
// from its one source file, rather than keeping a copy.
declare module "*?raw" {
  const content: string;
  export default content;
}
