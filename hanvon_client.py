"""Minimal Hanwang/Hanvon HDCP client (port 9922). Based on engwan/hanvon."""
import re
import socket
import sys
import time

IP = "172.16.40.63"
PORT = 9922


class HanvonCrypto:
    def __init__(self, password: str) -> None:
        if not re.fullmatch(r"\d{1,8}", password):
            raise ValueError("password must be 1-8 digits")
        self._key = [(ord(password[i]) if i < len(password) else 0) + (2**i >> 1) for i in range(8)]

    def convert(self, message: str, offset: int = 0) -> bytes:
        out = []
        for pos, ch in enumerate(message):
            out.append(ord(ch) ^ self._key[(pos + offset) % 8])
        return bytes(out)


class HanvonClient:
    def __init__(self, host: str, port: int = 9922, password: str | None = None, timeout: float = 60) -> None:
        self._sock = socket.create_connection((host, port), timeout=timeout)
        self._sock.settimeout(timeout)
        self._crypto = HanvonCrypto(password) if password else None

    def close(self) -> None:
        self._sock.close()

    def send(self, message: str) -> str:
        payload = message.encode("ascii")
        if self._crypto:
            payload = self._crypto.convert(message)
        self._sock.sendall(payload)
        return self._read_reply()

    def _read_reply(self) -> str:
        reply = ""
        offset = 0
        while True:
            chunk = self._sock.recv(65536)
            if not chunk:
                break
            if self._crypto:
                dec = bytes(
                    b ^ self._crypto._key[(offset + i) % 8]
                    for i, b in enumerate(chunk)
                )
                offset += len(chunk)
                reply += dec.decode("ascii", errors="replace")
            else:
                reply += chunk.decode("ascii", errors="replace")
            if reply.endswith(")"):
                if reply.startswith("Wait("):
                    reply = ""
                    continue
                break
        return reply

    def call(self, command: str, retries: int = 5, pause: float = 2.0) -> str:
        last = ""
        for _ in range(retries):
            last = self.send(command)
            if "Device Is Busy" not in last:
                return last
            time.sleep(pause)
        return last


def try_connect(host: str, password: str | None) -> None:
    label = password or "(no comm key)"
    print(f"\n=== Comm key: {label} ===")
    c = HanvonClient(host, password=password, timeout=15)
    try:
        for cmd in (
            "GetDeviceInfo()",
            "GetEmployeeInfo()",
            "GetDeviceRecord()",
        ):
            try:
                r = c.call(cmd)
                print(f"{cmd} -> {r[:500]}")
                if len(r) > 500:
                    print(f"  ... ({len(r)} chars total)")
            except socket.timeout:
                print(f"{cmd} -> TIMEOUT")
            except OSError as e:
                print(f"{cmd} -> {e}")
    finally:
        c.close()


def main() -> None:
    host = sys.argv[1] if len(sys.argv) > 1 else IP
    # Admin PINs 10492/1990 are for MENU on device; network comm key is usually separate/OFF.
    passwords: list[str | None] = [None]
    for pw in passwords:
        try:
            try_connect(host, pw)
        except Exception as e:
            print(f"Failed ({pw}): {e}")


if __name__ == "__main__":
    main()
