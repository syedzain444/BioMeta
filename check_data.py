"""Quick status via HDCP port 9922 (no comm key).

Device MENU admin PINs 10492 / 1990 are NOT used here — those are for the keypad only.
Run: python check_data.py [IP] [employee_id]
"""
import sys
from fetch_data import hdcp_call, IP


def main() -> None:
    host = sys.argv[1] if len(sys.argv) > 1 else IP
    print(f"Checking {host}:9922 ...\n")

    info = hdcp_call(host, "GetDeviceInfo()")
    print("=== Device ===")
    print(info)
    print()

    work = hdcp_call(host, "GetWorkStatus()")
    print("=== Work status ===")
    print(work)
    print()

    rec = hdcp_call(
        host,
        'GetRecord(start_time="2020-01-01 00:00:00" end_time="2030-12-31 23:59:59")',
    )
    print("=== Attendance records (on device) ===")
    print(rec)
    print()

    emp_id = sys.argv[2] if len(sys.argv) > 2 else None
    if emp_id:
        r = hdcp_call(host, f'GetEmployee(employee_id="{emp_id}")')
        print(f"=== Employee {emp_id} ===")
        print(r)


if __name__ == "__main__":
    main()
