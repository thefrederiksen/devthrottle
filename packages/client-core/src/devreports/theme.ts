// The look the note tray takes inside a report frame (handoff ruling 6, CONTRACT.md "The tray's theme").
// The app's CSS custom properties do not cross into the sandboxed frame, so the host passes the values on
// the injected script element and the script bakes them into its shadow-root stylesheet.
//
// These are the Cockpit's and the phone's shared dark tokens (apps/cockpit/src/styles.css,
// apps/mobile/src/styles.css) and the app's type. They are also the note-taking script's own default.

export interface DevReportTheme {
  background: string;
  surface: string;
  surface2: string;
  border: string;
  text: string;
  textDim: string;
  accent: string;
  accentText: string;
  font: string;
  monoFont: string;
}

export const APP_DEV_REPORT_THEME: DevReportTheme = {
  background: "#0b1020",
  surface: "#141a2e",
  surface2: "#1b2238",
  border: "#28304a",
  text: "#e6e9f2",
  textDim: "#99a0b8",
  accent: "#3b82f6",
  accentText: "#ffffff",
  font: '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif',
  monoFont: '"Cascadia Mono", Consolas, Menlo, monospace',
};
