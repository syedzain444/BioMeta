"""
Hanwang / FaceID push server (device connects TO your PC).

Device flow (port 9920):
  PostRecord(sn=...)  -> server: Return(result="success" postphoto="false")
  Record(...)         -> server: Return(result="success")
  GetRequest(sn=...)  -> server sends a command e.g. GetRecord(...) then device may reply

Run: python push_listener.py --bind 0.0.0.0 --ports 9920 9924
"""
from __future__ import annotations

import argparse
import json
import re
import socket
import threading
from datetime import datetime, timezone
from pathlib import Path

LOG_DIR = Path(__file__).resolve().parent / "push_logs"
RECORDS_DIR = Path(__file__).resolve().parent / "push_logs" / "records"
ENROLLED_DIR = Path(__file__).resolve().parent / "output"
ENROLLED_FILE = ENROLLED_DIR / "enrolled_from_push.jsonl"
DEFAULT_PORTS = (9920, 9924)

# Sending GetRecord on GetRequest keeps the terminal stuck on “Network Operating”.
DEFAULT_PULL_CMD = ""


def decode_payload(data: bytes) -> str:
    for enc in ("gbk", "utf-8", "latin-1"):
        try:
            return data.decode(enc)
        except UnicodeDecodeError:
            continue
    return data.decode("latin-1", errors="replace")


def encode_out(text: str) -> bytes:
    if not text.endswith("\r\n"):
        text += "\r\n"
    return text.encode("gbk", errors="replace")


def read_one_message(sock: socket.socket, timeout: float) -> str | None:
    """Read bytes until one top-level Command(...) message is complete."""
    sock.settimeout(timeout)
    buf = ""
    while True:
        depth = 0
        started = False
        for i, ch in enumerate(buf):
            if ch == "(":
                depth += 1
                started = True
            elif ch == ")":
                depth -= 1
                if started and depth == 0:
                    return buf[: i + 1].strip()
        try:
            chunk = sock.recv(8192)
        except socket.timeout:
            return buf.strip() if buf.strip() else None
        if not chunk:
            return buf.strip() if buf.strip() else None
        buf += decode_payload(chunk)


def log_event(port: int, peer: str, kind: str, body: str, extra: dict | None = None) -> None:
    LOG_DIR.mkdir(exist_ok=True)
    line = {
        "ts": datetime.now(timezone.utc).isoformat(),
        "port": port,
        "peer": peer,
        "kind": kind,
        "body": body[:50000],
    }
    if extra:
        line.update(extra)
    path = LOG_DIR / f"push_{datetime.now().strftime('%Y-%m-%d')}.jsonl"
    with path.open("a", encoding="utf-8") as f:
        f.write(json.dumps(line, ensure_ascii=False) + "\n")
    preview = body.replace("\r", " ").replace("\n", " ")[:200]
    print(f"[{port}] {peer} {kind}: {preview}")


def parse_record_fields(body: str) -> dict[str, str]:
    return {m.group(1): m.group(2) for m in re.finditer(r'(\w+)="([^"]*)"', body)}


def client_loop(
    sock: socket.socket,
    addr: tuple[str, int],
    port: int,
    post_photo: bool,
    pull_cmd: str,
) -> None:
    peer = f"{addr[0]}:{addr[1]}"
    print(f"[{port}] connected {peer}")
    try:
        while True:
            msg = read_one_message(sock, timeout=90.0)
            if not msg:
                break
            if msg.startswith("Quit"):
                log_event(port, peer, "Quit", msg)
                break

            kind_m = re.match(r"^(\w+)", msg)
            kind = kind_m.group(1) if kind_m else "data"

            if msg.startswith("Record"):
                fields = parse_record_fields(msg)
                log_event(port, peer, "Record", msg, extra={"parsed": fields})
                RECORDS_DIR.mkdir(parents=True, exist_ok=True)
                rec_file = RECORDS_DIR / "attendance.jsonl"
                with rec_file.open("a", encoding="utf-8") as f:
                    f.write(json.dumps({"ts": datetime.now(timezone.utc).isoformat(), **fields}, ensure_ascii=False) + "\n")
                sock.sendall(encode_out('Return(result="success")'))
                continue

            log_event(port, peer, kind, msg)

            if msg.startswith("PostRecord"):
                photo = "true" if post_photo else "false"
                sock.sendall(
                    encode_out(f'Return(result="success" postphoto="{photo}")')
                )
                continue

            if msg.startswith("PostEmployee"):
                sock.sendall(encode_out('Return(result="success")'))
                continue

            if msg.startswith("Employee"):
                fields = parse_record_fields(msg.split("face_data=")[0])
                if fields.get("id") or fields.get("name"):
                    ENROLLED_DIR.mkdir(parents=True, exist_ok=True)
                    row = {"ts": datetime.now(timezone.utc).isoformat(), **fields}
                    with ENROLLED_FILE.open("a", encoding="utf-8") as f:
                        f.write(json.dumps(row, ensure_ascii=False) + "\n")
                    print(f"[{port}] ENROLLED id={fields.get('id')} name={fields.get('name')}")
                log_event(port, peer, "Employee", msg, extra={"parsed": fields})
                sock.sendall(encode_out('Return(result="success")'))
                continue

            if msg.startswith("GetRequest"):
                if pull_cmd.strip():
                    print(f"[{port}] {peer} -> sending pull: {pull_cmd}")
                    sock.sendall(encode_out(pull_cmd))
                else:
                    sock.sendall(encode_out('Return(result="success")'))
                continue

            sock.sendall(encode_out('Return(result="success")'))

    except OSError as e:
        print(f"[{port}] {peer} closed: {e}")
    finally:
        sock.close()
        print(f"[{port}] disconnected {peer}")


def listen_port(bind_ip: str, port: int, post_photo: bool, pull_cmd: str) -> None:
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((bind_ip, port))
    srv.listen(16)
    print(f"Listening on {bind_ip}:{port}")
    while True:
        client, addr = srv.accept()
        t = threading.Thread(
            target=client_loop,
            args=(client, addr, port, post_photo, pull_cmd),
            daemon=True,
        )
        t.start()


def main() -> None:
    p = argparse.ArgumentParser(description="Hanwang FaceID push listener")
    p.add_argument("--bind", default="0.0.0.0")
    p.add_argument("--ports", nargs="+", type=int, default=list(DEFAULT_PORTS))
    p.add_argument("--post-photo", action="store_true")
    p.add_argument(
        "--pull-on-request",
        action="store_true",
        help="Legacy: reply to GetRequest with GetRecord (causes Network Operating on device)",
    )
    p.add_argument(
        "--pull-cmd",
        default='GetRecord(start_time="2020-01-01 00:00:00" end_time="2030-12-31 23:59:59")',
        help="Used only with --pull-on-request",
    )
    args = p.parse_args()
    pull_cmd = args.pull_cmd if args.pull_on_request else DEFAULT_PULL_CMD

    print("Logs ->", LOG_DIR)
    print("Attendance ->", RECORDS_DIR / "attendance.jsonl")
    print("On GetRequest, send:", pull_cmd or 'Return(result="success")')
    print("Device: Push ON, Record Data ON, server IP = this PC\n")

    threads = []
    for port in args.ports:
        th = threading.Thread(
            target=listen_port,
            args=(args.bind, port, args.post_photo, pull_cmd),
            daemon=True,
        )
        th.start()
        threads.append(th)

    try:
        for th in threads:
            th.join()
    except KeyboardInterrupt:
        print("\nStopped.")


if __name__ == "__main__":
    main()
