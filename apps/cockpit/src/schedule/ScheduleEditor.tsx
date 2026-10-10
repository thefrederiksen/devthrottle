import { useCallback, useEffect, useMemo, useState } from "react";
import {
  createCronJob,
  getSeatChoices,
  updateCronJob,
  type CronJob,
  type CronSeatChoices,
} from "@devthrottle/client-core/schedule/scheduleClient";
import {
  ENDPOINT_STATE_UNREACHABLE_BY_NAME,
  getFleetDirectors,
  getSessionsEnvelope,
  type DirectorReachability,
  type FleetDirector,
  type MachineError,
} from "@devthrottle/client-core/fleet/fleetClient";
import { canStartSessionOn } from "@devthrottle/client-core/fleet/directorPresentation";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { classify, dotHex, stateLabel } from "@devthrottle/client-core/sessions/ordering";
import { ConfirmDialog, useDismissOnBackdrop } from "../components";
import { cronToEnglish } from "./scheduleFormat";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-schedule-editor";

// The schedule editor: the large two-tab create/edit dialog (issue #1289), its machine picker (#495) and its
// unsaved-changes guard. It is its own component so the Schedule page and a factory's Seats tab open the SAME editor
// (the owner, 2026-10-09: a factory's schedules are changed in the factory). The host mounts it while it is open and
// is told when it closes and when a save landed; every write goes through client-core to the Gateway.

/** What the editor opens on: a new job (starting in the account's time zone) or an existing one. */
export type ScheduleEditorRequest = { kind: "create"; timeZone: string } | { kind: "edit"; job: CronJob };

// The create/edit form state, kept together so open/close/reset is one object (mirrors the Blazor
// _f* fields). enabled + preventOverlap have no form control but are preserved across an edit so
// renaming/rescheduling a disabled job never silently re-arms it (Blazor QA #488).
interface FormState {
  editingId: string | null;
  name: string;
  machine: string;
  repoPath: string;
  actionKind: "worklist" | "seed";
  workListName: string;
  seed: string;
  // "random" (issue #3622) is never offered for a new job here; it appears only when editing one made from
  // the command line, so an edit keeps the kind and its settings text rather than turning it into a one-off.
  // "window" likewise: the Gateway chooses its minute, and it is made from the command line.
  scheduleKind: "oneOff" | "recurring" | "random" | "window";
  cron: string;
  runAt: string;
  timeZone: string;
  notifyOn: "none" | "always" | "failure";
  notifyWebhookUrl: string;
  enabled: boolean;
  preventOverlap: boolean;
  // The factory and seat the schedule runs as, or "" for a schedule in no factory (the owner, 2026-10-10).
  factory: string;
  seat: string;
}

// Per-field validation messages for the create/edit form. A field is present here only when it is
// invalid, so an empty object means "minimally valid to submit" (issue #1027). We validate exactly
// the fields the Gateway needs to run a job: a name, a target machine, a repository path, and a
// schedule (a cron expression when recurring, or a run-at instant when one-off).
interface FormErrors {
  name?: string;
  machine?: string;
  repoPath?: string;
  schedule?: string;
  seat?: string;
}

function validateForm(f: FormState): FormErrors {
  const errors: FormErrors = {};
  if (f.name.trim().length === 0) errors.name = "Enter a name so you can find this job in the list.";
  if (f.machine.trim().length === 0) errors.machine = "Choose the machine this job runs on.";
  if (f.repoPath.trim().length === 0) errors.repoPath = "Enter the repository path the session opens in.";
  if (f.factory.length > 0 && f.seat.length === 0) errors.seat = "Choose the seat this schedule runs as.";
  if (f.scheduleKind === "recurring") {
    if (f.cron.trim().length === 0) errors.schedule = "Enter a 5-field cron expression, for example 0 0 * * *.";
  } else if (f.scheduleKind === "random") {
    if (f.cron.trim().length === 0) errors.schedule = "Enter the random settings, for example window=07:00-01:00 perDay=4 minGap=45.";
  } else if (f.scheduleKind === "window") {
    if (f.cron.trim().length === 0) errors.schedule = "Enter the window settings, for example window=00:00-06:30 deadline=07:00.";
  } else {
    if (f.runAt.trim().length === 0) errors.schedule = "Enter the local date and time to run once.";
  }
  return errors;
}

const EMPTY_FORM: FormState = {
  editingId: null,
  name: "",
  machine: "",
  repoPath: "",
  // New jobs default to the skill / prompt action. The "work list" action is hidden for now (the
  // named-work-list feature is being retired - GitHub issues are the queue), so no new work-list-
  // backed schedule can be created; see the hidden "What to run" selector below.
  actionKind: "seed",
  workListName: "",
  seed: "",
  scheduleKind: "oneOff",
  cron: "",
  runAt: "",
  timeZone: "",
  notifyOn: "none",
  notifyWebhookUrl: "",
  enabled: true,
  preventOverlap: true,
  factory: "",
  seat: "",
};

// The form for an existing job. Every field it has no control for is still carried, so an edit never drops it.
function formFromJob(job: CronJob): FormState {
  return {
    editingId: job.id,
    name: job.name,
    machine: job.target.machine,
    repoPath: job.action.repoPath,
    actionKind: job.action.workListName && job.action.workListName.length > 0 ? "worklist" : "seed",
    workListName: job.action.workListName ?? "",
    seed: job.action.seed,
    scheduleKind: formKind(job.scheduleKind),
    cron: job.cronExpression ?? "",
    runAt: job.runAt ?? "",
    timeZone: job.timeZoneId,
    notifyOn: normalizeNotify(job.notifyOn),
    notifyWebhookUrl: job.notifyWebhookUrl ?? "",
    enabled: job.enabled,
    preventOverlap: job.preventOverlap,
    factory: job.factory ?? "",
    seat: job.seat ?? "",
  };
}

export function ScheduleEditor({
  request,
  onClose,
  onSaved,
}: {
  request: ScheduleEditorRequest;
  /** The editor closed: cancelled, discarded, or saved. */
  onClose: () => void;
  /** A save landed; the host refreshes what it shows and reads the Gateway's load warning off the answer. */
  onSaved: (saved: CronJob) => void | Promise<void>;
}) {
  // The form as it opened, which is also the unsaved-changes baseline.
  const [initial] = useState<FormState>(() =>
    request.kind === "create" ? { ...EMPTY_FORM, timeZone: request.timeZone } : formFromJob(request.job),
  );

  // Director-picker source data (the machine picker's live preview).
  const [directors, setDirectors] = useState<FleetDirector[]>([]);
  const [sessions, setSessions] = useState<SessionDto[]>([]);
  const [machineErrors, setMachineErrors] = useState<MachineError[]>([]);
  // The per-Director reachability from the same envelope. A Director that was SHUT DOWN is absent from
  // machineErrors, because nothing failed - but a job pointed at it could not run either, so the picker
  // has to know about it too.
  const [directorReach, setDirectorReach] = useState<DirectorReachability[]>([]);
  const [machineFilter, setMachineFilter] = useState("");
  const [showDirectorPicker, setShowDirectorPicker] = useState(false);

  // Create/edit modal state. The editor is a large two-tab dialog (issue #1289): a "Settings" tab
  // with every field, and an "Instructions" tab where the prompt editor fills essentially the whole
  // tab. activeTab remembers which tab is showing; baseline is the form as it was when the dialog
  // opened, so unsaved edits can be detected and guarded (the dirty-tracking convention from #1255);
  // confirmDiscard drives the "discard unsaved changes?" guard when closing a dirty editor.
  const [saving, setSaving] = useState(false);
  const [form, setForm] = useState<FormState>(initial);
  const [baseline] = useState<FormState>(initial);
  const [activeTab, setActiveTab] = useState<"settings" | "instructions">("settings");
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  // The factory and seat picker's choices, folded by the Gateway from the registry a save is checked against.
  const [seatChoices, setSeatChoices] = useState<CronSeatChoices | null>(null);
  const [seatChoicesError, setSeatChoicesError] = useState<string | null>(null);
  useEffect(() => {
    const abort = new AbortController();
    getSeatChoices(abort.signal)
      .then(setSeatChoices)
      .catch((err: unknown) => {
        if (!abort.signal.aborted) setSeatChoicesError(`Could not read the factories: ${gatewayErrorMessage(err)}`);
      });
    return () => abort.abort();
  }, []);
  const chosenFactory = seatChoices?.factories.find((c) => c.factory === form.factory);

  const loadDirectors = useCallback(async () => {
    try {
      const [dirs, env] = await Promise.all([getFleetDirectors(), getSessionsEnvelope()]);
      setDirectors(dirs);
      setSessions(env.sessions);
      setMachineErrors(env.machineErrors); // error-report-exempt: the list of unreachable machines, produced and held by the Gateway; nothing failed in the Cockpit
      setDirectorReach(env.directors);
    } catch {
      /* the picker degrades to "no machines known" rather than blocking the form */
    }
  }, []);

  // The machine picker's live preview is read as the editor opens.
  useEffect(() => {
    void loadDirectors();
  }, [loadDirectors]);

  const buildFromForm = useCallback((f: FormState): CronJob => {
    return {
      id: "",
      name: f.name.trim(),
      enabled: f.enabled,
      scheduleKind: f.scheduleKind,
      cronExpression: f.scheduleKind === "oneOff" ? null : f.cron.trim(),
      runAt: f.scheduleKind === "oneOff" ? f.runAt.trim() : null,
      timeZoneId: f.timeZone.trim(),
      target: { machine: f.machine.trim() },
      action: {
        repoPath: f.repoPath.trim(),
        seed: f.actionKind === "seed" ? f.seed.trim() : "",
        workListName: f.actionKind === "worklist" ? f.workListName.trim() : null,
      },
      preventOverlap: f.preventOverlap,
      notifyOn: f.notifyOn,
      notifyWebhookUrl:
        f.notifyOn !== "none" && f.notifyWebhookUrl.trim().length > 0 ? f.notifyWebhookUrl.trim() : null,
      factory: f.factory.length > 0 ? f.factory : null,
      seat: f.seat.length > 0 ? f.seat : null,
    };
  }, []);

  // Live per-field validation of the open form. Empty object => minimally valid (issue #1027).
  const formErrors = useMemo(() => validateForm(form), [form]);
  const formValid = Object.keys(formErrors).length === 0;

  // Every validated field lives on the Settings tab, so a validation problem is a Settings-tab
  // problem. The tab shows a marker when it holds an unfilled required field, so a person editing on
  // the Instructions tab still sees why Save is disabled (issue #1289).
  const settingsHasError = !formValid;

  // Unsaved-edit tracking (the #1255 convention): the form is dirty when it differs from the value it
  // opened with. FormState is a flat object of primitives, so a stable JSON comparison is exact.
  const formDirty = useMemo(() => JSON.stringify(form) !== JSON.stringify(baseline), [form, baseline]);

  // Close the editor and clear its transient guard. Used by a clean close and after a saved or
  // discarded edit.
  const closeForm = useCallback(() => {
    setConfirmDiscard(false);
    onClose();
  }, [onClose]);

  // Cancel / backdrop / Escape all route through here so a dirty editor asks before dropping edits and
  // a clean one closes at once (issue #1289, guard style from #1255).
  const requestCloseForm = useCallback(() => {
    if (formDirty) {
      setConfirmDiscard(true);
    } else {
      closeForm();
    }
  }, [formDirty, closeForm]);

  // Escape closes the editor (through the unsaved-edit guard), but never while a save is in flight and
  // never when a nested dialog (the machine picker or the discard confirmation) is open on top of it.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape" && !saving && !showDirectorPicker && !confirmDiscard) {
        requestCloseForm();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [saving, showDirectorPicker, confirmDiscard, requestCloseForm]);

  // Backdrop dismissal for the editor and the machine picker. Both close only on a press that STARTED
  // on the backdrop, so selecting the instructions or a machine name with the mouse and releasing past
  // the panel edge is a selection, never a dismissal (see useDismissOnBackdrop).
  const dismissForm = useDismissOnBackdrop(requestCloseForm);
  const dismissDirectorPicker = useDismissOnBackdrop(useCallback(() => setShowDirectorPicker(false), []));

  const save = useCallback(async () => {
    // The Create/Save button is disabled while invalid, but guard here too so no code path can POST
    // an empty job (issue #1027).
    if (!formValid) return;
    setSaving(true);
    setFormError(null);
    try {
      const dto = buildFromForm(form);
      const saved = form.editingId === null ? await createCronJob(dto) : await updateCronJob(form.editingId, dto);
      closeForm();
      await onSaved(saved);
    } catch (err) {
      // Surface the Gateway's message (incl. a 400 for an invalid cron) inline in the form.
      setFormError(describeAndReport(SURFACE, "save the schedule", err));
    } finally {
      setSaving(false);
    }
  }, [buildFromForm, form, formValid, closeForm, onSaved]);

  // ---- Director picker (the machine picker, #495) ----
  const machines = useMemo(() => {
    const names = directors
      .map((d) => d.machineName ?? "")
      .filter((m) => m.trim().length > 0);
    const distinct = Array.from(new Set(names.map((m) => m)));
    // Case-insensitive de-dup + sort, matching the Blazor Machines() helper.
    const seen = new Map<string, string>();
    for (const m of distinct) {
      const key = m.toLowerCase();
      if (!seen.has(key)) seen.set(key, m);
    }
    return Array.from(seen.values()).sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase()));
  }, [directors]);

  const filteredMachines = useMemo(() => {
    const f = machineFilter.trim().toLowerCase();
    if (f.length === 0) return machines;
    return machines.filter((m) => m.toLowerCase().includes(f));
  }, [machines, machineFilter]);

  // "Cannot be given work right now", which is what this picker actually needs to know - not "is
  // something wrong". A shut-down Director answers no to the first and no to the second, so it is asked
  // through canStartSessionOn (the Gateway's own ruling) rather than through the failure list.
  const isUnreachable = useCallback(
    (d: FleetDirector): boolean =>
      d.advertisedEndpointState === ENDPOINT_STATE_UNREACHABLE_BY_NAME ||
      machineErrors.some((e) => (e.directorId ?? "").toLowerCase() === (d.directorId ?? "").toLowerCase()) ||
      !canStartSessionOn(
        directorReach.find((r) => (r.directorId ?? "").toLowerCase() === (d.directorId ?? "").toLowerCase()),
      ),
    [machineErrors, directorReach],
  );

  return (
    <>
      <div className="sched-modal-backdrop" {...dismissForm}>
        {/* The large two-tab editor (issue #1289): about 70 percent of the viewport, with a Settings
            tab for every field and an Instructions tab where the prompt editor fills the room. Save
            and Cancel sit in the shared footer, visible from either tab. */}
        <div
          className="sched-modal sched-modal-large"
          role="dialog"
          aria-modal="true"
          aria-label={form.editingId === null ? "New cron job" : "Edit cron job"}
        >
          <div className="sched-modal-head sched-modal-head-tabbed">
            <span className="sched-modal-title">
              {form.editingId === null ? "New cron job" : "Edit cron job"}
            </span>
            <div className="sched-tabs" role="tablist" aria-label="Editor sections">
              <button
                type="button"
                role="tab"
                id="sched-tab-settings"
                aria-selected={activeTab === "settings"}
                aria-controls="sched-panel-settings"
                className={`sched-tab${activeTab === "settings" ? " active" : ""}`}
                onClick={() => setActiveTab("settings")}
              >
                Settings
                {settingsHasError && (
                  <span className="sched-tab-warn" title="A required field on this tab still needs a value">
                    !
                  </span>
                )}
              </button>
              <button
                type="button"
                role="tab"
                id="sched-tab-instructions"
                aria-selected={activeTab === "instructions"}
                aria-controls="sched-panel-instructions"
                className={`sched-tab${activeTab === "instructions" ? " active" : ""}`}
                onClick={() => setActiveTab("instructions")}
              >
                Instructions
              </button>
            </div>
          </div>

          <div className="sched-modal-body sched-modal-body-tabbed">
            <div
              id="sched-panel-settings"
              role="tabpanel"
              aria-labelledby="sched-tab-settings"
              hidden={activeTab !== "settings"}
              className="sched-tabpanel sched-tabpanel-settings"
            >
              <div className="sched-settings-grid">
                <div className="sched-fld">
                  <label className="sched-fld-label">Name</label>
                  <input
                    className={formErrors.name ? "invalid" : undefined}
                    value={form.name}
                    placeholder="e.g. Nightly issue sweep"
                    onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
                  />
                  {formErrors.name && <div className="sched-fld-err">{formErrors.name}</div>}
                </div>

                {/* The factory seat this schedule runs as (the owner, 2026-10-10). The Gateway checks the pair on
                    save; it cannot take a schedule out of its factory, so "no factory" is offered only to a
                    schedule that has none. A stored value the list does not hold is kept as it is. A work-list
                    schedule cannot be a factory's, so it is offered no factory at all. */}
                {(form.actionKind === "seed" || form.factory.length > 0) && (
                  <div className="sched-fld">
                    <label className="sched-fld-label" htmlFor="sched-factory">Factory</label>
                    <select
                      id="sched-factory"
                      value={form.factory}
                      disabled={seatChoices === null}
                      onChange={(e) => setForm((f) => ({ ...f, factory: e.target.value, seat: "" }))}
                    >
                      {seatChoices === null && <option value={form.factory}>{seatChoicesError === null ? "Loading..." : form.factory || "Unavailable"}</option>}
                      {seatChoices !== null && initial.factory.length === 0 && <option value="">{seatChoices.noneLabel}</option>}
                      {seatChoices !== null && form.factory.length > 0 && chosenFactory === undefined && (
                        <option value={form.factory}>{form.factory}</option>
                      )}
                      {seatChoices?.factories.map((c) => (
                        <option key={c.factory} value={c.factory}>
                          {c.title}
                        </option>
                      ))}
                    </select>
                    {seatChoicesError !== null && <div className="sched-fld-err">{seatChoicesError}</div>}
                  </div>
                )}

                {form.factory.length > 0 && (
                  <div className="sched-fld">
                    <label className="sched-fld-label" htmlFor="sched-seat">Seat</label>
                    <select
                      id="sched-seat"
                      className={formErrors.seat ? "invalid" : undefined}
                      value={form.seat}
                      onChange={(e) => setForm((f) => ({ ...f, seat: e.target.value }))}
                    >
                      {form.seat.length === 0 && <option value="">Choose a seat...</option>}
                      {form.seat.length > 0 && !chosenFactory?.seats.some((s) => s.id === form.seat) && (
                        <option value={form.seat}>{form.seat}</option>
                      )}
                      {chosenFactory?.seats.map((s) => (
                        <option key={s.id} value={s.id}>
                          {s.label}
                        </option>
                      ))}
                    </select>
                    {formErrors.seat && <div className="sched-fld-err">{formErrors.seat}</div>}
                  </div>
                )}

                <div className="sched-fld">
                  <label className="sched-fld-label">Run on (machine)</label>
                  <div className="sched-dpick-field">
                    <span className={`sched-dpick-chosen${form.machine.length === 0 ? " none" : ""}`}>
                      {form.machine.length === 0 ? "No machine selected" : form.machine}
                    </span>
                    <button
                      type="button"
                      className="sched-btn dpick-choose"
                      onClick={() => {
                        setMachineFilter("");
                        setShowDirectorPicker(true);
                      }}
                    >
                      {form.machine.length === 0 ? "Choose..." : "Change"}
                    </button>
                  </div>
                  {formErrors.machine && <div className="sched-fld-err">{formErrors.machine}</div>}
                </div>

                <div className="sched-fld sched-fld-wide">
                  <label className="sched-fld-label">Repository path</label>
                  <input
                    className={`mono${formErrors.repoPath ? " invalid" : ""}`}
                    value={form.repoPath}
                    placeholder="C:\repos\devthrottle"
                    onChange={(e) => setForm((f) => ({ ...f, repoPath: e.target.value }))}
                  />
                  {formErrors.repoPath && <div className="sched-fld-err">{formErrors.repoPath}</div>}
                </div>

                <div className="sched-fld">
                  <label className="sched-fld-label">Schedule</label>
                  <select
                    value={form.scheduleKind}
                    onChange={(e) =>
                      setForm((f) => ({ ...f, scheduleKind: e.target.value as FormState["scheduleKind"] }))
                    }
                  >
                    <option value="oneOff">Run once</option>
                    <option value="recurring">Recurring (cron)</option>
                    {form.scheduleKind === "random" && <option value="random">At random times</option>}
                    {form.scheduleKind === "window" && <option value="window">In a window (the Gateway picks the minute)</option>}
                  </select>
                </div>

                {form.scheduleKind === "window" ? (
                  <div className="sched-fld">
                    <label className="sched-fld-label">Window settings</label>
                    <input
                      className={`mono${formErrors.schedule ? " invalid" : ""}`}
                      value={form.cron}
                      placeholder="window=00:00-06:30 deadline=07:00"
                      onChange={(e) => setForm((f) => ({ ...f, cron: e.target.value }))}
                    />
                    <div className="sched-cron-preview">The Gateway chooses the minute again when you save.</div>
                    {formErrors.schedule && <div className="sched-fld-err">{formErrors.schedule}</div>}
                  </div>
                ) : form.scheduleKind === "random" ? (
                  <div className="sched-fld">
                    <label className="sched-fld-label">Random settings</label>
                    <input
                      className={`mono${formErrors.schedule ? " invalid" : ""}`}
                      value={form.cron}
                      placeholder="window=07:00-01:00 perDay=4 minGap=45 shape=human"
                      onChange={(e) => setForm((f) => ({ ...f, cron: e.target.value }))}
                    />
                    {formErrors.schedule && <div className="sched-fld-err">{formErrors.schedule}</div>}
                  </div>
                ) : form.scheduleKind === "recurring" ? (
                  <div className="sched-fld">
                    <label className="sched-fld-label">Cron expression (5-field)</label>
                    <input
                      className={`mono${formErrors.schedule ? " invalid" : ""}`}
                      value={form.cron}
                      placeholder="0 0 * * *"
                      onChange={(e) => setForm((f) => ({ ...f, cron: e.target.value }))}
                    />
                    {form.cron.trim().length > 0 && (
                      <div className="sched-cron-preview">Runs {cronToEnglish(form.cron)}.</div>
                    )}
                    {formErrors.schedule && <div className="sched-fld-err">{formErrors.schedule}</div>}
                  </div>
                ) : (
                  <div className="sched-fld">
                    <label className="sched-fld-label">Run at (local time)</label>
                    <input
                      className={`mono${formErrors.schedule ? " invalid" : ""}`}
                      value={form.runAt}
                      placeholder="2026-06-18T00:00:00"
                      onChange={(e) => setForm((f) => ({ ...f, runAt: e.target.value }))}
                    />
                    {formErrors.schedule && <div className="sched-fld-err">{formErrors.schedule}</div>}
                  </div>
                )}

                <div className="sched-fld">
                  <label className="sched-fld-label">Time zone</label>
                  <input
                    className="mono"
                    value={form.timeZone}
                    placeholder="America/Chicago"
                    onChange={(e) => setForm((f) => ({ ...f, timeZone: e.target.value }))}
                  />
                </div>

                <div className="sched-fld">
                  <label className="sched-fld-label">Notify when run completes</label>
                  <select
                    value={form.notifyOn}
                    onChange={(e) =>
                      setForm((f) => ({ ...f, notifyOn: e.target.value as "none" | "always" | "failure" }))
                    }
                  >
                    <option value="none">Off (no notification)</option>
                    <option value="always">Always (success or failure)</option>
                    <option value="failure">Only on failure</option>
                  </select>
                </div>

                {form.notifyOn !== "none" && (
                  <div className="sched-fld sched-fld-wide">
                    <label className="sched-fld-label">Webhook URL (optional)</label>
                    <input
                      className="mono"
                      value={form.notifyWebhookUrl}
                      placeholder="example.com/hook (https)"
                      onChange={(e) => setForm((f) => ({ ...f, notifyWebhookUrl: e.target.value }))}
                    />
                  </div>
                )}
              </div>
            </div>

            <div
              id="sched-panel-instructions"
              role="tabpanel"
              aria-labelledby="sched-tab-instructions"
              hidden={activeTab !== "instructions"}
              className="sched-tabpanel sched-tabpanel-instructions"
            >
              {/* The whole point of the editor is this one field. On its own tab it fills the room:
                  a large monospace, resizable text area for a multi-paragraph instruction. The
                  "What to run" selector stays hidden (work lists are being retired); an existing
                  work-list job still shows and keeps its name here so nothing is lost. */}
              {form.actionKind === "worklist" ? (
                <div className="sched-fld sched-fld-wide">
                  <label className="sched-fld-label">Work list name</label>
                  <input
                    value={form.workListName}
                    placeholder="e.g. Tonight"
                    onChange={(e) => setForm((f) => ({ ...f, workListName: e.target.value }))}
                  />
                  <div className="sched-fld-help">
                    This job drains a named work list. New jobs use a skill or prompt instead.
                  </div>
                </div>
              ) : (
                <>
                  <label className="sched-fld-label" htmlFor="sched-instructions-box">
                    Skill / prompt
                  </label>
                  <textarea
                    id="sched-instructions-box"
                    className="sched-prompt sched-prompt-large mono"
                    value={form.seed}
                    placeholder={"/implementation-loop 312\n\nor a full multi-paragraph instruction..."}
                    onChange={(e) => setForm((f) => ({ ...f, seed: e.target.value }))}
                  />
                  <div className="sched-fld-help">
                    A skill invocation (begins with a slash) or a full multi-paragraph instruction - it
                    fills the tab and can be dragged taller.
                  </div>
                </>
              )}
            </div>
          </div>

          <div className="sched-modal-foot">
            {formError !== null && <div className="sched-modal-error sched-modal-foot-error">{formError}</div>}
            {formDirty && <span className="sched-modal-dirty">Unsaved changes</span>}
            <button className="sched-btn" onClick={requestCloseForm} disabled={saving}>
              Cancel
            </button>
            <button
              className="sched-btn primary"
              disabled={saving || !formValid}
              title={formValid ? undefined : "Fill in the highlighted fields on the Settings tab to continue"}
              onClick={() => void save()}
            >
              {saving ? "Saving..." : form.editingId === null ? "Create job" : "Save"}
            </button>
          </div>
        </div>
      </div>

      {showDirectorPicker && (
        <div className="sched-modal-backdrop dpicker-over" {...dismissDirectorPicker}>
          <div className="sched-modal dpicker-modal">
            <div className="sched-modal-head">Choose a machine</div>
            <div className="sched-modal-body">
              <input
                className="sched-dpicker-filter"
                placeholder="filter machine"
                value={machineFilter}
                onChange={(e) => setMachineFilter(e.target.value)}
              />
              {machines.length === 0 ? (
                <div className="sched-dpick-empty">No machines known to this Gateway yet.</div>
              ) : (
                <div className="sched-dpick">
                  {filteredMachines.map((machine) => {
                    const dirs = directors.filter(
                      (d) => (d.machineName ?? "").toLowerCase() === machine.toLowerCase(),
                    );
                    const reachable = dirs.filter((d) => !isUnreachable(d)).length;
                    const machineSessions = sessions.filter(
                      (s) => (s.machineName ?? "").toLowerCase() === machine.toLowerCase(),
                    );
                    const needs = needsYouCount(machineSessions);
                    const shown = [...machineSessions]
                      .sort((a, b) => Number(a.sortOrder ?? 0) - Number(b.sortOrder ?? 0))
                      .slice(0, 3);
                    return (
                      <button
                        key={machine}
                        type="button"
                        className={`sched-dcard${form.machine === machine ? " sel" : ""}`}
                        onClick={() => {
                          setForm((f) => ({ ...f, machine }));
                          setShowDirectorPicker(false);
                        }}
                      >
                        <div className="sched-dcard-top">
                          <span className={`sched-dot ${reachable > 0 ? "ok" : "dead"}`} />
                          <span className="sched-dname">{machine}</span>
                          <span className="sched-dmeta">
                            {reachable} director{reachable === 1 ? "" : "s"} running
                          </span>
                          {needs > 0 && <span className="sched-dneeds">{needs} NEEDS YOU</span>}
                        </div>
                        {reachable === 0 ? (
                          <div className="sched-dsub dempty">no Director running - one will be launched on demand</div>
                        ) : machineSessions.length === 0 ? (
                          <div className="sched-dsub dempty">idle - 0 sessions</div>
                        ) : (
                          <>
                            {shown.map((s) => {
                              const p = pickerSession(s);
                              return (
                                <div className="sched-dsess" key={s.sessionId}>
                                  <span className="sched-sdot" style={{ background: p.dot }} />
                                  <span className="sched-sname">{sessName(s)}</span>
                                  <span className="sched-sstate">{p.state}</span>
                                </div>
                              );
                            })}
                            {machineSessions.length > 3 && (
                              <div className="sched-dsub dempty">+{machineSessions.length - 3} more</div>
                            )}
                          </>
                        )}
                      </button>
                    );
                  })}
                </div>
              )}
            </div>
            <div className="sched-modal-foot">
              <button className="sched-btn" onClick={() => setShowDirectorPicker(false)}>
                Cancel
              </button>
            </div>
          </div>
        </div>
      )}

      {/* The unsaved-edit guard (issue #1289, guard style from #1255): closing a dirty editor asks
          before dropping the edits, so a stray backdrop click or Escape can never silently discard a
          part-written instruction. */}
      <ConfirmDialog
        open={confirmDiscard}
        title="Discard unsaved changes?"
        message="You have edits that have not been saved. Closing the editor will discard them. This cannot be undone."
        confirmLabel="Discard and close"
        cancelLabel="Keep editing"
        onConfirm={closeForm}
        onClose={() => setConfirmDiscard(false)}
      />
    </>
  );
}

function normalizeNotify(value: string | null | undefined): "none" | "always" | "failure" {
  const v = (value ?? "").toLowerCase();
  return v === "always" || v === "failure" ? v : "none";
}

// The form's kind for a stored job. An unknown kind reads as one-off, as it always has.
function formKind(kind: string): FormState["scheduleKind"] {
  const k = kind.trim().toLowerCase();
  return k === "recurring" ? "recurring" : k === "random" ? "random" : k === "window" ? "window" : "oneOff";
}

// The Director-picker session preview vocabulary (Blazor SessName / SessState / SessClass).
function sessName(s: SessionDto): string {
  const name = s.name ?? "";
  if (name.trim().length > 0) return name;
  const sid = s.sessionId ?? "";
  return sid.length > 8 ? sid.slice(0, 8) : sid;
}

// The Director picker's session preview, straight from the Gateway's stamped fold. Pure and exported
// so the rule is testable without a DOM.
//
// sessState and sessClass are GONE, not corrected. They were an entire parallel triage fold living in
// this picker: sessState derived "needs you" from the needsYouSince TIMESTAMP instead of the Gateway's
// stamped triageBucket, so a snoozed session - which keeps its needsYouSince stamp - read "needs you"
// here while every other screen showed it parked. sessClass folded the same question a second time, in
// a different order, off the raw activityState.
//
// sessClass was also a LAW VIOLATION: it returned "run" for a working session, and .sched-sdot.run
// painted --sched-green. A working session rendered GREEN. The law says working is BLUE, always. Worse
// than a stray hex: in the shared vocabulary green MEANS "ready - brand-new, parked at its prompt", so
// this screen used one colour to mean the opposite of what it means everywhere else.
//
// The picker reads the same /sessions envelope that stamps every other screen (getSessionsEnvelope),
// so the fold's answers were available here all along. The client renders; it does not decide.
export function pickerSession(s: SessionDto): { dot: string; state: string } {
  return { dot: dotHex(s), state: stateLabel(s) };
}

// How many of a machine's sessions actually need you: the Gateway's stamped bucket, never a count of
// needsYouSince stamps. A snoozed session keeps its stamp - it needed you once - so counting stamps
// put parked sessions in the "NEEDS YOU" chip.
export function needsYouCount(sessions: SessionDto[]): number {
  return sessions.filter((s) => classify(s) === "needsYou").length;
}
