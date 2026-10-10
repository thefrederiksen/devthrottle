import { useEffect, useState } from "react";
import {
  setDailyReportCadence,
  setMentorReportEnabled,
  setSnoozePresets,
  setTimeZone,
  type ReportCadence,
  type SnoozePresets,
} from "./settingsClient";
import { formatSnoozeLength, snoozeDraftFrom, snoozeMinutesFrom, type SnoozeUnit } from "./snoozeFormat";
import { ACCOUNT_SCOPE, CardHead, errText, useGatewaySettings } from "./settingsShared";
import { disablePush, enablePush, isPushSubscribed, notificationPermission, pushSupported } from "../push/register";
import "./settings.css";

// ---- "Notifications" tab: how a session that needs you reaches you --------------------------------
//
// Snooze, the display time zone, and notifications are all preferences about how the fleet reaches and
// reads to you, so they sit together. The time zone moved here from the retired "This machine" tab
// (issue #2022): it is a per-tenant display preference - which local hours the dashboards read in - not
// a fact about the host, so it belongs with the account settings, not on the About page.
//
// Shared by the Cockpit and the phone. The phone had NONE of this before the surfaces were unified: it
// could snooze a session but not say for how long, and its notifications could only be turned on from a
// button on the roster.

export function NotificationsTab() {
  return (
    <>
      <SnoozeCard />
      <TimeZoneCard />
      <NotificationsCard />
      <DailyReportCard />
      <MentorReportCard />
    </>
  );
}

// The snooze lengths and which of them is the default (Snooze Length mission). A Gateway setting, so it
// owns its own load of the settings document (one fetch for this tab).
//
// The list and its default are ONE form: the radio that marks the default is a row in the same list you
// edit, so there is no separate "default" control that could name a length the list does not offer. Every
// change writes both through setSnoozePresets in a single call, which is what holds that invariant.
//
// Exported so the browser proof harness (browser-tests/snooze-presets-proof) can mount this exact
// component against a fake Gateway. Otherwise it is reached through the Settings tabs.
export function SnoozeCard() {
  const { settings, setSettings, error, busy, msg, runSave } = useGatewaySettings();
  // The row being added or edited: its index (null when adding) and the drafted number + unit. Null when
  // the editor is closed.
  const [editing, setEditing] = useState<{ index: number | null; count: string; unit: SnoozeUnit } | null>(null);

  if (error !== null) {
    return <div className="settings-error">Could not load the snooze setting: {error}</div>;
  }
  if (settings === null) {
    return <p className="settings-loading">Loading...</p>;
  }

  const presets = settings.snoozePresets;
  const max = settings.snoozeMaxPresets;
  const isFull = presets.length >= max;

  // Every edit goes through here: it writes the whole list plus the default in one call, so a
  // half-applied change can never leave a default that is not on the menu.
  const save = (next: number[], nextDefault: number, describe: (applied: SnoozePresets) => string) => {
    if (busy) return;
    void runSave(async () => {
      const applied = await setSnoozePresets(next, nextDefault);
      setSettings({
        ...settings,
        snoozePresets: applied.presets,
        snoozeDefaultMinutes: applied.defaultMinutes,
        snoozeMaxPresets: applied.maxPresets,
      });
      setEditing(null);
      return describe(applied);
    });
  };

  const makeDefault = (minutes: number) =>
    save(presets, minutes, (a) => `One click on Snooze now holds a session for ${formatSnoozeLength(a.defaultMinutes)}.`);

  const removeLength = (minutes: number) => {
    const next = presets.filter((m) => m !== minutes);
    // Deleting the default moves it to the shortest remaining length rather than refusing the delete:
    // the list can never be empty (the last row's Remove is disabled) so there is always one to move to.
    const nextDefault = minutes === settings.snoozeDefaultMinutes ? Math.min(...next) : settings.snoozeDefaultMinutes;
    save(next, nextDefault, () =>
      minutes === settings.snoozeDefaultMinutes
        ? `Removed ${formatSnoozeLength(minutes)}. The default is now ${formatSnoozeLength(nextDefault)}.`
        : `Removed ${formatSnoozeLength(minutes)}.`,
    );
  };

  const commitEdit = () => {
    if (editing === null) return;
    const minutes = snoozeMinutesFrom(editing.count, editing.unit);
    if (minutes === null) return;
    const previous = editing.index === null ? null : presets[editing.index];
    const next = previous === null ? [...presets, minutes] : presets.map((m) => (m === previous ? minutes : m));
    // Editing the row that holds the default keeps the default on that row.
    const nextDefault =
      previous !== null && previous === settings.snoozeDefaultMinutes ? minutes : settings.snoozeDefaultMinutes;
    save(next, nextDefault, () => `Saved ${formatSnoozeLength(minutes)}.`);
  };

  return (
    <section className="settings-card">
      <CardHead title="Snooze" scope={ACCOUNT_SCOPE} />
      <p className="settings-hint">
        How long a snoozed session stays parked before it returns to &quot;needs you&quot; on its own - a
        dead-man&apos;s switch, so a session you snooze can never be lost. These are the lengths every Snooze
        menu offers, the same on every device. Read at snooze time, so a change applies to the next snooze.
      </p>

      <ul className="snooze-list">
        {presets.map((minutes, index) => (
          <li key={minutes} className="snooze-row">
            <label className="snooze-row-pick">
              <input
                type="radio"
                name="snooze-default"
                checked={minutes === settings.snoozeDefaultMinutes}
                disabled={busy}
                onChange={() => makeDefault(minutes)}
              />
              <span className="snooze-row-name">{formatSnoozeLength(minutes)}</span>
            </label>
            <button
              type="button"
              className="settings-btn"
              disabled={busy}
              onClick={() => setEditing({ index, ...snoozeDraftFrom(minutes) })}
            >
              Edit
            </button>
            <button
              type="button"
              className="settings-btn"
              // The menu can never be empty, so the last remaining length cannot be removed.
              disabled={busy || presets.length === 1}
              title={presets.length === 1 ? "You need at least one snooze length." : undefined}
              onClick={() => removeLength(minutes)}
            >
              Remove
            </button>
          </li>
        ))}
      </ul>

      <p className="settings-hint">
        The dot is the default: what one click on Snooze, the phone, and voice all use. {presets.length} of{" "}
        {max} lengths used.
      </p>

      {editing !== null ? (
        <div className="settings-field">
          <label htmlFor="settings-snooze-count">Length</label>
          <input
            id="settings-snooze-count"
            className="settings-input"
            type="number"
            min={1}
            step={1}
            value={editing.count}
            disabled={busy}
            onChange={(e) => setEditing({ ...editing, count: e.target.value })}
            onKeyDown={(e) => {
              if (e.key === "Enter") commitEdit();
              if (e.key === "Escape") setEditing(null);
            }}
          />
          <select
            className="settings-input"
            aria-label="Unit"
            value={editing.unit}
            disabled={busy}
            onChange={(e) => setEditing({ ...editing, unit: e.target.value as SnoozeUnit })}
          >
            <option value="minutes">minutes</option>
            <option value="hours">hours</option>
            <option value="days">days</option>
          </select>
          <button
            type="button"
            className="settings-btn"
            disabled={busy || snoozeMinutesFrom(editing.count, editing.unit) === null}
            onClick={() => commitEdit()}
          >
            Save
          </button>
          <button type="button" className="settings-btn" disabled={busy} onClick={() => setEditing(null)}>
            Cancel
          </button>
        </div>
      ) : (
        <button
          type="button"
          className="settings-btn"
          disabled={busy || isFull}
          title={isFull ? `A snooze menu holds at most ${max} lengths. Remove one first.` : undefined}
          onClick={() => setEditing({ index: null, count: "30", unit: "minutes" })}
        >
          Add a length
        </button>
      )}

      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}

// The display time zone (issue #2022 - relocated here from the retired "This machine" tab). A per-account
// preference, so it owns its own load of the settings document (one fetch), the same pattern SnoozeCard uses.
function TimeZoneCard() {
  const { settings, setSettings, error, busy, msg, runSave } = useGatewaySettings();

  if (error !== null) {
    return <div className="settings-error">Could not load the time zone setting: {error}</div>;
  }
  if (settings === null) {
    return <p className="settings-loading">Loading...</p>;
  }

  const chooseTimeZone = (tz: string) => {
    if (busy || tz === settings.timeZone) return;
    void runSave(async () => {
      const applied = await setTimeZone(tz);
      setSettings({ ...settings, timeZone: applied });
      return `Time zone set to ${applied}. Your Throttle charts now read this zone.`;
    });
  };

  return (
    <section className="settings-card">
      <CardHead title="Display time zone" scope={ACCOUNT_SCOPE} />
      <p className="settings-hint">
        The zone your private dashboards read local clock hours in - the Your Throttle hourly charts. It
        starts on this Gateway machine&apos;s own zone ({settings.timeZoneMachineDefault}); change it if you
        want the charts shown in a different zone. Read at render time, so it applies on the next refresh.
      </p>
      <div className="settings-field">
        <label htmlFor="settings-timezone">Zone</label>
        <select
          id="settings-timezone"
          className="settings-select"
          value={settings.timeZone}
          disabled={busy}
          onChange={(e) => chooseTimeZone(e.target.value)}
        >
          {timeZoneOptions(settings.timeZone).map((tz) => (
            <option key={tz} value={tz}>
              {tz}
            </option>
          ))}
        </select>
        {settings.timeZone !== settings.timeZoneMachineDefault && (
          <button
            type="button"
            className="settings-btn"
            disabled={busy}
            onClick={() => chooseTimeZone(settings.timeZoneMachineDefault)}
          >
            Use gateway zone
          </button>
        )}
      </div>
      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}

// ---- Notifications (issue #1257) ------------------------------------------------------------------
//
// Per-device toggle that subscribes THIS browser - desktop tab or installed phone app - to the Gateway's
// "needs you" web push (#905) so a session turning red reaches you when DevThrottle is not in front of
// you. The subscribe/unsubscribe flow is the shared client-core push module; this card is only its on/off
// switch plus the browser permission prompt behind it. Self-contained state (support, permission,
// subscribed) so it never depends on the Gateway settings load - a push subscription is a property of
// this device, not of the account's Gateway config. Turning it off unsubscribes this device only.
//
// The wording is deliberately device-neutral rather than saying "browser": the identical card is what the
// phone now shows, and a phone that calls itself a browser reads as software talking about itself.
function NotificationsCard() {
  const supported = pushSupported();
  const [permission, setPermission] = useState<NotificationPermission | "unsupported">(notificationPermission());
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState("");

  useEffect(() => {
    if (!supported) {
      setEnabled(false);
      return;
    }
    let cancelled = false;
    void isPushSubscribed().then((on) => {
      if (!cancelled) setEnabled(on);
    });
    return () => {
      cancelled = true;
    };
  }, [supported]);

  const onToggle = async (checked: boolean) => {
    if (busy) return;
    setBusy(true);
    setMsg("Saving...");
    try {
      if (checked) {
        // enablePush names every outcome and carries the sentence for it, so the reason a switch-on
        // did not take is always shown. It used to collapse three different outcomes into "this
        // browser blocked notifications" - which is a false statement when the prompt was never
        // shown, and said nothing at all about permission being granted while REGISTERING the
        // device failed.
        const result = await enablePush();
        setPermission(notificationPermission());
        setEnabled(result.state === "granted");
        setMsg(
          result.state === "granted"
            ? "On. This device will be notified when a session needs you, even in the background."
            : result.message,
        );
      } else {
        await disablePush();
        setEnabled(false);
        setMsg("Off. Notifications stopped for this device only.");
      }
    } catch (e) {
      setMsg(errText(e, checked ? "turn notifications on" : "turn notifications off"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="settings-card">
      <CardHead title="Notifications" scope="this device" />
      <p className="settings-hint">
        Get a notification when a session needs you, even when DevThrottle is in the background or closed.
        Opening the notification brings DevThrottle forward on the session that is waiting. This is
        per-device: turning it off here stops notifications on this device only, and your other devices
        keep their own.
      </p>
      <label className="settings-check">
        <input
          type="checkbox"
          checked={enabled === true}
          disabled={!supported || busy || enabled === null || permission === "denied"}
          onChange={(e) => void onToggle(e.target.checked)}
        />
        Notify me on this device
      </label>
      {!supported && (
        <p className="settings-hint settings-hint-inline">This device does not support web notifications.</p>
      )}
      {supported && permission === "denied" && (
        <p className="settings-hint settings-hint-inline">
          Notifications are blocked for this site. Allow them in the browser&apos;s site settings to turn
          this on.
        </p>
      )}
      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}

// ---- The daily report email (issue #1000) ---------------------------------------------------------
//
// How often this ACCOUNT gets the morning report - the last of the "how the fleet reaches you" settings,
// and the only one that reaches you when you are not in the app at all, so it sits at the end of the tab.
//
// This question used to be asked by the first-run wizard, which runs once per director per machine: one
// person with three machines answered it three times for one email address, and nothing read the answer
// anyway. It belongs here because the Gateway is the only place the preference is true at - one account,
// one address, one answer - and it is read by the Gateway itself when the sender asks who to mail.
//
// Two choices, not three. The wizard also offered Weekly; the report covers one calendar day, so weekly
// would mail a Monday and call it a week. It comes back when the report can summarize a range.
export function DailyReportCard() {
  const { settings, setSettings, error, busy, msg, runSave } = useGatewaySettings();

  if (error !== null) {
    return <div className="settings-error">Could not load the daily report setting: {error}</div>;
  }
  if (settings === null) {
    return <p className="settings-loading">Loading...</p>;
  }

  const choose = (cadence: ReportCadence) => {
    if (busy || cadence === settings.dailyReportCadence) return;
    void runSave(async () => {
      const applied = await setDailyReportCadence(cadence);
      setSettings({ ...settings, dailyReportCadence: applied });
      return applied === "off"
        ? "Off. You will not get the daily report email."
        : "On. The report arrives every morning at 7:00 Eastern.";
    });
  };

  return (
    <section className="settings-card">
      <CardHead title="Daily report" scope={ACCOUNT_SCOPE} />
      <p className="settings-hint">
        A short email every morning at 7:00 Eastern: what your sessions did yesterday and what is waiting
        on you. It goes to your account&apos;s email address, so this is one setting for the whole account -
        every device you sign in on shows the same choice. Read when the email is sent, so a change applies
        to the next morning.
      </p>
      <div className="settings-field">
        <label className="settings-check">
          <input
            type="radio"
            name="daily-report-cadence"
            checked={settings.dailyReportCadence === "daily"}
            disabled={busy}
            onChange={() => choose("daily")}
          />
          Send it every morning
        </label>
        <label className="settings-check">
          <input
            type="radio"
            name="daily-report-cadence"
            checked={settings.dailyReportCadence === "off"}
            disabled={busy}
            onChange={() => choose("off")}
          />
          Do not send it
        </label>
      </div>
      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}

// ---- the Development Mentor report (devthrottle_internal#1661) ------------------------------------
//
// The second email about you that arrives on a rhythm, so it sits beside the daily report rather than on a
// tab of its own. Everything else in this file is about how a session that needs you reaches you; these two
// are about what the product sends you when nothing needs you at all.
//
// It is a CHECKBOX and not a pair of radios, which is the opposite of the card above it. The daily report
// answers "how often" and has a third answer waiting; this one answers "do you want this at all", and a
// question with two answers is not made clearer by drawing it as a list.
//
// The Gateway does not send this report - the mentor harness does, and it reads this setting straight out of
// the database when it runs. So the hint says the change applies to the next report rather than naming an
// hour, and it is written not to promise a rhythm the owner has not committed to publicly.
export function MentorReportCard() {
  const { settings, setSettings, error, busy, msg, runSave } = useGatewaySettings();

  if (error !== null) {
    return <div className="settings-error">Could not load the mentor report setting: {error}</div>;
  }
  if (settings === null) {
    return <p className="settings-loading">Loading...</p>;
  }

  const toggle = (enabled: boolean) => {
    if (busy || enabled === settings.mentorReportEnabled) return;
    void runSave(async () => {
      const applied = await setMentorReportEnabled(enabled);
      setSettings({ ...settings, mentorReportEnabled: applied });
      return applied
        ? "On. Your next mentor report will be sent."
        : "Off. No more mentor reports will be sent, and your prompts will not be read to write one.";
    });
  };

  return (
    <section className="settings-card">
      <CardHead title="Mentor report" scope={ACCOUNT_SCOPE} />
      <p className="settings-hint">
        Feedback on how you prompt: what you asked for over the week, where the asking cost you time, and
        what to try next. It is written by AI from your own prompts - no person reads them to produce it -
        and it goes to your account&apos;s email address. Turn it off here and no report is made for you and
        your prompts are not read to write one.
      </p>
      <div className="settings-field">
        <label className="settings-check">
          <input
            type="checkbox"
            checked={settings.mentorReportEnabled}
            disabled={busy}
            onChange={(e) => toggle(e.target.checked)}
          />
          Send me the mentor report
        </label>
      </div>
      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}

// The IANA time-zone options for the picker: the browser's full supported list (Intl.supportedValuesOf),
// with the current value guaranteed present even if the browser cannot enumerate zones.
function timeZoneOptions(current: string): string[] {
  let list: string[] = [];
  try {
    const supported = (Intl as unknown as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
    if (typeof supported === "function") list = supported("timeZone");
  } catch {
    /* Intl.supportedValuesOf unavailable - fall through to just the current value */
  }
  if (current && list.indexOf(current) < 0) list = [current, ...list];
  return list.length > 0 ? list : [current || "UTC"];
}
