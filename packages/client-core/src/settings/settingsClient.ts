// The Gateway per-account settings surface (issue #1025, epic #967): the typed, same-origin client the
// React Cockpit's Settings page reads and drives for everything that is NOT the AI tab (that tab reuses
// ../api/ai).
//
// Issue #2022 - the machine settings LEFT this surface. The "This machine" tab was retired: process
// diagnostics + the auto-resolved address + version are now read-only on the About page (aboutClient),
// start-at-login is the installer plus the `cc-devthrottle autostart` command, network addressing is
// dropped, and brain restart/config is gone. So this client no longer has an addressing-mode or autostart
// setter, and GET /gateway/settings now carries only the per-account settings the collapsed page renders.
//
// Every request is root-relative to the Gateway front door (never a Director address) and carries the
// same Bearer via authHeaders(). A user action throws GatewayError carrying the Gateway's own message on
// a non-2xx (the no-fallback rule).
import { authHeaders, GatewayError } from "../api/client";

/** The GET /gateway/settings snapshot the collapsed per-account Settings page renders (issue #2022). */
export interface GatewaySettings {
  // Snooze Length mission: the per-user default snooze length in minutes (one value across every device,
  // because they all talk to this one Gateway). Default 60. Always one of snoozePresets.
  snoozeDefaultMinutes: number;
  // The lengths every Snooze menu offers, ascending. Shipped as [15, 60, 240, 480] until the user edits
  // them. Gateway-owned like the default, so the same lengths appear on desktop, phone, and Cockpit.
  snoozePresets: number[];
  // The most lengths the list may hold, so the Settings page can disable "Add a length" when full.
  snoozeMaxPresets: number;
  // The display time zone (IANA id) the private dashboards' hourly charts read local hours in. Auto-
  // defaults to the Gateway machine's own zone when unset; the owner can override it here.
  timeZone: string;
  // What "automatic" resolves to: the Gateway machine's own zone. Lets the page show it and offer a reset.
  timeZoneMachineDefault: string;
  // How often this account wants the daily report email (issue #1000). One value for the account, not for
  // this device or this machine: the report is one person and one email, which is exactly why the question
  // left the first-run wizard, where it was asked once per install.
  dailyReportCadence: ReportCadence;
  // Whether this account receives the Development Mentor report (devthrottle_internal#1661). One value for
  // the account: the mentor reads one person's own prompts and writes to one person.
  mentorReportEnabled: boolean;
  // Whether this account's stops are judged at all (the Wingman-on-every-turn mission). Off means no model
  // call is made at a turn end and no verdict is recorded.
  turnVerdictJudgeEnabled: boolean;
  // Whether judged verdicts reach this account's screens - the calm colours and the one-line label. Off
  // with judging ON is the shadow state: every verdict is stored and gradeable and no row's colour moves.
  turnVerdictColourEnabled: boolean;
}

/**
 * How often the daily report email is sent. "weekly" is deliberately absent: the report covers one calendar
 * day, so a weekly send could only mail one day and call it a week. Add it here when the Gateway can
 * summarize a range - the wire value is a name precisely so that costs nothing stored.
 */
export type ReportCadence = "daily" | "off";

async function gatewayErrorFrom(res: Response, label: string): Promise<GatewayError> {
  let detail = `${res.status}`;
  try {
    const text = await res.text();
    if (text.length > 0) {
      try {
        const body = JSON.parse(text) as { error?: string; detail?: string };
        detail = body.error ?? body.detail ?? text;
      } catch {
        detail = text;
      }
    }
  } catch {
    /* body unreadable - keep the status code */
  }
  return new GatewayError(res.status, `${label} failed: ${detail}`);
}

async function getJson<T>(path: string, label: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, label);
  return (await res.json()) as T;
}

async function putJson<T>(path: string, label: string, body: unknown, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify(body),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, label);
  return (await res.json()) as T;
}

// GET /gateway/settings - the per-account snapshot the collapsed page renders (issue #2022). Throws on
// transport failure so the page shows an error banner rather than a fabricated empty state.
export async function getGatewaySettings(signal?: AbortSignal): Promise<GatewaySettings> {
  const body = (await getJson<Partial<GatewaySettings>>(
    "/gateway/settings",
    "GET /gateway/settings",
    signal,
  )) ?? {};
  return {
    snoozeDefaultMinutes: Number(body.snoozeDefaultMinutes ?? 60),
    snoozePresets: Array.isArray(body.snoozePresets)
      ? body.snoozePresets.map(Number)
      : [15, 60, 240, 480],
    snoozeMaxPresets: Number(body.snoozeMaxPresets ?? 5),
    timeZone: typeof body.timeZone === "string" && body.timeZone.length > 0 ? body.timeZone : "UTC",
    timeZoneMachineDefault:
      typeof body.timeZoneMachineDefault === "string" && body.timeZoneMachineDefault.length > 0
        ? body.timeZoneMachineDefault
        : "UTC",
    // Anything this client does not recognize reads as "daily" - the same direction the Gateway takes, and
    // for the same reason: a card that showed Off because it met a value it did not know would tell the
    // account its report is stopped when it is not.
    dailyReportCadence: body.dailyReportCadence === "off" ? "off" : "daily",
    // Only an explicit false reads as off, for the same reason the cadence above only reads "off" as off: a
    // card that showed Off because the field was missing - an older Gateway, a shape this client does not
    // know - would tell the account the mentor is stopped when it is not.
    mentorReportEnabled: body.mentorReportEnabled !== false,
    // The OPPOSITE direction from the two above, and deliberately so. Those default to ON when the field is
    // missing, because a card that showed Off for something still happening would tell the account it had
    // stopped. These default to OFF, because that is what a Gateway too old to know the field is actually
    // doing: it is not judging anything and it is not colouring anything. Showing On here would tell the
    // account its fleet is being judged when nothing is, and the switch it would then turn off does nothing.
    turnVerdictJudgeEnabled: body.turnVerdictJudgeEnabled === true,
    turnVerdictColourEnabled: body.turnVerdictColourEnabled === true,
  };
}

// PUT /gateway/turn-verdict-judge { enabled } - turn the Wingman's judging of this account's stops on or
// off (the Wingman-on-every-turn mission). Read by the turn-end seat at every stop, so a change applies to
// the next turn end. Returns the applied value.
export async function setTurnVerdictJudgeEnabled(enabled: boolean, signal?: AbortSignal): Promise<boolean> {
  const body = await putJson<{ enabled?: boolean }>(
    "/gateway/turn-verdict-judge",
    "PUT /gateway/turn-verdict-judge",
    { enabled },
    signal,
  );
  return body.enabled === true;
}

// PUT /gateway/turn-verdict-colour { enabled } - turn the judged verdicts' COLOURS on or off for this
// account. Separate from judging on purpose: off here with judging on is the shadow state. Returns the
// applied value.
export async function setTurnVerdictColourEnabled(enabled: boolean, signal?: AbortSignal): Promise<boolean> {
  const body = await putJson<{ enabled?: boolean }>(
    "/gateway/turn-verdict-colour",
    "PUT /gateway/turn-verdict-colour",
    { enabled },
    signal,
  );
  return body.enabled === true;
}

// PUT /gateway/daily-report { cadence } - set how often this account gets the daily report email. Read by
// the Gateway when the sender asks who to mail, so a change applies to the next morning's send. Returns the
// applied cadence.
export async function setDailyReportCadence(
  cadence: ReportCadence,
  signal?: AbortSignal,
): Promise<ReportCadence> {
  const body = await putJson<{ cadence?: string }>(
    "/gateway/daily-report",
    "PUT /gateway/daily-report",
    { cadence },
    signal,
  );
  return body.cadence === "off" ? "off" : "daily";
}

// PUT /gateway/mentor-report { enabled } - turn the Development Mentor report on or off for this account
// (devthrottle_internal#1661). The Gateway does not send that report; the harness reads this setting out of
// the database when it runs, so a change applies to the next run. Returns the applied value.
export async function setMentorReportEnabled(enabled: boolean, signal?: AbortSignal): Promise<boolean> {
  const body = await putJson<{ enabled?: boolean }>(
    "/gateway/mentor-report",
    "PUT /gateway/mentor-report",
    { enabled },
    signal,
  );
  return body.enabled !== false;
}

// PUT /gateway/time-zone { timeZone } - set the display time zone (an IANA id) the private dashboards read
// local hours in. Read at render time, so a change applies on the next refresh with no restart. Returns
// the applied id.
export async function setTimeZone(timeZone: string, signal?: AbortSignal): Promise<string> {
  const body = await putJson<{ timeZone?: string }>("/gateway/time-zone", "PUT /gateway/time-zone", { timeZone }, signal);
  return typeof body.timeZone === "string" && body.timeZone.length > 0 ? body.timeZone : timeZone;
}

// PUT /gateway/snooze-default { minutes } - set the per-user default snooze length (Snooze Length
// mission). Read at snooze time, so a change applies to the next snooze with no restart, and it is the
// same value on every device (all talk to this one Gateway). Returns the applied minutes.
export async function setSnoozeDefaultMinutes(minutes: number, signal?: AbortSignal): Promise<number> {
  const body = await putJson<{ minutes?: number }>("/gateway/snooze-default", "PUT /gateway/snooze-default", { minutes }, signal);
  return Number(body.minutes ?? minutes);
}

// The snooze lengths and which of them is the default, as one value. They travel together because they
// have an invariant between them: the default must be one of the lengths.
export interface SnoozePresets {
  presets: number[];
  defaultMinutes: number;
  maxPresets: number;
}

// PUT /gateway/snooze-presets { presets, defaultMinutes } - set the snooze lengths every Snooze menu
// offers and which one the plain Snooze click uses. Written in ONE call so the list and its default can
// never disagree. Read at snooze time, so a change applies to the next snooze with no restart, and it is
// the same on every device (all talk to this one Gateway). Returns the applied list, ascending.
export async function setSnoozePresets(
  presets: number[],
  defaultMinutes: number,
  signal?: AbortSignal,
): Promise<SnoozePresets> {
  const body = await putJson<Partial<SnoozePresets>>(
    "/gateway/snooze-presets",
    "PUT /gateway/snooze-presets",
    { presets, defaultMinutes },
    signal,
  );
  return {
    presets: Array.isArray(body.presets) ? body.presets.map(Number) : presets,
    defaultMinutes: Number(body.defaultMinutes ?? defaultMinutes),
    maxPresets: Number(body.maxPresets ?? 5),
  };
}

// Issue #2022: the machine-scoped setters that once lived here - setAddressingMode (network addressing was
// dropped, auto-resolved), setAutostart (start-at-login moved to the installer + the `cc-devthrottle
// autostart` command), and setTrainingCapture - are gone with the "This machine" tab. What remains is the
// per-account time zone + snooze setters above.
