"""
Copy enrolled users (with face template) from one Hanwang device to another.

Same flow as portal + push_listener:
  1) GetEmployee on SOURCE (9922) — includes face_data
  2) PC push server on 9920/9924 queues Employee(...)
  3) DownloadEmployee() on TARGET (9922) — device pulls user from PC

IMPORTANT:
  - Stop BiometricDashboard.Web OR push_listener.py — only ONE process can bind 9920/9924.
  - Target: Push ON, User Data ON, server IP = this PC, ports 9920 & 9924.
  - Push TCP client IP may differ from 9922 IP (e.g. target HDCP .202 but push from .203):
    use   --push-peer 10.100.17.203

Examples:
  python device_copy_users.py --source 10.100.17.191 --target 10.100.17.202 --push-peer 10.100.17.203 --ids 4140956148266
"""
from __future__ import annotations

import argparse
import re
import socket
import sys
import threading
import time
from pathlib import Path

from device_hdcp import hdcp_call

PUSH_PORTS = (9920, 9924)

_lock = threading.Lock()
_pending_cmd: str | None = None
_allowed_peers: set[str] = set()
_target_hdcp: str = ""
_ack = threading.Event()
_delivery_peer: str = ""
_seen_push_peers: set[str] = set()


def normalize_peer(ip: str) -> str:
    return ip.strip()


def peer_allowed(ip: str) -> bool:
    ip = normalize_peer(ip)
    if not _allowed_peers:
        return ip == _target_hdcp
    return ip in _allowed_peers or ip == _target_hdcp


def parse_fields(block: str) -> dict[str, str]:
    return {m.group(1): m.group(2) for m in re.finditer(r'(\w+)="([^"]*)"', block)}


def encode_out(text: str) -> bytes:
    if not text.endswith("\r\n"):
        text += "\r\n"
    return text.encode("gbk", errors="replace")


def read_one_message(sock: socket.socket, timeout: float = 90.0) -> str | None:
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
            chunk = sock.recv(65536)
        except socket.timeout:
            return buf.strip() if buf.strip() else None
        if not chunk:
            return buf.strip() if buf.strip() else None
        buf += chunk.decode("gbk", errors="replace")


def push_client_loop(sock: socket.socket, addr: tuple[str, int]) -> None:
    global _pending_cmd, _delivery_peer
    peer = f"{addr[0]}:{addr[1]}"
    peer_ip = normalize_peer(addr[0])
    try:
        while True:
            msg = read_one_message(sock, timeout=120.0)
            if not msg:
                break
            if msg.startswith("Quit"):
                break
            if msg.startswith("Return("):
                if peer_allowed(peer_ip) and _delivery_peer == peer_ip:
                    _ack.set()
                sock.sendall(encode_out('Return(result="success")'))
                continue
            if msg.startswith("GetRequest"):
                note_push_peer(peer_ip)
                if not peer_allowed(peer_ip):
                    print(f"  [{peer}] GetRequest ignored (not target; allowed={sorted(_allowed_peers)})")
                    sock.sendall(encode_out('Return(result="success")'))
                    continue
                cmd = None
                with _lock:
                    cmd = _pending_cmd
                    if cmd:
                        _pending_cmd = None
                        _delivery_peer = peer_ip
                if cmd:
                    print(f"  [{peer}] -> Employee payload ({len(cmd)} bytes)")
                    sock.sendall(encode_out(cmd))
                else:
                    print(f"  [{peer}] GetRequest (target) but no payload queued")
                    sock.sendall(encode_out('Return(result="success")'))
                continue
            if msg.startswith("Record(") or msg.startswith("PostRecord"):
                sock.sendall(encode_out('Return(result="success")'))
                continue
            sock.sendall(encode_out('Return(result="success")'))
    except OSError as ex:
        print(f"  [{peer}] closed: {ex}")
    finally:
        sock.close()


def start_push_servers(bind: str = "0.0.0.0") -> list[socket.socket]:
    servers: list[socket.socket] = []
    for port in PUSH_PORTS:
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind((bind, port))
        srv.listen(12)

        def accept_loop(srv_sock: socket.socket, listen_port: int) -> None:
            while True:
                client, addr = srv_sock.accept()
                print(f"  [push:{listen_port}] TCP from {addr[0]}:{addr[1]}")
                threading.Thread(
                    target=push_client_loop,
                    args=(client, addr),
                    daemon=True,
                ).start()

        threading.Thread(target=accept_loop, args=(srv, port), daemon=True).start()
        servers.append(srv)
        print(f"Push server listening {bind}:{port}")
    return servers


def pull_employee_raw(host: str, emp_id: str) -> str | None:
    raw = hdcp_call(host, f'GetEmployee(employee_id="{emp_id}")', timeout=60, retries=8)
    if not raw or 'result="success"' not in raw or "unknown id" in raw.lower():
        return None
    return raw


def build_employee_command(raw: str, emp_id: str) -> str | None:
    head = raw.split("face_data=", 1)[0]
    head_line = " ".join(head.splitlines())
    fields = parse_fields(head_line)
    if not fields.get("id"):
        fields["id"] = emp_id
    face_chunks = re.findall(r'face_data="([^"]*)"', raw, re.S)
    if not face_chunks:
        print(f"  WARN {emp_id}: no face_data on source — copy may fail.")
    name = fields.get("name", "")
    auth = fields.get("authority", "0X11")
    card = fields.get("card_num", "0Xffffffff")
    check = fields.get("check_type", "face")
    cmd_head = (
        f'Employee(id="{fields["id"]}" name="{name}" authority="{auth}" card_num="{card}" '
        f'calid="0" check_type="{check}" opendoor_type="{check}" password="" type="M2000" alg_edition="3.1"'
    )
    if face_chunks:
        return cmd_head + '\r\nface_data="' + "".join(face_chunks) + '")'
    dept = fields.get("department", "")
    if dept:
        return cmd_head + f' department="{dept}")'
    return cmd_head + ")"


def guess_pc_ip() -> str:
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except OSError:
        return "?"


def local_ip_towards(host: str, port: int = 9922) -> str:
    """NIC used to reach the device — must match Server IP on the terminal."""
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.settimeout(3.0)
        s.connect((host.strip(), port))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except OSError:
        return guess_pc_ip()


def subnet24(ip: str) -> str:
    parts = ip.split(".")
    return ".".join(parts[:3]) if len(parts) == 4 else ip


_server_ip_hint = ""


def preflight_hdcp(source: str, target: str) -> None:
    print("Preflight (9922 HDCP):")
    for label, host in (("source", source), ("target", target)):
        try:
            raw = hdcp_call(host, "GetDeviceInfo()", timeout=18, retries=4)
            if raw and 'result="success"' in raw:
                m = re.search(r'type="([^"]+)"', raw)
                typ = m.group(1) if m else "?"
                print(f"  {label} {host}: OK ({typ})")
            else:
                print(f"  {label} {host}: bad response: {(raw or '')[:120]}")
        except OSError as ex:
            print(f"  {label} {host}: cannot connect — {ex}")

    try:
        probe = hdcp_call(target, "DownloadEmployee()", timeout=15, retries=2)
        if probe and "Command Not Support" in probe:
            print(
                f"  target {target}: DownloadEmployee() not supported on 9922 (typical M2000A).\n"
                f"    While this script waits, on the DEVICE use: Communication / Network → Download or Sync employee.\n"
                f"    Push must still reach this PC on 9920/9924 — same subnet as the gate is strongly recommended."
            )
        elif probe and 'result="success"' in probe:
            print(f"  target {target}: DownloadEmployee() HDCP OK — script can trigger pull remotely.")
        else:
            print(f"  target {target}: DownloadEmployee() -> {(probe or '')[:100]}")
    except OSError as ex:
        print(f"  target {target}: DownloadEmployee probe failed — {ex}")


def trigger_download(target: str) -> None:
    for trigger in ("ExportEmployee()", "DownloadEmployee()", "SyncEmployee()"):
        try:
            hdcp_call(target, trigger, timeout=20, retries=3)
        except OSError as ex:
            print(f"  trigger {trigger}: {ex}")
        time.sleep(0.35)


def verify_on_target(target: str, emp_id: str) -> bool:
    raw = hdcp_call(target, f'GetEmployee(employee_id="{emp_id}")', timeout=35, retries=4)
    return bool(raw and 'result="success"' in raw and "unknown id" not in raw.lower())


def reset_pending() -> None:
    global _pending_cmd, _delivery_peer
    with _lock:
        _pending_cmd = None
        _delivery_peer = ""
    _ack.clear()


def note_push_peer(ip: str) -> None:
    global _seen_push_peers
    _seen_push_peers.add(normalize_peer(ip))


def copy_one(source: str, target: str, emp_id: str, wait_sec: float) -> bool:
    global _pending_cmd
    print(f"\n=== {emp_id} ===")
    raw = pull_employee_raw(source, emp_id)
    if not raw:
        print("  FAIL: not on source")
        return False
    cmd = build_employee_command(raw, emp_id)
    if not cmd:
        print("  FAIL: could not build Employee command")
        return False

    reset_pending()
    with _lock:
        _pending_cmd = cmd

    print(
        f"  Waiting on push 9920/9924 from {sorted(_allowed_peers)} — "
        f"on target menu run Download/Sync employee if nothing happens ({int(wait_sec)}s max)."
    )
    trigger_download(target)

    deadline = time.time() + wait_sec
    last_trig = 0.0
    while time.time() < deadline:
        if verify_on_target(target, emp_id):
            print("  OK: verified on target")
            reset_pending()
            return True
        if _ack.is_set():
            time.sleep(1.2)
            if verify_on_target(target, emp_id):
                print("  OK: verified after push ack")
                reset_pending()
                return True
        now = time.time()
        if now - last_trig >= 8.0:
            trigger_download(target)
            last_trig = now
        time.sleep(1.5)

    print("  FAIL: timeout")
    print(f"         HDCP target {_target_hdcp}; allowed push peers: {sorted(_allowed_peers)}")
    allowed_seen = _seen_push_peers & _allowed_peers
    if not allowed_seen:
        print(
            f"         Target never opened push to this PC. Server IP on gate = {_server_ip_hint or local_ip_towards(_target_hdcp)} "
            f"(ports 9920/9924, Push + User Data ON)."
        )
        if subnet24(_server_ip_hint or "") != subnet24(_target_hdcp):
            print(
                f"         Cross-subnet: gate {_target_hdcp} is {subnet24(_target_hdcp)}.x but PC is {subnet24(_server_ip_hint or guess_pc_ip())}.x — "
                f"if no '[push:9920] TCP from ...' lines appeared, routing device→PC is likely blocked. "
                f"Fix: put this PC on {subnet24(_target_hdcp)}.x or ask IT to open TCP 9920/9924 to {_server_ip_hint}."
            )
        elif not _seen_push_peers:
            print(
                "         No TCP at all on 9920/9924 — wrong Server IP on device, firewall, or manual Sync not pressed during the wait."
            )
        if _seen_push_peers - _allowed_peers:
            print(f"         Other gates hit this listener: {sorted(_seen_push_peers - _allowed_peers)}")
    else:
        print(f"         Target did connect ({sorted(allowed_seen)}) but user not verified on {_target_hdcp}.")
    print("         If a new IP appears in logs, add: --push-peer <ip>")
    reset_pending()
    return False


def load_ids(args: argparse.Namespace) -> list[str]:
    ids: list[str] = list(args.ids or [])
    if args.ids_file:
        text = Path(args.ids_file).read_text(encoding="utf-8")
        for line in text.splitlines():
            line = line.strip()
            if line and not line.startswith("#"):
                ids.append(line.split()[0])
    return list(dict.fromkeys(ids))


def main() -> None:
    global _target_hdcp, _allowed_peers, _seen_push_peers, _server_ip_hint
    p = argparse.ArgumentParser(description="Copy users source -> target (Hanwang push)")
    p.add_argument("--source", required=True, help="Source device IP (9922)")
    p.add_argument("--target", required=True, help="Target device IP (9922)")
    p.add_argument(
        "--push-peer",
        action="append",
        dest="push_peers",
        help="IP that appears on push port (if not same as --target). Repeatable.",
    )
    p.add_argument("--bind", default="0.0.0.0", help="Push listen bind")
    p.add_argument("--ids", nargs="*", help="Employee IDs")
    p.add_argument("--ids-file", help="Text file with one ID per line")
    p.add_argument("--wait", type=float, default=90.0, help="Seconds to wait per user on target")
    p.add_argument("--delay", type=float, default=0.5, help="Delay between users")
    p.add_argument(
        "--server-ip",
        help="IP to configure on target as push server (default: auto NIC toward --target)",
    )
    args = p.parse_args()

    _target_hdcp = normalize_peer(args.target)
    _seen_push_peers = set()
    _allowed_peers = {normalize_peer(x) for x in (args.push_peers or [])}
    _allowed_peers.add(_target_hdcp)

    ids = load_ids(args)
    if not ids:
        print("No IDs — use --ids or --ids-file")
        sys.exit(1)

    try:
        start_push_servers(args.bind)
    except OSError as ex:
        print("Could not bind push ports (9920/9924):", ex)
        print("Stop BiometricDashboard.Web or another push_listener, then retry.")
        sys.exit(2)

    pc_default = guess_pc_ip()
    pc_toward = local_ip_towards(_target_hdcp)
    _server_ip_hint = normalize_peer(args.server_ip) if args.server_ip else pc_toward

    print(f"HDCP target: {_target_hdcp}")
    print(f"Serving Employee payloads only to push peers: {sorted(_allowed_peers)}")
    print(f"Configure TARGET push Server IP = {_server_ip_hint}  (ports 9920 / 9924)")
    if pc_default != _server_ip_hint:
        print(
            f"  Note: default route NIC is {pc_default} — wrong for 10.100.x devices if not {_server_ip_hint}."
        )
    if subnet24(_server_ip_hint) != subnet24(_target_hdcp):
        print(
            f"  WARNING: server IP {_server_ip_hint} is not same /24 as target {_target_hdcp} — "
            f"fix device menu or run: ipconfig and pick the 10.100.17.x address of this PC, then --server-ip that"
        )
    print("Other devices on LAN will get Return(success) without stealing the queue.\n")
    preflight_hdcp(args.source, _target_hdcp)
    print()

    ok = 0
    for emp_id in ids:
        if copy_one(args.source, args.target, emp_id, args.wait):
            ok += 1
        time.sleep(args.delay)

    print(f"\nDone: {ok}/{len(ids)} copied to {args.target}")


if __name__ == "__main__":
    main()