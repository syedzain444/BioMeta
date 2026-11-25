"""Try Hanwang HDCP commands on 9922 — one TCP connection per command."""
import sys
import time

import socket

from device_hdcp import DEFAULT_HOST, DEFAULT_PORT, hdcp_call as hdcp_request

IP = DEFAULT_HOST
PORT = DEFAULT_PORT

COMMANDS = [
    "GetDeviceInfo()",
    "GetEmployeeInfo()",
    "GetEmployee()",
    "GetAllEmployee()",
    "GetAllEmployeeInfo()",
    "GetEmployeeList()",
    "GetEmployInfo()",
    "GetDeviceRecord()",
    "GetRecord()",
    "GetAttRecord()",
    "GetAllRecord()",
    "GetRecordInfo()",
    "DownloadRecord()",
    "GetManagerInfo()",
    "GetWorkCode()",
    "GetWorkStatus()",
    "GetEmployee(employee_id=\"1\")",
    "GetRecord(start_time=\"2026-01-01 00:00:00\" end_time=\"2026-12-31 23:59:59\")",
    "GetEmployee(employee_id=\"100\")",
    "GetEmployee(user_id=\"100\")",
    "GetEmployee(id=\"100\")",
    "GetEmployeeCount()",
    "ListEmployee()",
    "QueryEmployee()",
    "GetFirstEmployee()",
]


def hdcp_call(host: str, command: str, timeout: float = 20.0) -> str:
    return hdcp_request(host, command, timeout=timeout, retries=8)


def main() -> None:
    host = sys.argv[1] if len(sys.argv) > 1 else IP
    print(f"Host {host}:{PORT}\n")

    for cmd in COMMANDS:
        for attempt in range(4):
            try:
                r = hdcp_call(host, cmd)
                if "Device Is Busy" in r:
                    time.sleep(2)
                    continue
                status = "OK" if 'result="success"' in r else "----"
                if "Not Support" in r or "fail" in r:
                    status = "fail"
                print(f"[{status}] {cmd}")
                if len(r) <= 400:
                    print(f"       {r}")
                else:
                    print(f"       {r[:400]}...")
                    print(f"       ({len(r)} chars)")
                break
            except socket.timeout:
                print(f"[TIMEOUT] {cmd} (attempt {attempt + 1})")
                time.sleep(1)
            except OSError as e:
                print(f"[ERR] {cmd}: {e}")
                break
        else:
            print(f"[BUSY] {cmd} — device busy after retries")
        time.sleep(0.3)

    print("\n--- Ports 9920 / 9924 (Push) ---")
    for p in (9920, 9924):
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.settimeout(3)
        try:
            s.connect((host, p))
            print(f"  {p} OPEN")
        except OSError:
            print(f"  {p} closed")
        finally:
            s.close()


if __name__ == "__main__":
    main()
