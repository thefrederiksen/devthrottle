"""Throwaway measurement: how fast is a plain directory walk on this machine?

Read-only. Time-capped. Every error is counted and the first few are shown, never dropped.
Not product code.

usage: python scan_probe.py <root> <threads> <cap_seconds>
"""
import os
import stat
import sys
import threading
import time
from collections import Counter
from concurrent.futures import ThreadPoolExecutor

RECALL = 0x00400000  # FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: cloud placeholder, not on disk
OFFLINE = 0x00001000

root = sys.argv[1]
threads = int(sys.argv[2])
cap = float(sys.argv[3])
KEY_DEPTH = int(sys.argv[4]) if len(sys.argv) > 4 else 1
TOP_N = int(sys.argv[5]) if len(sys.argv) > 5 else 8

lock = threading.Lock()
totals = Counter()
top = Counter()
errors = []
pending = [0]
done = threading.Event()
deadline = time.perf_counter() + cap
capped = [False]


def walk(path, top_name):
    files = dirs = size = cloud = cloud_bytes = reparse = 0
    subdirs = []
    try:
        with os.scandir(path) as it:
            for e in it:
                try:
                    st = e.stat(follow_symlinks=False)
                except OSError as ex:
                    with lock:
                        totals["errors"] += 1
                        if len(errors) < 5:
                            errors.append("%s: %s" % (e.path, ex))
                    continue
                attrs = st.st_file_attributes
                if attrs & stat.FILE_ATTRIBUTE_REPARSE_POINT and stat.S_ISDIR(st.st_mode):
                    reparse += 1  # junction or link: never followed, or it is counted twice
                    continue
                if stat.S_ISDIR(st.st_mode):
                    dirs += 1
                    subdirs.append(e.path)
                else:
                    files += 1
                    if attrs & (RECALL | OFFLINE):
                        cloud += 1
                        cloud_bytes += st.st_size
                    else:
                        size += st.st_size
    except OSError as ex:
        with lock:
            totals["errors"] += 1
            if len(errors) < 5:
                errors.append("%s: %s" % (path, ex))
    with lock:
        totals["files"] += files
        totals["dirs"] += dirs
        totals["bytes"] += size
        totals["cloud_files"] += cloud
        totals["cloud_bytes"] += cloud_bytes
        totals["links_not_followed"] += reparse
        top[top_name] += size
    for sd in subdirs:
        if time.perf_counter() > deadline:
            capped[0] = True
            break
        # key on the first KEY_DEPTH path components under the root
        rel = os.path.relpath(sd, root).split(os.sep)
        name = os.sep.join(rel[:KEY_DEPTH])
        with lock:
            pending[0] += 1
        pool.submit(run, sd, name)


def run(path, top_name):
    try:
        walk(path, top_name)
    finally:
        with lock:
            pending[0] -= 1
            if pending[0] == 0:
                done.set()


start = time.perf_counter()
pool = ThreadPoolExecutor(max_workers=threads)
pending[0] = 1
pool.submit(run, root, "")
done.wait()
elapsed = time.perf_counter() - start
pool.shutdown(wait=True)

n = totals["files"] + totals["dirs"]
print("root=%s threads=%d" % (root, threads))
print("complete=%s (cap %ss)" % ("NO - CAPPED, totals are partial" if capped[0] else "yes", cap))
print("files=%d dirs=%d entries=%d" % (totals["files"], totals["dirs"], n))
print("on_disk_bytes=%.2f GB  cloud_placeholder_files=%d (%.2f GB logical, not on disk)" % (
    totals["bytes"] / 1024**3, totals["cloud_files"], totals["cloud_bytes"] / 1024**3))
print("links_not_followed=%d errors=%d" % (totals["links_not_followed"], totals["errors"]))
print("elapsed=%.1fs rate=%d entries/sec" % (elapsed, n / elapsed if elapsed else 0))
for e in errors:
    print("  error: " + e)
print("top children by size:")
for name, b in top.most_common(TOP_N):
    print("  %8.2f GB  %s" % (b / 1024**3, name or "(files in root)"))
