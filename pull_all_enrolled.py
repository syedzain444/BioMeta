"""
Pull ALL enrolled users without knowing IDs beforehand.

M2000A does NOT support GetAllEmployee on port 9922. Two working approaches:

  A) PUSH (recommended) — device sends every user to your PC
     1. Device: Push to Server ON, User Data ON, server IP = this PC, ports 9920/9924
     2. Run: python push_listener.py --pull-cmd 'SyncEmployee()'
        or:  python pull_all_enrolled.py --wait-push
     3. Wait or trigger upload from device; users -> output/enrolled_from_push.jsonl

  B) AUTO SCAN — tries numeric IDs in a range (slow, no password, no manual IDs)
     python pull_all_enrolled.py --scan --from-id 10000 --to-id 99999

  C) USB — MENU -> USB Management -> export users (fastest for full list offline)
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

from fetch_data import hdcp_call, IP
from pull_from_device import OUT, pull_employee

ENROLLED_PUSH = Path(__file__).resolve().parent / "output" / "enrolled_from_push.jsonl"
SCAN_OUT = OUT / "enrolled_scan.json"
PROGRESS_FILE = OUT / "enrolled_scan_progress.json"


def device_employee_count(host: str) -> int:
    raw = hdcp_call(host, "GetDeviceInfo()", timeout=15)
    m = re.search(r'real_employee="(\d+)"', raw)
    return int(m.group(1)) if m else 0


def scan_range(host: str, start: int, end: int, delay: float, resume: bool) -> None:
    OUT.mkdir(exist_ok=True)
    found: dict[str, dict] = {}
    last_id = start - 1
    if resume and SCAN_OUT.exists():
        found = {r["employee_id"]: r for r in json.loads(SCAN_OUT.read_text(encoding="utf-8"))}
    if resume and PROGRESS_FILE.exists():
        last_id = json.loads(PROGRESS_FILE.read_text()).get("last_id", last_id)

    target = device_employee_count(host)
    print(f"Device reports ~{target} enrolled. Scanning IDs {start}..{end} (resume from {last_id + 1})")

    for num in range(max(start, last_id + 1), end + 1):
        eid = str(num)
        if eid in found:
            continue
        try:
            emp = pull_employee(host, eid, include_face=False)
            if emp:
                found[eid] = emp
                print(f"  + {eid} {emp.get('name', '?')}  ({len(found)}/{target or '?'})")
                SCAN_OUT.write_text(json.dumps(list(found.values()), ensure_ascii=False, indent=2), encoding="utf-8")
                if target and len(found) >= target:
                    print("Reached device employee count.")
                    break
        except Exception as ex:
            if "Busy" in str(ex):
                time.sleep(2)
                continue
        PROGRESS_FILE.write_text(json.dumps({"last_id": num}), encoding="utf-8")
        time.sleep(delay)

    SCAN_OUT.write_text(json.dumps(list(found.values()), ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Done. {len(found)} users -> {SCAN_OUT}")


def wait_push(existing: Path) -> None:
    print("Enable on device: Push ON + User Data ON, server = this PC (9920/9924)")
    print("Run in another terminal: python push_listener.py")
    print(f"Watching {existing} ... Ctrl+C to stop\n")
    seen = 0
    while True:
        if existing.exists():
            lines = [ln for ln in existing.read_text(encoding="utf-8").splitlines() if ln.strip()]
            if len(lines) != seen:
                seen = len(lines)
                print(f"  collected {seen} user rows")
        time.sleep(3)


def main() -> None:
    p = argparse.ArgumentParser(description="Enrolled users without manual ID list")
    p.add_argument("--host", default=IP)
    p.add_argument("--scan", action="store_true", help="Brute-scan ID range via GetEmployee")
    p.add_argument("--from-id", type=int, default=10000)
    p.add_argument("--to-id", type=int, default=99999)
    p.add_argument("--delay", type=float, default=0.2)
    p.add_argument("--no-resume", action="store_true")
    p.add_argument("--wait-push", action="store_true", help="Monitor push export file")
    args = p.parse_args()

    if args.wait_push:
        wait_push(ENROLLED_PUSH)
        return

    if args.scan:
        scan_range(args.host, args.from_id, args.to_id, args.delay, resume=not args.no_resume)
        return

    print(__doc__)
    print("\nChoose: --wait-push  OR  --scan  OR  USB export from device menu.")


if __name__ == "__main__":
    main()
