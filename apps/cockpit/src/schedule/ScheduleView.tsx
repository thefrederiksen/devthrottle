import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import {
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
import { ScheduleEditor, type ScheduleEditorRequest } from "./ScheduleEditor";
import { getGatewaySettings } from "@devthrottle/client-core/settings/settingsClient";
import { useVisiblePolling } from "@devthrottle/client-core/polling/useVisiblePolling";
import { clockLabel, relativeTime, repoBasename } from "../fleet/format";
import {
  Button,
  ConfirmDialog,
  DataTable,
  PageHeader,
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
  isFactorySchedule,
  scheduleGroupOf,
  scheduleGroupTitle,
  scheduleListOf,
  type ScheduleList,
} from "./scheduleFormat";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-schedule";

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


export function ScheduleView() {
  const [jobs, setJobs] = useState<CronJob[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [runs, setRuns] = useState<CronRunRecord[]>([]);
  const [lastRefresh, setLastRefresh] = useState<Date | null>(null);
  const [lastError, setLastError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);


  // The schedule editor, while it is open (issue #1289). It is its own component so a factory's Seats tab opens the
  // same one (the owner, 2026-10-09).
  const [editor, setEditor] = useState<ScheduleEditorRequest | null>(null);

  // Which list the grid shows (the owner, 2026-10-09): only the schedules that will still run, by default. A
  // spent one-off and a switched-off schedule are still reachable, behind the Paused and Historical tabs.
  const [list, setList] = useState<ScheduleList>("active");
  // Layout A (the owner, 2026-10-09): grouped by factory by default; "Group: None" gives the flat list.
  const [grouped, setGrouped] = useState(true);
  // The owner, 2026-10-09: the page is for his own jobs; factory schedules are changed in their factory, so they are
  // hidden until this switch is on. Its position is remembered on this device only, as a convenience.
  const [showFactory, setShowFactoryState] = useState(readShowFactory);
  const setShowFactory = useCallback((on: boolean) => {
    setShowFactoryState(on);
    writeShowFactory(on);
  }, []);
  const navigate = useNavigate();
  // The load strip (the owner, 2026-10-09): the Gateway's 24-hour forecast, the machine it shows, and the hour the
  // list is filtered to when a bar is tapped.
  const [load, setLoad] = useState<CronLoad | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loadMachine, setLoadMachine] = useState("");
  // Only WHICH hour was tapped is held; its schedules are read from the latest forecast, so the list never keeps
  // ids from a forecast the Gateway has since replaced.
  const [hourPick, setHourPick] = useState<{ machine: string; startUtc: string } | null>(null);
  // The Gateway's warning on the schedule just saved or switched on, when it lands in an hour over capacity. It is
  // shown until dismissed or until the next save, because the schedule is saved either way.
  const [loadWarning, setLoadWarning] = useState<string | null>(null);

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
      setLastError(describeAndReport(SURFACE, "read the schedules", err, { background: true }));
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
      setLoadError(describeAndReport(SURFACE, "read the schedule forecast", err, { background: true }));
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
    setEditor({ kind: "create", timeZone: accountTimeZone });
  }, [accountTimeZone]);

  const openEdit = useCallback((job: CronJob) => setEditor({ kind: "edit", job }), []);

  // A save in the editor: show the Gateway's load warning, then read the list and the forecast again.
  const onEditorSaved = useCallback(
    async (saved: CronJob) => {
      setLoadWarning(saved.loadWarning ?? null);
      await refresh();
      await refreshLoad();
    },
    [refresh, refreshLoad],
  );

  const runNow = useCallback(
    async (job: CronJob) => {
      setActionError(null);
      try {
        await runCronJobNow(job.id);
        setSelectedId(job.id);
        await refresh();
        setRuns(await getCronRuns(job.id));
      } catch (err) {
        setActionError(`Run now failed: ${describeAndReport(SURFACE, "run the schedule now", err)}`);
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
        const saved = await updateCronJob(job.id, toMutableDto(job, { enabled: !job.enabled }));
        setLoadWarning(saved.loadWarning ?? null);
        await refresh();
        await refreshLoad();
      } catch (err) {
        setActionError(`Toggle failed: ${describeAndReport(SURFACE, job.enabled ? "pause the schedule" : "resume the schedule", err)}`);
      }
    },
    [refresh, refreshLoad],
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
          <span className={`sched-cell-name${isFactorySchedule(job) ? " factory" : ""}`}>
            <span className="sched-cell-name-main">{grouped ? job.shortName ?? job.name : job.name}</span>
            <span className="sched-cell-name-sub">
              {scheduleGroupOf(job) === "" ? (
                <span className="sched-plainbadge">personal</span>
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
            {isFactorySchedule(job) ? (
              // One place to change a factory's schedule: its factory (the owner, 2026-10-09).
              <Link className="sched-linkbtn" to={factoryHref(job)}>
                Edit in factory
              </Link>
            ) : (
              <>
                <button className="sched-linkbtn" onClick={() => openEdit(job)}>
                  Edit
                </button>
                <button className="sched-linkbtn del" onClick={() => setPendingDelete(job)}>
                  Delete
                </button>
              </>
            )}
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
              {hasGatewayWords(job) && <span className="sched-detail-cron">({job.cronExpression})</span>}
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

  // The jobs split into the three lists, in one pass, so the tab counts and the grid always agree. `everything` holds
  // factory schedules too; `byList` is what the switch lets the page show.
  const everything = useMemo(() => {
    const lists: Record<ScheduleList, CronJob[]> = { active: [], paused: [], historical: [] };
    for (const job of jobs) lists[scheduleListOf(job)].push(job);
    return lists;
  }, [jobs]);
  const byList = useMemo(() => {
    if (showFactory) return everything;
    const own = (rows: CronJob[]) => rows.filter((job) => !isFactorySchedule(job));
    return { active: own(everything.active), paused: own(everything.paused), historical: own(everything.historical) };
  }, [everything, showFactory]);

  const ownActive = everything.active.filter((job) => !isFactorySchedule(job)).length;
  const factoryActive = everything.active.length - ownActive;
  const factoryInList = everything[list].length - everything[list].filter((job) => !isFactorySchedule(job)).length;

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
    // A tapped bar counts every schedule, factory ones included, so the hour shows all of them whatever the switch says.
    const ids = new Set(hourFilter.hour.jobIds);
    return everything[list].filter((job) => ids.has(job.id));
  }, [byList, everything, list, hourFilter]);

  return (
    <div className="sched">
      <PageHeader
        title="Schedule"
        subtitle={`${ownActive} active job${ownActive === 1 ? "" : "s"} of your own, plus ${factoryActive} in factories.`}
        actions={
          <Button variant="primary" onClick={openCreate}>
            New cron job
          </Button>
        }
      />

      {lastError !== null && <div className="sched-banner-error">Gateway error: {lastError}</div>}
      {actionError !== null && <div className="sched-banner-error">{actionError}</div>}

      {loadError !== null && <div className="sched-banner-error">Load forecast: {loadError}</div>}
      {loadWarning !== null && (
        <div className="sched-banner-warn" role="status">
          <span>Saved, but: {loadWarning}</span>
          <button type="button" className="sched-groupbtn" onClick={() => setLoadWarning(null)}>
            Dismiss
          </button>
        </div>
      )}
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
              <label
                className="sched-factoryswitch"
                title={hourFilter !== null ? "A picked hour shows every schedule in it, factory ones included." : undefined}
              >
                <input
                  type="checkbox"
                  checked={showFactory}
                  disabled={hourFilter !== null}
                  onChange={(e) => setShowFactory(e.target.checked)}
                />
                Show factory schedules ({factoryInList})
              </label>
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
              {isFactorySchedule(job) ? (
                <Button variant="secondary" onClick={() => navigate(factoryHref(job))}>
                  Edit in factory
                </Button>
              ) : (
                <Button variant="secondary" onClick={() => openEdit(job)}>
                  Edit
                </Button>
              )}
            </>
          )}
        />
      )}

      {editor !== null && (
        <ScheduleEditor request={editor} onClose={() => setEditor(null)} onSaved={onEditorSaved} />
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

// The factory page a factory schedule is changed on, as the Gateway stamped it.
function factoryHref(job: CronJob): string {
  return job.factoryHref ?? "";
}

// The switch's remembered position. Browser storage can be missing or refuse, so a failure means "off", the default.
const SHOW_FACTORY_KEY = "schedule.showFactorySchedules";

function readShowFactory(): boolean {
  try {
    return window.localStorage.getItem(SHOW_FACTORY_KEY) === "1";
  } catch {
    return false;
  }
}

function writeShowFactory(on: boolean): void {
  try {
    window.localStorage.setItem(SHOW_FACTORY_KEY, on ? "1" : "0");
  } catch {
    /* not remembered on this device; the switch still works for this visit */
  }
}

function isRandom(j: CronJob): boolean {
  return j.scheduleKind.trim().toLowerCase() === "random";
}

// A random or window schedule: its words come from the Gateway, and its settings text is shown beside them.
function hasGatewayWords(j: CronJob): boolean {
  const k = j.scheduleKind.trim().toLowerCase();
  return k === "random" || k === "window";
}

function scheduleEnglish(j: CronJob): string {
  if (j.scheduleKind.toLowerCase() === "recurring") return cronToEnglish(j.cronExpression);
  // A random schedule's words are folded by the Gateway (issue #3622); its settings text is shown as stored
  // only when the Gateway sent no words, which it does for settings that no longer validate.
  if (isRandom(j)) return j.scheduleText ?? `Random: ${j.cronExpression ?? ""}`;
  // A window schedule's words, with the minute the Gateway placed it at, are the Gateway's.
  if (j.scheduleKind.trim().toLowerCase() === "window") return j.scheduleText ?? `Window: ${j.cronExpression ?? ""}`;
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
