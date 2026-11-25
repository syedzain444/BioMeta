"""
Pull users + attendance if device speaks ZKTeco protocol (TCP 4370).
Install: pip install pyzk
Comm password: set on device under Comm -> PC Connect -> Comm Key (0 if disabled).
"""
from zk import ZK, const

IP = "172.16.40.63"
PORT = 4370
COMM_PASSWORD = 0  # change if device has comm key set


def main() -> None:
    zk = ZK(IP, port=PORT, timeout=10, password=COMM_PASSWORD, force_udp=False, ommit_ping=False)
    conn = None
    try:
        conn = zk.connect()
        print("Connected.")
        print("Firmware:", conn.get_firmware_version())
        print("Platform:", conn.get_platform())
        print("Serial:", conn.get_serialnumber())
        print("Device name:", conn.get_device_name())
        print()

        users = conn.get_users()
        print(f"Users: {len(users)}")
        for u in users[:20]:
            priv = "Admin" if u.privilege == const.USER_ADMIN else "User"
            print(f"  uid={u.uid} user_id={u.user_id} name={u.name!r} priv={priv}")
        if len(users) > 20:
            print(f"  ... and {len(users) - 20} more")

        att = conn.get_attendance()
        print(f"\nAttendance records: {len(att)}")
        for r in att[-10:]:
            print(f"  user={r.user_id} time={r.timestamp} status={r.status} punch={r.punch}")
        if len(att) > 10:
            print(f"  (showing last 10 of {len(att)})")
    except Exception as e:
        print("Failed:", e)
        print("If port 4370 is closed, device may use Push/Cloud only — configure server URL on device.")
    finally:
        if conn:
            conn.disconnect()


if __name__ == "__main__":
    main()
