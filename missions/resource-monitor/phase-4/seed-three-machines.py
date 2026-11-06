"""Seed a local Gateway's statistics store with 24 hours of five-minute resource rows for two machines.

The rows are written in the shape ResourceSummaryStore writes them (tenant "local", EF Core's SQLite date text,
crossings as web-default camelCase JSON). The third machine, demo-win11, is given NO rows: it is the offline one.
"""
import json, math, random, sqlite3, sys
from datetime import datetime, timedelta, timezone

DB = sys.argv[1]
GB = 1_073_741_824
random.seed(2811)

now = datetime.now(timezone.utc).replace(tzinfo=None)
newest = now.replace(second=0, microsecond=0) - timedelta(minutes=now.minute % 5) - timedelta(minutes=5)

def t(d):
    return d.strftime("%Y-%m-%d %H:%M:%S")

def iso(d):
    return d.strftime("%Y-%m-%dT%H:%M:%SZ")

def hour(d):
    return d.replace(minute=0, second=0)

MACHINES = {
    "devthrottle-mac-mini": {"director": "seed-mac-1", "total": 16 * GB},
    "devlinux": {"director": "seed-linux-1", "total": 39 * GB},
}

FACTORIES = {
    "devthrottle-mac-mini": [("Marketing", 4, 5.5, 15.0, 0.78, 1.45), ("Website Black", 3, 4.6, 14.6, 0.8, 1.36), (None, 1, 4.0, 12.0, 0.6, 0.9)],
    "devlinux": [("Biographies", 1, 31.0, 97.0, 1.9, 6.2), ("Marketing", 2, 9.0, 30.0, 1.1, 2.4), (None, 1, 3.0, 9.0, 0.5, 0.8)],
}

c = sqlite3.connect(DB)
c.execute("delete from resource_window where tenant='local'")
c.execute("delete from resource_session_window where tenant='local'")

for machine, m in MACHINES.items():
    total = m["total"]
    for i in range(24 * 12):
        start = newest - timedelta(minutes=5 * i)
        hours_ago = i / 12
        refused = None
        cpu_avg = cpu_peak = mem_avg = mem_peak = None
        cpu_reason = mem_reason = None
        crossings = []
        diurnal = 0.5 + 0.5 * math.sin((start.hour + start.minute / 60) / 24 * 2 * math.pi)
        if machine == "devthrottle-mac-mini":
            cpu_avg = 14 + 18 * diurnal + random.uniform(-4, 4)
            cpu_peak = min(cpu_avg + random.uniform(15, 40), 100)
            mem_used = (8.2 + 0.8 * diurnal + random.uniform(-0.2, 0.2)) * GB
            mem_avg, mem_peak = mem_used, mem_used + random.uniform(0.1, 0.4) * GB
            if i == 0:
                cpu_avg, cpu_peak = 28.0, 52.0
                mem_avg, mem_peak = 8.9 * GB, 8.95 * GB
            if i == 3:
                cpu_peak = 71.0
                mem_peak = 8.9 * GB
        else:
            cpu_avg = 35 + 25 * diurnal + random.uniform(-6, 6)
            cpu_peak = min(cpu_avg + random.uniform(10, 30), 100)
            mem_used = (11.5 + 1.2 * diurnal + random.uniform(-0.3, 0.3)) * GB
            mem_avg, mem_peak = mem_used, mem_used + random.uniform(0.2, 0.6) * GB
            if i < 3:  # the last fifteen minutes: hot, amber now
                cpu_avg, cpu_peak = 88.0 - i, 96.0 + i
                crossings.append({"resource": "processor", "level": "amber", "startedUtc": iso(start + timedelta(seconds=40)), "seconds": 260 - 40 * i, "peakPercent": cpu_peak})
            if i == 2:
                cpu_peak = 99.0
                crossings[0]["peakPercent"] = 99.0
            if i == 0:
                mem_avg, mem_peak = 13.2 * GB, 13.6 * GB
            if i == 4:
                mem_peak = 14.0 * GB
            if i == 16:  # a red crossing about an hour and twenty minutes ago
                cpu_avg, cpu_peak = 81.0, 100.0
                crossings.append({"resource": "processor", "level": "red", "startedUtc": iso(start + timedelta(seconds=95)), "seconds": 90, "peakPercent": 100.0})
            if i == 40:  # memory amber about three hours ago
                mem_avg, mem_peak = 34.0 * GB, 36.5 * GB
                crossings.append({"resource": "memory", "level": "amber", "startedUtc": iso(start + timedelta(seconds=20)), "seconds": 150, "peakPercent": 93.6})
            if 72 <= i < 76:  # six hours ago the processor could not be read for twenty minutes
                cpu_avg = cpu_peak = None
                cpu_reason = "the processor counters did not answer"
            if i == 120:  # ten hours ago the Gateway refused a window
                refused = "its processor peak was 412%, above the 100% a whole machine can reach"
                cpu_avg = cpu_peak = mem_avg = mem_peak = None
        row = dict(
            tenant="local", director_id=m["director"], window_start_utc=t(start), hour_start_utc=t(hour(start)),
            machine_name=machine, window_seconds=300, sample_count=60, received_utc=t(start + timedelta(minutes=5, seconds=3)),
            refused_reason=refused,
            processor_samples=0 if cpu_avg is None else 60,
            processor_average_percent=None if cpu_avg is None else round(cpu_avg, 2),
            processor_peak_percent=None if cpu_peak is None else round(cpu_peak, 2),
            processor_unreadable_reason=cpu_reason,
            memory_samples=0 if mem_avg is None else 60,
            memory_average_used_bytes=None if mem_avg is None else int(mem_avg),
            memory_peak_used_bytes=None if mem_peak is None else int(mem_peak),
            memory_total_bytes=None if refused else total,
            memory_unreadable_reason=None,
            crossings_json=json.dumps(crossings),
        )
        c.execute(f"insert into resource_window ({','.join(row)}) values ({','.join('?' * len(row))})", list(row.values()))

        if refused:
            continue
        for factory, sessions, avg, peak, mavg, mpeak in FACTORIES[machine]:
            for s in range(sessions):
                active = (i + s * 7) % 9 != 0
                if not active:
                    continue
                share = 1.0 / sessions
                pa = avg * share * random.uniform(0.6, 1.4)
                pp = peak * share * (1.0 if i == 10 + s else random.uniform(0.3, 0.8))
                ma = mavg * share * random.uniform(0.8, 1.2) * GB
                mp = mpeak * share * (1.0 if i == 10 + s else random.uniform(0.5, 0.9)) * GB
                srow = dict(
                    tenant="local", director_id=m["director"], window_start_utc=t(start), session_id=f"{machine}-{factory or 'none'}-{s}",
                    hour_start_utc=t(hour(start)), machine_name=machine, factory=factory, sample_count=60,
                    processes_with_unknown_figures=1 if (factory == "Marketing" and machine == "devlinux" and i == 30) else 0,
                    received_utc=t(start + timedelta(minutes=5, seconds=3)),
                    processor_samples=60, processor_average_percent=round(pa, 2), processor_peak_percent=round(max(pp, pa), 2),
                    processor_unreadable_reason=None, memory_samples=60, memory_average_used_bytes=int(ma),
                    memory_peak_used_bytes=int(max(mp, ma)), memory_total_bytes=total, memory_unreadable_reason=None, crossings_json="[]",
                )
                c.execute(f"insert into resource_session_window ({','.join(srow)}) values ({','.join('?' * len(srow))})", list(srow.values()))

c.commit()
print("newest window", t(newest), "rows", c.execute("select count(*) from resource_window").fetchone(), c.execute("select count(*) from resource_session_window").fetchone())
