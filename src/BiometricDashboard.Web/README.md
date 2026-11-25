# Biometric Live Dashboard (.NET 8 MVC)

## Run

```powershell
cd "d:\Zain BTK Projects\Biometric\src\BiometricDashboard.Web"
dotnet run
```

Open: https://localhost:7xxx or http://localhost:5xxx (see console)

**Primary database only:** `ConnectionStrings:GateEntryOracle` (Gate_Entry_Surveillance · `entrygate_surv` @ `172.16.10.60:1521` SID `orcl`). No Heritage DB.

Device IPs are stored in **`BIO_DEVICE`** (setup / Admin → Devices). `BiometricDevice` in appsettings is only poll interval + `GetRecord` date range.

Run in SQL Developer (F5): `002_gate_entry_portal_schema.sql`, then `006_biometric_devices.sql`.

Default login: **Employee ID `ADMIN`** / **Password `Admin@123`** (change after first sign-in).

## Architecture

- **Core** — entities, repository interfaces, `DeviceOptions`
- **Infrastructure** — `HanwangDeviceClient` (HDCP 9922), `InMemoryAttendanceRepository`, `DeviceSyncBackgroundService` (poll every 2s)
- **Web** — MVC dashboard, SignalR `AttendanceHub`, live grid updates on new punch

## Live behaviour

Background service polls `GetRecord` from the terminal. New punches are stored via repository (dedupe by employee + time) and pushed to the browser with SignalR (`NewPunches`).
