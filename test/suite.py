#!/usr/bin/env python3
"""Planetbase Co-op test suite: real game copies under Proton, two lanes in parallel.

    python3 -I test/suite.py            # everything
    python3 -I test/suite.py join twin-no-commands

Each case prints PASS/FAIL. Twins (mutation twins) turn one protection off and must FAIL on
the named check; a twin that passes means the test does not exercise that protection.
"""
import subprocess
import sys
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
LAB_DIR = HERE.parent.parent / "lab"

# name, lab.py args (without host/client/port), expected ("pass" or a check name that must FAIL)
CASES = [
    ("boot", ["run", "boot", "{h}"], "pass"),
    ("join", ["run", "pair", "scenario=join"], "pass"),
    ("refuse", ["run", "pair", "scenario=refuse", "fake_mod=9.9.9"], "pass"),
    ("twin-no-lifecycle", ["run", "pair", "scenario=join", "mut=no-lifecycle,no-census"], "host-built module appears on the joiner"),
    ("twin-no-commands", ["run", "pair", "scenario=join", "mut=no-commands"], "joiner-placed module built by the host"),
    ("twin-no-char-states", ["run", "pair", "scenario=join", "mut=no-char-states"], "colonists are where the host has them"),
    ("twin-no-res-states", ["run", "pair", "scenario=join", "mut=no-res-states"], "resources have the same holder and place"),
    ("twin-no-census", ["run", "pair", "scenario=join", "mut=no-census"], "census restores the resource"),
    ("ships", ["run", "pair", "scenario=ships"], "pass"),
]
LANES = [("a", "b", "47610"), ("c", "d", "47620")]


def run_case(case, lane, results):
    name, args, expect = case
    h, c, port = lane
    if args[:2] == ["run", "pair"]:
        args = args + [f"host={h}", f"client={c}", f"port={port}", "timeout=1200"]
    else:
        args = [a.replace("{h}", h) for a in args] + ["timeout=300"]
    t0 = time.time()
    out = subprocess.run([sys.executable, "-I", str(HERE / "lab.py")] + args, capture_output=True, text=True)
    text = out.stdout + out.stderr
    status = (LAB_DIR / "runs" / h / "status.txt")
    host_lines = status.read_text(errors="replace").splitlines() if status.exists() else []
    passed = "RESULT: PASS" in text.splitlines()[-1:][0] if text.strip() else False
    if expect == "pass":
        ok = passed and out.returncode == 0
        why = "" if ok else next((l for l in host_lines if l.startswith("FAIL")), "no result")
    else:
        failed_named = any(l.startswith("FAIL " + expect) for l in host_lines)
        ok = (not passed) and failed_named
        why = f"twin {'caught' if ok else 'NOT caught'}: " + (next((l for l in host_lines if l.startswith("FAIL")), "no FAIL line"))
    keep = LAB_DIR / "suite" / name
    keep.mkdir(parents=True, exist_ok=True)
    for who in (h, c):
        for f in ("status.txt", "coop.log"):
            src = LAB_DIR / "runs" / who / f
            if src.exists():
                (keep / f"{who}-{f}").write_bytes(src.read_bytes())
    (keep / "lab-output.txt").write_text(text)
    results.append((name, ok, f"{time.time() - t0:.0f} s", why))
    print(f"{'PASS' if ok else 'FAIL'} {name} ({time.time() - t0:.0f} s) {why}", flush=True)


def main(argv):
    wanted = argv[1:]
    cases = [c for c in CASES if not wanted or c[0] in wanted]
    subprocess.check_call([sys.executable, "-I", str(HERE / "lab.py"), "provision"] + [x for lane in LANES for x in lane[:2]],
                          stdout=subprocess.DEVNULL)
    results, queue = [], list(cases)
    lock = threading.Lock()

    def worker(lane):
        while True:
            with lock:
                if not queue:
                    return
                case = queue.pop(0)
            run_case(case, lane, results)

    threads = [threading.Thread(target=worker, args=(lane,)) for lane in LANES]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    n_ok = sum(1 for r in results if r[1])
    print(f"SUITE: {n_ok}/{len(results)} passed")
    for r in results:
        print(f"  {'PASS' if r[1] else 'FAIL'} {r[0]} ({r[2]}) {r[3]}")
    return 0 if n_ok == len(results) and results else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
