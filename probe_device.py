"""
Probe NEDO CL-2020 / Hanwang face terminal at 172.40.16.63
Run on a PC on the same LAN as the device: python probe_device.py
"""
import socket
import sys

IP = "172.16.40.63"
PORTS = [80, 443, 4370, 8080, 8000, 9920, 9922, 9924, 5005, 8088, 22]

# ZKTeco standalone: CMD_CONNECT payload (first packet)
ZK_CONNECT = bytes.fromhex("5050827d130000000000000000000000000000000000000000")


def try_port_on(host: str, port: int, timeout: float = 3.0) -> bool:
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(timeout)
    try:
        s.connect((host, port))
        return True
    except OSError:
        return False
    finally:
        s.close()


def probe_zkteco_4370(host: str, timeout: float = 5.0) -> str:
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(timeout)
    try:
        s.connect((host, 4370))
        s.send(ZK_CONNECT)
        data = s.recv(1024)
        if data and data[:2] == b"\x50\x50":
            return f"ZKTeco-like reply ({len(data)} bytes): {data[:32].hex()}..."
        return f"4370 open, non-ZK response: {data[:64]!r}" if data else "4370 open, no reply"
    except OSError as e:
        return f"4370 failed: {e}"
    finally:
        s.close()


def probe_hanwang_9922(host: str, timeout: float = 5.0) -> str:
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(timeout)
    try:
        s.connect((host, 9922))
        s.send(b"help\r\n")
        data = s.recv(4096)
        return f"9922 text reply: {data[:200]!r}" if data else "9922 open, no reply to help"
    except OSError as e:
        return f"9922 failed: {e}"
    finally:
        s.close()


def main() -> None:
    target = sys.argv[1] if len(sys.argv) > 1 else IP
    print(f"Target: {target}\n")

    print("Open TCP ports:")
    open_ports = []
    for p in PORTS:
        if try_port_on(target, p):
            open_ports.append(p)
            print(f"  {p}  OPEN")
    if not open_ports:
        print("  (none of common ports — check firewall/VLAN or device Comm settings)")

    print()
    if 4370 in open_ports:
        print("4370 (ZKTeco standalone):", probe_zkteco_4370(target))
        print("  -> Try: pip install pyzk  then connect with password=0 or comm key from device menu")
    if 9922 in open_ports:
        print("9922 (Hanwang HDCP):", probe_hanwang_9922(target))
        print("  -> Needs Hanwang HwDevComm.dll SDK or compatible library")

    if 80 in open_ports or 8080 in open_ports:
        for p in (80, 8080):
            if p in open_ports:
                print(f"\nTry browser: http://{target}:{p}/")

    print("\n--- On-device ---")
    print("MENU admin PIN: 10492 and/or 1990 (device screen only, not Python comm key)")
    print("MENU -> Comm / Ethernet -> note IP; Comm Key should be OFF for scripts on 9922")


if __name__ == "__main__":
    main()
