import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link } from "react-router-dom";
import {
  createCronJob,
  deleteCronJob,
  getCronJobs,
  getCronLoad,
  getCronRuns,
  runCronJobNow,
  updateCronJob,
  type CronJob,
  type CronLoad,
  type CronRunRecord,
} from "@devthrottle/client-core/schedule/scheduleClient";
import { LoadStrip, machineShown, resolveHourPick } from "./LoadStrip";
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
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { getGatewaySettings } from "@devthrottle/client-core/settings/settingsClient";
import { classify, dotHex, stateLabel } from "@devthrottle/client-core/sessions/ordering";
import { useVisiblePolling } from "@devthrottle/client-core/polling/useVisiblePolling";
import { clockLabel, relativeTime, repoBasename } from "../fleet/format";
import {
  Button,
  ConfirmDialog,
  DataTable,
  PageHeader,
  useDismissOnBackdrop,
  type DataTableColumn,
  type DataTableGrouping,
} from "../components";
import {
  absoluteUtc,
  actionShortLabel,
  actionType,
  cronToEnglish,
  epochOrMax,
  lastOutcome,
  nextRunInstant,
  nextRunLabel,
  promptBody,
  compareScheduleGroups,
  repeatsLabel,
  scheduleGroupOf,
  scheduleGroupTitle,
  scheduleListOf,
  type ScheduleList,
} from "./scheduleFormat";

// The Schedule page (issue #976, epic #967) - the React port of the Blazor Cockpit Schedule.razor
// (issue #488). The human's window into cron jobs: a pure CLIENT of the Gateway's /cron/jobs surface
// (every create / update / delete / run / toggle goes through client-core to the Gateway, so the
// Cockpit never owns a copy). It lists jobs, shows the selected job's run history, and drives the
// create/edit modal with its own roomy Director picker dialog (the machine picker, #495). Reads and
// writes are same-origin, root-relative through the Gateway front door - never a Director address.
//
// Polling matches the Blazor page: the job list refreshes every 5s; a refresh never blocks the modal.
const POLL_MS = 5000;
const LOAD_POLL_MS = 60000;

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
  scheduleKind: "oneOff" | "recurring" | "random";
  cron: string;
  runAt: string;
  timeZone: string;
  notifyOn: "none" | "always" | "failure";
  notifyWebhookUrl: string;
  enabled: boolean;
  preventOverlap: boolean;
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
}

function validateForm(f: FormState): FormErrors {
  const errors: FormErrors = {};
  if (f.name.trim().length === 0) errors.name = "Enter a name so you can find this job in the list.";
  if (f.machine.trim().length === 0) errors.machine = "Choose the machine this job runs on.";
  if (f.repoPath.trim().length === 0) errors.repoPath = "Enter the repository path the session opens in.";
  if (f.scheduleKind === "recurring") {
    if (f.cron.trim().length === 0) errors.schedule = "Enter a 5-field cron expression, for example 0 0 * * *.";
  } else if (f.scheduleKind === "random") {
    if (f.cron.trim().length === 0) errors.schedule = "Enter the random settings, for example window=07:00-01:00 perDay=4 minGap=45.";
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
};

export function ScheduleView() {
  const [jobs, setJobs] = useState<CronJob[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [runs, setRuns] = useState<CronRunRecord[]>([]);
  const [lastRefresh, setLastRefresh] = useState<Date | null>(null);
  const [lastError, setLastError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

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
  const [showForm, setShowForm] = useState(false);
  const [saving, setSaving] = useState(false);
  const [form, setForm] = useState<FormState>(EMPTY_FORM);
  const [baseline, setBaseline] = useState<FormState>(EMPTY_FORM);
  const [activeTab, setActiveTab] = useState<"settings" | "instructions">("settings");
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  // Which list the grid shows (the owner, 2026-10-09): only the schedules that will still run, by default. A
  // spent one-off and a switched-off schedule are still reachable, behind the Paused and Historical tabs.
  const [list, setList] = useState<ScheduleList>("active");
  // Layout A (the owner, 2026-10-09): grouped by factory by default; "Group: None" gives the flat list.
  const [grouped, setGrouped] = useState(true);
  // The load strip (the owner, 2026-10-09): the Gateway's 24-hour forecast, the machine it shows, and the hour the
  // list is filtered to when a bar is tapped.
  const [load, setLoad] = useState<CronLoad | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loadMachine, setLoadMachine] = useState("");
  // Only WHICH hour was tapped is held; its schedules are read from the latest forecast, so the list never keeps
  // ids from a forecast the Gateway has since replaced.
  const [hourPick, setHourPick] = useState<{ machine: string; startUtc: string } | null>(null);

  // The cron job awaiting delete confirmation. Deleting a job removes the schedule permanently, so it
  // asks through the shared ConfirmDialog (issue #1244) instead of firing on the first click.
  const [pendingDelete, setPendingDelete] = useState<CronJob | null>(null);

  // selectedRef mirrors selectedId so the poll can refetch the open job's runs without re-subscribing.
  const selectedRef = useRef<string | null>(null);
  selectedRef.current = selectedId;

  const refresh = useCallback(async (signal?: AbortSignal) => {
    try {
      const fresh = await getCronJobs(signal);
      setJobs(fresh);
      setLastError(null);
      setLastRefresh(new Date());
      const sel = selectedRef.current;
      if (sel !== null && fresh.some((j) => j.id === sel)) {
        try {
          setRuns(await getCronRuns(sel, signal));
        } catch {
          /* run-history refresh is non-fatal; keep the last list */
        }
      }
    } catch (err) {
      if (signal?.aborted === true) return;
      setLastError(gatewayErrorMessage(err));
    }
  }, []);

  // The schedule list refresh is visibility-aware (issue #1239): a hidden tab stops polling and resumes,
  // refetching at once, when it returns to the foreground.
  useVisiblePolling(refresh, POLL_MS);

  // The forecast moves by the hour, not by the second, so it is read far less often than the list.
  const refreshLoad = useCallback(async (signal?: AbortSignal) => {
    try {
      setLoad(await getCronLoad(signal));
      setLoadError(null);
    } catch (err) {
      if (signal?.aborted === true) return;
      setLoadError(gatewayErrorMessage(err));
    }
  }, []);
  useVisiblePolling(refreshLoad, LOAD_POLL_MS);

  const selectJob = useCallback(async (id: string) => {
    setSelectedId(id);
    setActionError(null);
    try {
      setRuns(await getCronRuns(id));
    } catch {
      setRuns([]);
    }
  }, []);

  const loadDirectors = useCallback(async () => {
    try {
      const [dirs, env] = await Promise.all([getFleetDirectors(), getSessionsEnvelope()]);
      setDirectors(dirs);
      setSessions(env.sessions);
      setMachineErrors(env.machineErrors);
      setDirectorReach(env.directors);
    } catch {
      /* the picker degrades to "no machines known" rather than blocking the form */
    }
  }, []);

  // Open the dialog on a form, recording the same value as the dirty-tracking baseline and starting on
  // the Settings tab. Both the create and edit flows funnel through here so they share the two-tab
  // dialog and the unsaved-edit guard identically (issue #1289).
  const openForm = useCallback(
    (initial: FormState) => {
      setForm(initial);
      setBaseline(initial);
      setActiveTab("settings");
      setConfirmDiscard(false);
      setFormError(null);
      setShowForm(true);
      void loadDirectors();
    },
    [loadDirectors],
  );

  // The account's time zone (the Settings page value), read once so a NEW job's time zone defaults to
  // it instead of starting empty - the account setting is the one place time zone lives (issue #2115).
  // Editing an existing job keeps that job's own stored zone.
  const [accountTimeZone, setAccountTimeZone] = useState("");
  useEffect(() => {
    let cancelled = false;
    void getGatewaySettings()
      .then((s) => {
        if (!cancelled) setAccountTimeZone(s.timeZone);
      })
      .catch(() => {
        /* the form simply starts with an empty zone when settings cannot be read */
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const openCreate = useCallback(() => {
    openForm({ ...EMPTY_FORM, timeZone: accountTimeZone });
  }, [openForm, accountTimeZone]);

  const openEdit = useCallback(
    (job: CronJob) => {
      openForm({
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
      });
    },
    [openForm],
  );

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
    setShowForm(false);
    setConfirmDiscard(false);
  }, []);

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
    if (!showForm) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape" && !saving && !showDirectorPicker && !confirmDiscard) {
        requestCloseForm();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [showForm, saving, showDirectorPicker, confirmDiscard, requestCloseForm]);

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
      if (form.editingId === null) {
        await createCronJob(dto);
      } else {
        await updateCronJob(form.editingId, dto);
      }
      closeForm();
      await refresh();
    } catch (err) {
      // Surface the Gateway's message (incl. a 400 for an invalid cron) inline in the form.
      setFormError(gatewayErrorMessage(err));
    } finally {
      setSaving(false);
    }
  }, [buildFromForm, form, formValid, refresh, closeForm]);

  const runNow = useCallback(
    async (job: CronJob) => {
      setActionError(null);
      try {
        await runCronJobNow(job.id);
        setSelectedId(job.id);
        await refresh();
        setRuns(await getCronRuns(job.id));
      } catch (err) {
        setActionError(`Run now failed: ${gatewayErrorMessage(err)}`);
      }
    },
    [refresh],
  );

  // The actual delete, run once the ConfirmDialog is confirmed. A failure is left to throw so the
  // dialog surfaces it (fail loudly) rather than being swallowed into the page banner.
  const remove = useCallback(
    async (job: CronJob) => {
      setActionError(null);
      await deleteCronJob(job.id);
      if (selectedRef.current === job.id) {
        setSelectedId(null);
        setRuns([]);
      }
      await refresh();
    },
    [refresh],
  );

  const toggleEnabled = useCallback(
    async (job: CronJob) => {
      setActionError(null);
      try {
        // Send only the mutable job fields (issue #1027) - never echo back the Gateway's own
        // computed fields (nextRunUtc / lastFiredUtc / lastStatus / createdUtc). This preserves
        // every field the toggle does not change, notify settings included, so flipping enabled
        // never silently clears them (Blazor #622), while keeping read-only scheduling state on the
        // Gateway.
        await updateCronJob(job.id, toMutableDto(job, { enabled: !job.enabled }));
        await refresh();
      } catch (err) {
        setActionError(`Toggle failed: ${gatewayErrorMessage(err)}`);
      }
    },
    [refresh],
  );

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

  // The searchable text of a job: name, machine, repository, and the prompt text - so the search box
  // finds a job by any of them (issue #1245), including a word buried in the instructions - and the factory and
  // seat the row now shows, so whatever is on screen can be searched for.
  const searchableText = useCallback(
    (job: CronJob): string =>
      [
        job.name,
        job.target.machine,
        job.action.repoPath,
        job.action.seed,
        job.action.workListName ?? "",
        job.factory ?? "",
        job.factoryTitle ?? "",
        job.seat ?? "",
      ].join(" "),
    [],
  );

  // The grid columns: one short scannable value each. The prompt body is never here - it lives in the
  // drawer. Default sort is next run, soonest first (epochOrMax sinks "no next run" to the bottom).
  const columns = useMemo<DataTableColumn<CronJob>[]>(
    () => [
      {
        key: "state",
        header: "",
        width: "30px",
        render: (job) => (
          <span
            className={`sched-dot2 ${job.enabled ? "on" : "off"}`}
            title={job.enabled ? "Enabled" : "Disabled"}
          />
        ),
      },
      {
        key: "name",
        header: "Name",
        width: "200px",
        sortable: true,
        sortValue: (job) => job.name.toLowerCase(),
        render: (job) => (
          <span className="sched-cell-name">
            <span className="sched-cell-name-main">{grouped ? job.shortName ?? job.name : job.name}</span>
            <span className="sched-cell-name-sub">
              {scheduleGroupOf(job) === "" ? (
                <span className="sched-plainbadge">scheduled job</span>
              ) : grouped ? (
                `seat: ${job.seat ?? "-"}`
              ) : (
                `${job.factoryTitle ?? job.factory} - seat: ${job.seat ?? "-"}`
              )}
            </span>
          </span>
        ),
      },
      {
        key: "runs",
        header: "Runs",
        width: "220px",
        sortable: true,
        sortValue: (job) => actionShortLabel(job).toLowerCase(),
        render: (job) => (
          <span className="sched-runs">
            <span className={`sched-typechip ${actionType(job).toLowerCase().replace(/\s+/g, "-")}`}>
              {actionType(job)}
            </span>
            <span className="sched-runs-label" title={actionShortLabel(job)}>
              {actionShortLabel(job)}
            </span>
          </span>
        ),
      },
      {
        key: "target",
        header: "Target",
        width: "150px",
        sortable: true,
        sortValue: (job) => job.target.machine.toLowerCase(),
        render: (job) => (
          <span className="sched-target">
            <span className="mono">{job.target.machine}</span>
            <span className="sched-target-repo">{repoBasename(job.action.repoPath)}</span>
          </span>
        ),
      },
      {
        key: "schedule",
        header: "Schedule",
        width: "195px",
        sortable: true,
        sortValue: (job) => cronToEnglish(scheduleCron(job)).toLowerCase(),
        render: (job) => (
          <span className="sched-schedule" title={scheduleCron(job) ?? undefined}>
            <span className={`sched-repeats ${repeatsLabel(job) === "ONCE" ? "once" : "repeats"}`}>
              {repeatsLabel(job)}
            </span>
            {scheduleEnglish(job)}
            {notifyEnabled(job) && (
              <span className="sched-notify-badge" title={notifyTitle(job)}>
                {notifyLabel(job)}
              </span>
            )}
          </span>
        ),
      },
      {
        key: "next",
        header: "Next run",
        width: "90px",
        sortable: true,
        sortValue: (job) => epochOrMax(nextRunInstant(job)),
        render: (job) => (
          <span className="mono" title={absoluteUtc(nextRunInstant(job))}>
            {nextRunLabel(job)}
          </span>
        ),
      },
      {
        key: "last",
        header: "Last run",
        width: "135px",
        sortable: true,
        sortValue: (job) => epochOrMax(job.lastFiredUtc),
        render: (job) => {
          const outcome = lastOutcome(job.lastStatus);
          return (
            <span className="sched-lastrun" title={absoluteUtc(job.lastFiredUtc)}>
              <span className="dim">
                {job.lastFiredUtc ? relativeTime(job.lastFiredUtc, { withAgo: true }) : "never"}
              </span>
              {outcome.kind !== "none" && (
                <span className={`sched-outcome ${outcome.kind}`}>{outcome.label}</span>
              )}
            </span>
          );
        },
      },
      {
        // Whether this schedule's sessions close themselves (the owner, 2026-10-09). The words and the verdict are
        // the Gateway's; the page only colours the verdict it is given.
        key: "closes",
        header: "Closes itself",
        width: "230px",
        sortable: true,
        sortValue: (job) => runRecordRank(job.runRecord?.verdict),
        render: (job) => (
          <span className={`sched-runrecord ${runRecordClass(job.runRecord?.verdict)}`}>
            {job.runRecord?.text ?? "-"}
          </span>
        ),
      },
      {
        key: "enabled",
        header: "Status",
        width: "90px",
        className: "ui-table-cell-stop",
        sortable: true,
        sortValue: (job) => (job.enabled ? 0 : 1),
        render: (job) => (
          <button
            className={`sched-chip ${job.enabled ? "enabled" : "disabled"}`}
            title="Toggle enabled"
            onClick={() => void toggleEnabled(job)}
          >
            {job.enabled ? "Enabled" : "Disabled"}
          </button>
        ),
      },
      {
        key: "actions",
        header: "",
        // Wide enough for Resume, Run now, Edit and Delete on a paused row without clipping Delete.
        width: "225px",
        align: "right",
        className: "ui-table-cell-stop",
        render: (job) => (
          <span className="sched-actions">
            {job.lifecycle === "paused" && (
              <button className="sched-linkbtn" onClick={() => void toggleEnabled(job)}>
                Resume
              </button>
            )}
            <button className="sched-linkbtn" onClick={() => void runNow(job)}>
              Run now
            </button>
            <button className="sched-linkbtn" onClick={() => openEdit(job)}>
              Edit
            </button>
            <button className="sched-linkbtn del" onClick={() => setPendingDelete(job)}>
              Delete
            </button>
          </span>
        ),
      },
    ],
    [toggleEnabled, runNow, openEdit, grouped],
  );

  // One header per factory: its title, how many schedules, the soonest next run, and a way to the factory itself.
  const grouping = useMemo<DataTableGrouping<CronJob> | undefined>(() => {
    if (!grouped) return undefined;
    const titleOf = (groupKey: string) =>
      scheduleGroupTitle(groupKey, jobs.filter((job) => scheduleGroupOf(job) === groupKey));
    return {
      groupOf: scheduleGroupOf,
      compareGroups: (a, b) => compareScheduleGroups(a, b, titleOf),
      renderHeader: (groupKey, rows) => {
        const soonest = [...rows].sort((a, b) => epochOrMax(nextRunInstant(a)) - epochOrMax(nextRunInstant(b)))[0];
        return (
          <span className={`sched-group${groupKey === "" ? " plain" : ""}`}>
            <span className="sched-group-title">{scheduleGroupTitle(groupKey, rows)}</span>
            <span className="sched-group-meta">
              {rows.length} schedule{rows.length === 1 ? "" : "s"}
              {list === "active" && soonest !== undefined ? ` - next ${nextRunLabel(soonest)}` : ""}
            </span>
            {groupKey !== "" && (
              <Link className="sched-group-link" to={`/factories/${encodeURIComponent(groupKey)}`}>
                Open factory
              </Link>
            )}
          </span>
        );
      },
    };
  }, [grouped, jobs, list]);

  // The drawer body for a job: what it does, in full. The prompt lives here (read-only, scrollable,
  // monospace) alongside the plain-English schedule, the resolved next run, and recent run history.
  const renderDetail = useCallback(
    (job: CronJob) => {
      const showRuns = job.id === selectedId;
      return (
        <div className="sched-detail">
          <div className="sched-detail-summary">
            <span className={`sched-dot2 ${job.enabled ? "on" : "off"}`} />
            {job.enabled ? "Enabled" : "Disabled"} - <span className="mono">{job.target.machine}</span> -{" "}
            <span className="mono">{job.action.repoPath}</span>
          </div>

          <dl className="sched-detail-facts">
            <dt>Schedule</dt>
            <dd>
              {scheduleEnglish(job)}
              {scheduleCron(job) !== null && <span className="sched-detail-cron">({scheduleCron(job)})</span>}
              {isRandom(job) && <span className="sched-detail-cron">({job.cronExpression})</span>}
            </dd>
            {isRandom(job) && (
              <>
                <dt>Still today</dt>
                <dd className="mono">
                  {job.remainingToday && job.remainingToday.length > 0
                    ? job.remainingToday.join(", ")
                    : "No more runs today"}
                </dd>
              </>
            )}
            <dt>Next run</dt>
            <dd>
              {nextRunLabel(job)}{" "}
              <span className="dim">({absoluteUtc(nextRunInstant(job))})</span>
            </dd>
            <dt>Last run</dt>
            <dd>
              {job.lastFiredUtc ? relativeTime(job.lastFiredUtc, { withAgo: true }) : "never"}{" "}
              <span className="dim">({absoluteUtc(job.lastFiredUtc)})</span>
            </dd>
            <dt>Closes itself</dt>
            <dd className={`sched-runrecord ${runRecordClass(job.runRecord?.verdict)}`}>{job.runRecord?.text ?? "-"}</dd>
            <dt>Notify</dt>
            <dd>{notifyDescription(job)}</dd>
          </dl>

          <div className="sched-detail-label">
            {actionType(job)} - instructions
          </div>
          <pre className="sched-detail-prompt">{promptBody(job)}</pre>

          <div className="sched-detail-label">Recent runs</div>
          {!showRuns ? (
            <div className="sched-detail-note">Loading run history...</div>
          ) : runs.length === 0 ? (
            <div className="sched-detail-note">No runs recorded yet for this job.</div>
          ) : (
            <table className="sched-runtbl">
              <thead>
                <tr>
                  <th>Scheduled</th>
                  <th>Fired</th>
                  <th>Infra</th>
                  <th>How it ended</th>
                </tr>
              </thead>
              <tbody>
                {runs.map((r, i) => (
                  <tr key={`${r.scheduledUtc}/${r.firedUtc}/${i}`}>
                    <td className="mono">{fmtUtc(r.scheduledUtc)}</td>
                    <td className="mono">{fmtUtc(r.firedUtc)}</td>
                    <td>
                      <span className={`sched-st ${infraClass(r.infraStatus)}`}>{r.infraStatus}</span>
                    </td>
                    <td className="dim">{r.endingText ?? r.taskStatus}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      );
    },
    [runs, selectedId],
  );

  // The jobs split into the three lists, in one pass, so the tab counts and the grid always agree.
  const byList = useMemo(() => {
    const lists: Record<ScheduleList, CronJob[]> = { active: [], paused: [], historical: [] };
    for (const job of jobs) lists[scheduleListOf(job)].push(job);
    return lists;
  }, [jobs]);

  const activeCount = byList.active.length;

  // The machine the strip shows: the one picked, held across polls even when another becomes the busiest, and the
  // busiest only until one is picked or when the picked one has no active schedules left.
  const shownMachine = useMemo(() => (load === null ? undefined : machineShown(load, loadMachine)), [load, loadMachine]);
  useEffect(() => {
    if (shownMachine !== undefined && shownMachine.machine !== loadMachine) setLoadMachine(shownMachine.machine);
  }, [shownMachine, loadMachine]);

  // The tapped hour, read from the latest forecast. Once that hour has passed out of the forecast, or the strip
  // shows another machine, there is nothing to filter by and the filter ends.
  const hourFilter = useMemo(() => resolveHourPick(shownMachine, hourPick), [hourPick, shownMachine]);
  useEffect(() => {
    if (hourPick !== null && hourFilter === null) setHourPick(null);
  }, [hourPick, hourFilter]);

  // A tapped bar narrows the list to the schedules the Gateway says are open in that hour.
  const shownRows = useMemo(() => {
    if (hourFilter === null) return byList[list];
    const ids = new Set(hourFilter.hour.jobIds);
    return byList[list].filter((job) => ids.has(job.id));
  }, [byList, list, hourFilter]);

  return (
    <div className="sched">
      <PageHeader
        title="Schedule"
        subtitle={`${activeCount} active cron job${activeCount === 1 ? "" : "s"} across the fleet.`}
        actions={
          <Button variant="primary" onClick={openCreate}>
            New cron job
          </Button>
        }
      />

      {lastError !== null && <div className="sched-banner-error">Gateway error: {lastError}</div>}
      {actionError !== null && <div className="sched-banner-error">{actionError}</div>}

      {loadError !== null && <div className="sched-banner-error">Load forecast: {loadError}</div>}
      {load !== null && load.machines.length > 0 && (
        <LoadStrip
          load={load}
          machine={loadMachine}
          onMachine={(m) => {
            setLoadMachine(m);
            setHourPick(null);
          }}
          selectedHour={hourFilter?.hour.startUtc ?? null}
          onSelectHour={(hour) => {
            if (hour === null || shownMachine === undefined) {
              setHourPick(null);
              return;
            }
            setList("active");
            setHourPick({ machine: shownMachine.machine, startUtc: hour.startUtc });
          }}
        />
      )}

      {lastRefresh !== null && (
        <div className="sched-tabs sched-listtabs" role="tablist" aria-label="Which schedules">
          {LIST_TABS.map((t) => (
            <button
              key={t.key}
              type="button"
              role="tab"
              aria-selected={list === t.key}
              className={`sched-tab${list === t.key ? " active" : ""}`}
              title={t.title}
              onClick={() => {
                setList(t.key);
                setHourPick(null);
              }}
            >
              {t.label} <span className="sched-listtab-count">{byList[t.key].length}</span>
            </button>
          ))}
        </div>
      )}

      {hourFilter !== null && (
        <div className="sched-hourfilter">
          Showing the {shownRows.length} schedule{shownRows.length === 1 ? "" : "s"} open on {hourFilter.machine} in the{" "}
          {hourFilter.hour.label} hour.{" "}
          <button type="button" className="sched-groupbtn" onClick={() => setHourPick(null)}>
            Show all
          </button>
        </div>
      )}

      {lastRefresh !== null && (
        <DataTable<CronJob>
          // Remounted per list so each opens on its own sort: soonest next run for the live lists, most recent
          // run first for history.
          key={list}
          columns={columns}
          rows={shownRows}
          rowKey={(job) => job.id}
          searchableText={searchableText}
          searchPlaceholder="Search name, machine, repository, or prompt"
          defaultSort={
            list === "historical" ? { columnKey: "last", direction: "desc" } : { columnKey: "next", direction: "asc" }
          }
          emptyMessage={LIST_EMPTY[list]}
          grouping={grouping}
          toolbarExtra={
            <>
              <span className="sched-groupswitch" role="group" aria-label="Group the list">
                <button
                  type="button"
                  className={`sched-groupbtn${grouped ? " on" : ""}`}
                  aria-pressed={grouped}
                  onClick={() => setGrouped(true)}
                >
                  Group: Factory
                </button>
                <button
                  type="button"
                  className={`sched-groupbtn${grouped ? "" : " on"}`}
                  aria-pressed={!grouped}
                  onClick={() => setGrouped(false)}
                >
                  Group: None
                </button>
              </span>
              <span className="sched-refreshed">
                {lastRefresh === null ? "connecting..." : `updated ${clockLabel(lastRefresh)}`}
              </span>
            </>
          }
          onRowActivate={(job) => void selectJob(job.id)}
          renderDetail={renderDetail}
          detailTitle={(job) => job.name}
          detailActions={(job) => (
            <>
              <Button variant="secondary" onClick={() => void runNow(job)}>
                Run now
              </Button>
              <Button variant="secondary" onClick={() => openEdit(job)}>
                Edit
              </Button>
            </>
          )}
        />
      )}

      {showForm && (
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
                    </select>
                  </div>

                  {form.scheduleKind === "random" ? (
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
      )}

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

      <ConfirmDialog
        open={pendingDelete !== null}
        title="Delete this cron job?"
        message={
          pendingDelete === null
            ? ""
            : `Delete "${pendingDelete.name}"? This removes the schedule permanently and stops it from ` +
              "running again. This cannot be undone."
        }
        confirmLabel="Delete"
        busyLabel="Deleting..."
        onConfirm={async () => {
          if (pendingDelete !== null) await remove(pendingDelete);
        }}
        onClose={() => setPendingDelete(null)}
      />

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
    </div>
  );
}

// The three list tabs, in order. Active is the default and the one the header counts.
const LIST_TABS: { key: ScheduleList; label: string; title: string }[] = [
  { key: "active", label: "Active", title: "Schedules that will still run" },
  { key: "paused", label: "Paused", title: "Repeating schedules that are switched off; switch one on to resume it" },
  { key: "historical", label: "Historical", title: "One-off schedules that have run, or were switched off before they ran" },
];

const LIST_EMPTY: Record<ScheduleList, string> = {
  active: "No active cron jobs. Create one to schedule a session or a work-list drain on a machine.",
  paused: "No paused cron jobs.",
  historical: "No historical cron jobs.",
};

// Reduce a full CronJob (as read back from the Gateway, carrying its computed nextRunUtc /
// lastFiredUtc / lastStatus / createdUtc) to a clean create/update DTO of ONLY the mutable fields,
// applying overrides last. The enable/disable toggle routes through this so a plain toggle never
// echoes the Gateway's read-only scheduling state back to it (issue #1027) - the same clean shape
// buildFromForm produces for the edit path.
function toMutableDto(job: CronJob, overrides: Partial<CronJob>): CronJob {
  return {
    id: "",
    name: job.name,
    enabled: job.enabled,
    scheduleKind: job.scheduleKind,
    cronExpression: job.cronExpression ?? null,
    runAt: job.runAt ?? null,
    timeZoneId: job.timeZoneId,
    target: { machine: job.target.machine },
    action: {
      repoPath: job.action.repoPath,
      seed: job.action.seed,
      workListName: job.action.workListName ?? null,
    },
    preventOverlap: job.preventOverlap,
    notifyOn: job.notifyOn,
    notifyWebhookUrl: job.notifyWebhookUrl ?? null,
    ...overrides,
  };
}

// ---- display helpers (faithful ports of the Blazor private helpers) ----

function normalizeNotify(value: string | null | undefined): "none" | "always" | "failure" {
  const v = (value ?? "").toLowerCase();
  return v === "always" || v === "failure" ? v : "none";
}

function notifyEnabled(j: CronJob): boolean {
  return j.notifyOn.length > 0 && j.notifyOn.toLowerCase() !== "none";
}

function notifyLabel(j: CronJob): string {
  return j.notifyOn.toLowerCase() === "failure" ? "notify: failures" : "notify: always";
}

function notifyTitle(j: CronJob): string {
  return !j.notifyWebhookUrl || j.notifyWebhookUrl.length === 0
    ? "Run-complete notification rides the fleet channel"
    : `Run-complete notification + webhook: ${j.notifyWebhookUrl}`;
}

// The raw cron string when the job is recurring, otherwise null. Used for the "on hover" title and the
// drawer's parenthetical, and as the input to the plain-English schedule.
function scheduleCron(j: CronJob): string | null {
  if (j.scheduleKind.toLowerCase() !== "recurring") return null;
  const cron = (j.cronExpression ?? "").trim();
  return cron.length > 0 ? cron : null;
}

// The schedule in plain English: a recurring job reads its cron ("At 8:14 AM and 2:14 PM, Monday
// through Friday"); a one-off reads its run-at instant ("Once at ...").
// The form's kind for a stored job. An unknown kind reads as one-off, as it always has.
function formKind(kind: string): FormState["scheduleKind"] {
  const k = kind.trim().toLowerCase();
  return k === "recurring" ? "recurring" : k === "random" ? "random" : "oneOff";
}

function isRandom(j: CronJob): boolean {
  return j.scheduleKind.trim().toLowerCase() === "random";
}

function scheduleEnglish(j: CronJob): string {
  if (j.scheduleKind.toLowerCase() === "recurring") return cronToEnglish(j.cronExpression);
  // A random schedule's words are folded by the Gateway (issue #3622); its settings text is shown as stored
  // only when the Gateway sent no words, which it does for settings that no longer validate.
  if (isRandom(j)) return j.scheduleText ?? `Random: ${j.cronExpression ?? ""}`;
  const runAt = (j.runAt ?? "").trim();
  return runAt.length > 0 ? `Once at ${runAt}` : "Once (no time set)";
}

// The run-complete notification policy, spelled out for the drawer.
function notifyDescription(j: CronJob): string {
  const policy = j.notifyOn.toLowerCase();
  const base =
    policy === "always"
      ? "Always (success or failure)"
      : policy === "failure"
        ? "Only on failure"
        : "Off";
  const hook = j.notifyWebhookUrl && j.notifyWebhookUrl.length > 0 ? ` - webhook: ${j.notifyWebhookUrl}` : "";
  return base + hook;
}

// "yyyy-MM-dd HH:mm 'UTC'" from an ISO UTC timestamp, matching the Blazor FmtUtc. "-" when absent.
function fmtUtc(iso: string | null | undefined): string {
  if (!iso || iso.length === 0) return "-";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "-";
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} ${pad(d.getUTCHours())}:${pad(
    d.getUTCMinutes(),
  )} UTC`;
}

// The colour of a run record, from the verdict the Gateway stamped. Layout only: the verdict itself is never
// worked out here.
function runRecordClass(verdict: string | null | undefined): string {
  if (verdict === "ok") return "ok";
  if (verdict === "bad") return "bad";
  return "none";
}

// Sort order for the Closes itself column: schedules whose sessions are left behind first, so they are found.
function runRecordRank(verdict: string | null | undefined): number {
  if (verdict === "bad") return 0;
  if (verdict === "ok") return 2;
  return 1;
}

function infraClass(infra: string): string {
  if (infra === "started" || infra === "worklist-started") return "ok";
  if (infra === "catch-up") return "warn";
  if (infra === "not-started") return "err";
  if (infra.startsWith("worklist-") && infra !== "worklist-started") return "warn";
  return "dim";
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
