"""
Scan enrolled users by trying GetEmployee(id) on port 9922.
Safe for connection resets — saves progress, resumes automatically.

BEFORE RUNNING:
  - Stop: dotnet dashboard, push_listener (9920 ok), other Python on 9922
  - Device screen idle (not in menu)

  python scan_enrolled_users.py
  python scan_enrolled_users.py --from-id 20000 --to-id 30000 --delay 0.8
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

from device_hdcp import DEFAULT_HOST, hdcp_call

OUT = Path(__file__).resolve().parent / "output"
RESULT_FILE = OUT / "employees_scan_inprogress.json"
PROGRESS_FILE = OUT / "employees_scan_progress.json"


def parse_fields(block: str) -> dict[str, str]:
    return {m.group(1): m.group(2) for m in re.finditer(r'(\w+)="([^"]*)"', block)}


def get_device_target_count(host: str) -> int:
    raw = hdcp_call(host, "GetDeviceInfo()", timeout=20, retries=15)
    if "Device Is Busy" in raw:
        return 0
    m = re.search(r'real_employee="(\d+)"', raw.replace("\n", " "))
    return int(m.group(1)) if m else 0


def pull_one(host: str, emp_id: str, include_face: bool) -> dict | None:
    raw = hdcp_call(
        host,
        f'GetEmployee(employee_id="{emp_id}")',
        timeout=45,
        retries=10,
        busy_pause=3.0,
        error_pause=1.5,
    )
    if 'result="success"' not in raw or "unknown id" in raw:
        return None
    head = raw.split("face_data=")[0]
    fields = parse_fields(" ".join(head.splitlines()))
    if include_face:
        fields["face_template_count"] = len(re.findall(r'face_data="', raw))
    fields["employee_id"] = emp_id
    return fields


def load_state() -> tuple[list[dict], int]:
    employees: list[dict] = []
    last_id = 0
    if RESULT_FILE.exists():
        try:
            employees = json.loads(RESULT_FILE.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            employees = []
    if PROGRESS_FILE.exists():
        try:
            last_id = int(json.loads(PROGRESS_FILE.read_text()).get("last_id", 0))
        except (json.JSONDecodeError, ValueError, TypeError):
            last_id = 0
    return employees, last_id


def save_state(employees: list[dict], last_id: int) -> None:
    OUT.mkdir(exist_ok=True)
    RESULT_FILE.write_text(json.dumps(employees, ensure_ascii=False, indent=2), encoding="utf-8")
    PROGRESS_FILE.write_text(json.dumps({"last_id": last_id, "updated": datetime.now(timezone.utc).isoformat()}), encoding="utf-8")


def main() -> None:
    p = argparse.ArgumentParser(description="Resumable employee ID scan")
    p.add_argument("--host", default=DEFAULT_HOST)
    p.add_argument("--from-id", type=int, default=10000)
    p.add_argument("--to-id", type=int, default=99999)
    p.add_argument("--delay", type=float, default=0.6, help="Seconds between IDs (use 0.8-1.2 if resets)")
    p.add_argument("--with-face", action="store_true")
    p.add_argument("--stop-when-found", type=int, default=0, help="Stop after N users (0 = use device count)")
    args = p.parse_args()

    target = args.stop_when_found or get_device_target_count(args.host)
    employees, last_id = load_state()
    found_ids = {str(e.get("id") or e.get("employee_id")) for e in employees}

    start = max(args.from_id, last_id + 1)
    print(f"Host {args.host}:9922 | range {start}..{args.to_id} | already found {len(employees)} | target ~{target or '?'}")
    if start > args.to_id:
        print("Range already completed. Delete output/employees_scan_progress.json to rescan.")
        return

    streak_errors = 0
    for num in range(start, args.to_id + 1):
        eid = str(num)
        if eid in found_ids:
            continue
        try:
            emp = pull_one(args.host, eid, args.with_face)
            streak_errors = 0
            if emp:
                employees.append(emp)
                found_ids.add(eid)
                print(f"  + {eid} {emp.get('name', '?')}  (total {len(employees)})")
                save_state(employees, num)
                if target and len(employees) >= target:
                    print(f"Reached target count {target}.")
                    break
        except Exception as ex:
            streak_errors += 1
            print(f"  ! {eid} error: {ex} (streak {streak_errors})")
            save_state(employees, num - 1)
            if streak_errors >= 5:
                print("Too many errors — pausing 60s. Stop dotnet/push on 9922 if running.")
                time.sleep(60)
                streak_errors = 0
            else:
                time.sleep(5)
            continue

        save_state(employees, num)
        time.sleep(args.delay)

    final = OUT / f"employees_{datetime.now().strftime('%Y%m%d_%H%M%S')}.json"
    final.write_text(json.dumps(employees, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Done. {len(employees)} users -> {final}")
    print(f"Resume anytime: python scan_enrolled_users.py --from-id {args.from_id} --to-id {args.to_id}")


if __name__ == "__main__":
    main()
