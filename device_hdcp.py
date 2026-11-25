"""Shared Hanwang HDCP client (TCP 9922) with retries for Busy / connection drops."""
from __future__ import annotations

import socket
import time

DEFAULT_HOST = "172.16.40.63"
DEFAULT_PORT = 9922

_TRANSIENT = (
    ConnectionResetError,
    ConnectionAbortedError,
    BrokenPipeError,
    TimeoutError,
    socket.timeout,
    OSError,
)


def read_hdcp_response(sock: socket.socket, timeout: float = 60.0) -> str:
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
                    return buf[: i + 1]
        try:
            chunk = sock.recv(65536)
        except socket.timeout:
            return buf
        if not chunk:
            if buf:
                return buf
            raise ConnectionResetError("Device closed connection with no data")
        buf += chunk.decode("ascii", errors="replace")
        if buf.startswith("Wait("):
            buf = ""


def hdcp_once(host: str, command: str, port: int, timeout: float) -> str:
    s = socket.create_connection((host, port), timeout=15)
    try:
        s.settimeout(timeout)
        s.sendall(command.encode("ascii"))
        return read_hdcp_response(s, timeout=timeout)
    finally:
        try:
            s.close()
        except OSError:
            pass


def hdcp_call(
    host: str,
    command: str,
    *,
    port: int = DEFAULT_PORT,
    timeout: float = 60.0,
    retries: int = 12,
    busy_pause: float = 2.5,
    error_pause: float = 1.0,
) -> str:
    last = ""
    last_exc: Exception | None = None
    for attempt in range(retries):
        try:
            last = hdcp_once(host, command, port, timeout)
            if "Device Is Busy" in last:
                time.sleep(busy_pause * (1 + attempt * 0.15))
                continue
            return last
        except _TRANSIENT as ex:
            last_exc = ex
            wait = min(error_pause * (2**attempt), 45.0)
            time.sleep(wait)
    if last_exc and not last:
        raise last_exc
    return last
