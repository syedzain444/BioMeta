"""
Pull attendance + employee details directly from device (TCP 9922, no push).

  python pull_from_device.py                    # attendance -> output/
  python pull_from_device.py --employees 25258 25426
  python pull_from_device.py --from-attendance  # GetEmployee for each id in logs
  python pull_from_device.py --scan 25000 26000 # slow: try each id (optional)

Bulk list (all 400 users) is NOT supported on M2000A via 9922.
Alternatives: USB export (MENU->Data/USB), or Push User Data ON + push_listener.
"""
from __future__ import annotations

import argparse
import csv
import json
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

from device_hdcp import DEFAULT_HOST, hdcp_call

IP = DEFAULT_HOST
OUT = Path(__file__).resolve().parent / "output"


def hdcp_retry(host: str, command: str, timeout: float = 60.0, retries: int = 10, pause: float = 2.0) -> str:
    return hdcp_call(host, command, timeout=timeout, retries=retries, busy_pause=pause)


def parse_fields(block: str) -> dict[str, str]:
    return {m.group(1): m.group(2) for m in re.finditer(r'(\w+)="([^"]*)"', block)}


def parse_return(raw: str) -> tuple[dict[str, str], list[dict[str, str]]]:
    """Header fields + repeated line blocks (attendance rows)."""
    if not raw.startswith("Return("):
        return {}, []
    inner = raw[len("Return(") : raw.rfind(")")]
    lines = [ln.strip() for ln in inner.splitlines() if ln.strip()]
    header: dict[str, str] = {}
    rows: list[dict[str, str]] = []
    for ln in lines:
        f = parse_fields(ln)
        if not f:
            continue
        if "time" in f and "id" in f:
            rows.append(f)
        elif not rows:
            header.update(f)
    return header, rows


def pull_attendance(host: str, start: str, end: str) -> list[dict[str, str]]:
    cmd = f'GetRecord(start_time="{start}" end_time="{end}")'
    raw = hdcp_retry(host, cmd)
    if 'result="success"' not in raw:
        raise RuntimeError(raw[:500] if raw else "empty response")
    _, rows = parse_return(raw)
    return rows


def pull_employee(host: str, emp_id: str, include_face: bool) -> dict | None:
    try:
        raw = hdcp_call(host, f'GetEmployee(employee_id="{emp_id}")', timeout=45, retries=10)
    except OSError:
        return None
    if 'result="success"' not in raw or "unknown id" in raw:
        return None
    head = raw.split("face_data=")[0]
    head_one_line = " ".join(head.splitlines())
    fields = parse_fields(head_one_line)
    if include_face:
        faces = re.findall(r'face_data="([^"]*)"', raw, re.S)
        fields["face_template_count"] = len(faces)
    return fields


def save_json(name: str, data: object) -> Path:
    OUT.mkdir(exist_ok=True)
    p = OUT / name
    with p.open("w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    return p


def main() -> None:
    p = argparse.ArgumentParser(description="Direct pull from Hanwang terminal :9922")
    p.add_argument("--host", default=IP)
    p.add_argument("--start", default="2020-01-01 00:00:00")
    p.add_argument("--end", default="2030-12-31 23:59:59")
    p.add_argument("--employees", nargs="*", help="Employee IDs to fetch")
    p.add_argument("--from-attendance", action="store_true", help="Fetch employees for ids seen in attendance")
    p.add_argument(
        "--scan",
        nargs=2,
        metavar=("MIN", "MAX"),
        type=int,
        help="Try every employee ID in range (slow). Skips attendance pull.",
    )
    p.add_argument("--with-face", action="store_true", help="Include face template in export (large)")
    p.add_argument("--delay", type=float, default=0.6, help="Delay between GetEmployee calls (scan: use 0.8+)")
    p.add_argument(
        "--with-attendance",
        action="store_true",
        help="Also pull attendance when using --scan (may hit Device Busy)",
    )
    args = p.parse_args()

    info = hdcp_retry(args.host, "GetDeviceInfo()", timeout=15)
    dev = parse_fields(info.replace("\n", " ")).get("dev_id", "?")
    print("Device:", dev, flush=True)
    if "Device Is Busy" in info:
        print("Warning: device busy — stop dotnet dashboard / push_listener / other 9922 clients, then retry.")

    rows: list[dict[str, str]] = []
    if not args.scan or args.with_attendance:
        try:
            rows = pull_attendance(args.host, args.start, args.end)
            att_path = save_json(
                f"attendance_{datetime.now().strftime('%Y%m%d_%H%M%S')}.json",
                rows,
            )
            csv_path = OUT / att_path.name.replace(".json", ".csv")
            if rows:
                with csv_path.open("w", newline="", encoding="utf-8") as f:
                    w = csv.DictWriter(f, fieldnames=sorted({k for r in rows for k in r}))
                    w.writeheader()
                    w.writerows(rows)
            print(f"Attendance: {len(rows)} rows -> {att_path}")
            if rows:
                print(f"            CSV -> {csv_path}")
        except RuntimeError as e:
            if args.scan:
                print(f"Attendance skipped ({e})")
            else:
                raise
    elif args.scan:
        print("Scan mode: skipping attendance (use --with-attendance to include).")

    ids: set[str] = set(args.employees or [])
    if args.from_attendance:
        ids.update(r.get("id", "") for r in rows if r.get("id"))
    if args.scan:
        lo, hi = args.scan
        ids.update(str(i) for i in range(lo, hi + 1))
        print(f"Scanning employee IDs {lo} .. {hi} ({len(ids)} tries). This may take a while.")

    if not ids:
        print(
            "\nEmployee full list: device does not support GetAllEmployee on 9922.\n"
            "  - Use --employees ID ... or --from-attendance\n"
            "  - Or MENU -> USB Management -> export users to USB\n"
            "  - Or Push User Data ON + push_listener (Employee messages)\n"
        )
        return

    employees: list[dict] = []
    scan_path = OUT / "employees_scan_inprogress.json"
    if args.scan and scan_path.exists():
        try:
            employees = json.loads(scan_path.read_text(encoding="utf-8"))
            print(f"Resuming scan with {len(employees)} users already found.")
        except json.JSONDecodeError:
            employees = []
    found_ids = {e.get("id") or e.get("employee_id") for e in employees}

    progress_path = OUT / "employees_scan_progress.json"
    last_num = 0
    if args.scan and progress_path.exists():
        try:
            last_num = int(json.loads(progress_path.read_text()).get("last_id", 0))
        except (json.JSONDecodeError, ValueError, TypeError):
            last_num = 0

    sorted_ids = sorted(ids, key=lambda x: (len(x), x))
    for n, eid in enumerate(sorted_ids, start=1):
        if not eid or eid in found_ids:
            continue
        if args.scan and eid.isdigit() and int(eid) <= last_num:
            continue
        emp = pull_employee(args.host, eid, args.with_face)
        if emp:
            employees.append(emp)
            found_ids.add(eid)
            print(f"  OK [{n}/{len(ids)}] {eid}: {emp.get('name', '?')}")
        if args.scan:
            scan_path.parent.mkdir(exist_ok=True)
            scan_path.write_text(json.dumps(employees, ensure_ascii=False, indent=2), encoding="utf-8")
            if eid.isdigit():
                progress_path.write_text(json.dumps({"last_id": int(eid)}), encoding="utf-8")
        if n % 50 == 0:
            print(f"  ... progress {n}/{len(ids)}, found {len(employees)}")
        time.sleep(args.delay)

    emp_path = save_json(
        f"employees_{datetime.now().strftime('%Y%m%d_%H%M%S')}.json",
        employees,
    )
    if args.scan and scan_path.exists():
        scan_path.unlink(missing_ok=True)
    print(f"Employees: {len(employees)} saved -> {emp_path}")


if __name__ == "__main__":
    main()
