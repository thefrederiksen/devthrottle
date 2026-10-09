import type { CronLoad, CronLoadHour } from "@devthrottle/client-core/schedule/scheduleClient";

// The load strip (the owner, 2026-10-09): one machine's next 24 hours as bars, the most scheduled sessions open at
// once in each hour, against the capacity line. Everything it says is folded by the Gateway (GET /cron/load); this
// only lays the bars out. Tapping a bar filters the list to the schedules open in that hour.

interface Props {
  load: CronLoad;
  machine: string;
  onMachine: (machine: string) => void;
  /** The startUtc of the hour the list is filtered to, or null. */
  selectedHour: string | null;
  onSelectHour: (hour: CronLoadHour | null) => void;
}

/** Every third hour is labelled under the bars, so 24 labels never crowd a phone-width strip. */
const LABEL_EVERY = 3;

export function LoadStrip({ load, machine, onMachine, selectedHour, onSelectHour }: Props) {
  const current = load.machines.find((m) => m.machine === machine) ?? load.machines[0];
  if (current === undefined) return null;
  // The scale reaches the capacity line even on a quiet day, so a short bar reads as short.
  const scale = Math.max(current.peak, load.capacity, 1);
  const capacityPercent = (load.capacity / scale) * 100;

  return (
    <section className="sched-load" aria-label="Scheduled load, next 24 hours">
      <div className="sched-load-head">
        <span className="sched-load-title">Next 24 hours</span>
        {load.machines.length > 1 && (
          <span className="sched-groupswitch" role="group" aria-label="Machine">
            {load.machines.map((m) => (
              <button
                key={m.machine}
                type="button"
                className={`sched-groupbtn${m.machine === current.machine ? " on" : ""}`}
                aria-pressed={m.machine === current.machine}
                onClick={() => onMachine(m.machine)}
              >
                {m.machine}
              </button>
            ))}
          </span>
        )}
        <span className="sched-load-summary">{current.summary}</span>
      </div>
      <div className="sched-load-chart">
        <div className="sched-load-capacity" style={{ bottom: `${capacityPercent}%` }}>
          <span>capacity {load.capacity}</span>
        </div>
        {current.hours.map((h) => {
          const selected = h.startUtc === selectedHour;
          return (
            <button
              key={h.startUtc}
              type="button"
              className={`sched-load-col${selected ? " selected" : ""}`}
              title={`${h.label} - at most ${h.concurrent} open, ${h.starts} start${h.starts === 1 ? "" : "s"}`}
              aria-label={`${h.label}: at most ${h.concurrent} open, ${h.starts} starting`}
              aria-pressed={selected}
              disabled={h.jobIds.length === 0}
              onClick={() => onSelectHour(selected ? null : h)}
            >
              <span
                className={`sched-load-bar${h.over ? " over" : ""}`}
                style={{ height: `${(h.concurrent / scale) * 100}%` }}
              />
            </button>
          );
        })}
      </div>
      <div className="sched-load-axis" aria-hidden="true">
        {current.hours.map((h, i) => (
          <span key={h.startUtc}>{i % LABEL_EVERY === 0 ? h.label : ""}</span>
        ))}
      </div>
      {current.estimateNote !== "" && <div className="sched-load-note">{current.estimateNote}</div>}
    </section>
  );
}
