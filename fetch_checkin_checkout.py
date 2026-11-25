"""Daily check-in / check-out for employee IDs (Oracle log and/or Hanwang device).

Check-in = first punch of the day; check-out = last punch when count > 1.

Examples:
  python fetch_checkin_checkout.py --source oracle
  python fetch_checkin_checkout.py --source device --host 10.100.23.124
  python fetch_checkin_checkout.py --source auto
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from collections import defaultdict
from datetime import datetime, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parent
OUT = ROOT / "output"

DEFAULT_IDS = ["5001", "5002", "5003", "5004"]
DEFAULT_FROM = "2026-10-01"
DEFAULT_TO = "2026-11-04"

ORACLE_USER = os.environ.get("GATE_ORACLE_USER", "entrygate_surv")
ORACLE_PASSWORD = os.environ.get("GATE_ORACLE_PASSWORD", "sma")
ORACLE_DSN = os.environ.get(
    "GATE_ORACLE_DSN",
    "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=172.16.10.60)(PORT=1521))"
    "(CONNECT_DATA=(SID=orcl)))",
)


def load_appsettings_oracle() -> tuple[str, str, str]:
    user, password, dsn = ORACLE_USER, ORACLE_PASSWORD, ORACLE_DSN
    appsettings = ROOT / "src" / "BiometricDashboard.Web" / "appsettings.json"
    if not appsettings.is_file():
        return user, password, dsn
    text = appsettings.read_text(encoding="utf-8")
    m = re.search(r'"GateEntryOracle"\s*:\s*"([^"]+)"', text, re.S)
    if not m:
        return user, password, dsn
    cs = m.group(1)
    for part in cs.split(";"):
        part = part.strip()
        if part.lower().startswith("user id="):
            user = part.split("=", 1)[1].strip()
        elif part.lower().startswith("password="):
            password = part.split("=", 1)[1].strip()
        elif part.lower().startswith("data source="):
            dsn = part.split("=", 1)[1].strip()
    return user, password, dsn


def normalize_id(raw: str) -> str:
    s = (raw or "").strip()
    if not s:
        return s
    z = s.lstrip("0")
    return z if z else "0"


def aggregate_punches(
    punches: list[tuple[str, str, datetime]],
    id_filter: set[str] | None,
) -> list[dict]:
    """(employee_id, name, punch_time)"""
    by_key: dict[tuple[str, datetime.date], list[tuple[datetime, str]]] = defaultdict(list)
    names: dict[str, str] = {}
    for emp_id, name, pt in punches:
        nid = normalize_id(emp_id)
        if id_filter and nid not in id_filter and emp_id.strip() not in id_filter:
            continue
        names[nid] = (name or "").strip() or names.get(nid, "")
        day = pt.date()
        by_key[(nid, day)].append((pt, ""))

    rows: list[dict] = []
    for (emp_id, day), items in sorted(by_key.items()):
        items.sort(key=lambda x: x[0])
        first = items[0][0]
        last = items[-1][0]
        rows.append(
            {
                "employee_id": emp_id,
                "employee_name": names.get(emp_id, ""),
                "date": day.isoformat(),
                "check_in": first.strftime("%Y-%m-%d %H:%M:%S"),
                "check_out": last.strftime("%Y-%m-%d %H:%M:%S") if len(items) > 1 else "",
                "punch_count": len(items),
            }
        )
    return rows


def oracle_connect_string() -> str:
    user, password, dsn = load_appsettings_oracle()
    return f'{user}/{password}@{dsn}'


def fetch_oracle_sqlplus(ids: list[str], date_from: str, date_to: str) -> list[tuple[str, str, datetime]]:
    sqlplus = shutil.which("sqlplus")
    if not sqlplus:
        raise RuntimeError("sqlplus not found on PATH (required for Oracle 11g from Python).")

    id_list = ",".join(f"'{i}'" for i in ids)
    id_list_z = ",".join(f"'{i}'" for i in ids)
    end_exclusive = (datetime.strptime(date_to, "%Y-%m-%d") + timedelta(days=1)).strftime("%Y-%m-%d")

    sql = f"""
SET PAGESIZE 50000 LINESIZE 400 FEEDBACK OFF VERIFY OFF HEADING OFF
SELECT TRIM(EMPLOYEE_ID) || chr(9) || REPLACE(NVL(EMPLOYEE_NAME,' '), chr(9), ' ') || chr(9) ||
       TO_CHAR(PUNCH_TIME, 'YYYY-MM-DD HH24:MI:SS')
FROM BIOMETRIC_ATTENDANCE_LOG
WHERE PUNCH_TIME >= TO_DATE('{date_from} 00:00:00', 'YYYY-MM-DD HH24:MI:SS')
  AND PUNCH_TIME <  TO_DATE('{end_exclusive} 00:00:00', 'YYYY-MM-DD HH24:MI:SS')
  AND (
        TRIM(EMPLOYEE_ID) IN ({id_list})
     OR LTRIM(TRIM(EMPLOYEE_ID), '0') IN ({id_list_z})
      )
ORDER BY EMPLOYEE_ID, PUNCH_TIME;
EXIT;
"""
    with tempfile.NamedTemporaryFile("w", suffix=".sql", delete=False, encoding="utf-8") as tf:
        tf.write(sql)
        sql_path = tf.name

    try:
        proc = subprocess.run(
            [sqlplus, "-S", oracle_connect_string(), f"@{sql_path}"],
            capture_output=True,
            text=True,
            timeout=120,
        )
    finally:
        Path(sql_path).unlink(missing_ok=True)

    if proc.returncode != 0 and not proc.stdout.strip():
        raise RuntimeError(proc.stderr or proc.stdout or "sqlplus failed")

    punches: list[tuple[str, str, datetime]] = []
    for line in proc.stdout.splitlines():
        line = line.strip()
        if not line or line.startswith("SP2-") or "ORA-" in line:
            if "ORA-" in line:
                raise RuntimeError(line)
            continue
        parts = line.split("\t")
        if len(parts) < 3:
            continue
        emp_id, name, tstr = parts[0], parts[1], parts[2]
        try:
            pt = datetime.strptime(tstr, "%Y-%m-%d %H:%M:%S")
        except ValueError:
            continue
        punches.append((emp_id, name, pt))
    return punches


def fetch_oracle_oracledb(ids: list[str], date_from: str, date_to: str) -> list[tuple[str, str, datetime]]:
    import oracledb

    user, password, dsn = load_appsettings_oracle()
    end_exclusive = (datetime.strptime(date_to, "%Y-%m-%d") + timedelta(days=1)).strftime("%Y-%m-%d 00:00:00")
    id_ph = ", ".join(f":id{i}" for i in range(len(ids)))
    sql = f"""
SELECT TRIM(EMPLOYEE_ID), NVL(EMPLOYEE_NAME,' '), PUNCH_TIME
FROM BIOMETRIC_ATTENDANCE_LOG
WHERE PUNCH_TIME >= TO_DATE(:dt_from, 'YYYY-MM-DD HH24:MI:SS')
  AND PUNCH_TIME <  TO_DATE(:dt_to, 'YYYY-MM-DD HH24:MI:SS')
  AND (TRIM(EMPLOYEE_ID) IN ({id_ph}) OR LTRIM(TRIM(EMPLOYEE_ID), '0') IN ({id_ph}))
ORDER BY 1, 3
"""
    params: dict = {"dt_from": f"{date_from} 00:00:00", "dt_to": end_exclusive}
    for i, eid in enumerate(ids):
        params[f"id{i}"] = eid

    lib = os.environ.get("ORACLE_CLIENT_LIB")
    if lib:
        try:
            oracledb.init_oracle_client(lib_dir=lib)
        except oracledb.Error:
            pass

    with oracledb.connect(user=user, password=password, dsn=dsn) as conn:
        with conn.cursor() as cur:
            cur.execute(sql, params)
            rows = cur.fetchall()

    out: list[tuple[str, str, datetime]] = []
    for emp_id, name, pt in rows:
        out.append((str(emp_id).strip(), str(name or ""), pt))
    return out


def fetch_oracle(ids: list[str], date_from: str, date_to: str) -> list[tuple[str, str, datetime]]:
    if shutil.which("sqlplus"):
        return fetch_oracle_sqlplus(ids, date_from, date_to)
    return fetch_oracle_oracledb(ids, date_from, date_to)


def fetch_device(host: str, date_from: str, date_to: str) -> list[tuple[str, str, datetime]]:
    from pull_from_device import parse_return, pull_attendance

    start = f"{date_from} 00:00:00"
    end = f"{date_to} 23:59:59"
    raw_rows = pull_attendance(host, start, end)
    punches: list[tuple[str, str, datetime]] = []
    for r in raw_rows:
        emp_id = r.get("id", "")
        name = r.get("name", "")
        tstr = r.get("time", "")
        if not emp_id or not tstr:
            continue
        try:
            pt = datetime.strptime(tstr, "%Y-%m-%d %H:%M:%S")
        except ValueError:
            continue
        punches.append((emp_id, name, pt))
    return punches


def write_outputs(records: list[dict], stem: str) -> tuple[Path, Path]:
    OUT.mkdir(exist_ok=True)
    csv_path = OUT / f"{stem}.csv"
    json_path = OUT / f"{stem}.json"
    fields = ["employee_id", "employee_name", "date", "check_in", "check_out", "punch_count"]
    with csv_path.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        w.writeheader()
        w.writerows(records)
    json_path.write_text(json.dumps(records, indent=2, ensure_ascii=False), encoding="utf-8")
    return csv_path, json_path


def print_table(records: list[dict]) -> None:
    if not records:
        print("No rows (no punches for these IDs in the date range).")
        return
    cols = ("employee_id", "employee_name", "date", "check_in", "check_out", "punch_count")
    widths = {c: max(len(c), *(len(str(r.get(c, ""))) for r in records)) for c in cols}
    print(" | ".join(c.ljust(widths[c]) for c in cols))
    print("-+-".join("-" * widths[c] for c in cols))
    for r in records:
        print(" | ".join(str(r.get(c, "")).ljust(widths[c]) for c in cols))


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--ids", nargs="+", default=DEFAULT_IDS)
    p.add_argument("--from", dest="date_from", default=DEFAULT_FROM)
    p.add_argument("--to", dest="date_to", default=DEFAULT_TO)
    p.add_argument("--source", choices=("oracle", "device", "auto"), default="auto")
    p.add_argument("--host", default=os.environ.get("BIO_DEVICE_HOST", "172.16.40.63"))
    p.add_argument(
        "--try-device",
        action="store_true",
        help="After Oracle, pull from device if no rows (needs network to gate :9922)",
    )
    p.add_argument("--out", default="checkin_checkout", help="Output file stem under output/")
    args = p.parse_args()

    id_filter = {normalize_id(i) for i in args.ids} | set(args.ids)
    print(f"IDs: {', '.join(args.ids)}")
    print(f"Range: {args.date_from} .. {args.date_to} (inclusive)\n")

    punches: list[tuple[str, str, datetime]] = []
    source_used = ""

    if args.source in ("oracle", "auto"):
        try:
            punches = fetch_oracle(args.ids, args.date_from, args.date_to)
            source_used = "oracle"
            print(f"Oracle: {len(punches)} raw punch(es).")
        except Exception as e:
            print(f"Oracle: {e}")
            if args.source == "oracle":
                return 1

    try_device = args.source == "device" or args.try_device or (
        args.source == "auto" and os.environ.get("BIO_TRY_DEVICE", "").lower() in ("1", "true", "yes")
    )
    if try_device and not punches:
        try:
            punches = fetch_device(args.host, args.date_from, args.date_to)
            source_used = f"device ({args.host})"
            print(f"Device: {len(punches)} raw punch(es).")
        except Exception as e:
            print(f"Device: {e}")
            if args.source == "device":
                return 1
    elif args.source == "auto" and source_used == "oracle" and not punches:
        print(
            "Tip: IDs not in Oracle for this range. Pull from terminal:\n"
            f"  python fetch_checkin_checkout.py --source device --host <GATE_IP> "
            f"--from {args.date_from} --to {args.date_to} --ids {' '.join(args.ids)}"
        )

    records = aggregate_punches(punches, id_filter)
    print(f"Source: {source_used or 'none'}\n")
    print_table(records)

    stem = f"{args.out}_{args.date_from}_{args.date_to}"
    csv_path, json_path = write_outputs(records, stem)
    print(f"\nWrote {csv_path}")
    print(f"Wrote {json_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
