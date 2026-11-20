# FrankEnPoller

A small Windows system-tray app that shows live power flow for a home with **Enphase** solar and a **FranklinWH** battery system, a two-vendor setup stitched together like Frankenstein's monster, hence the name.

> **Unofficial.** FrankEnPoller is not affiliated with, endorsed by, or supported by Enphase Energy or FranklinWH Energy Storage. It reads the FranklinWH aGate through FranklinWH's undocumented cloud API, which can change or stop working at any time.

## What it shows

- **Tray icon:** battery charge % while the battery is charging or discharging. When the battery is idle or full, it shows grid power in kW instead. The background is green when exporting more than 100 W, amber when importing more than 100 W, and blue in between.
- **Status window** (left-click the icon):
  - **Solar:** current output, energy today, and today's peak.
  - **Home:** current load (measured by the aGate) and energy used today.
  - **Grid:** import or export.
  - **Battery:** charge % and charge/discharge power.
  - **Backup:** estimated time the batteries could run the house in an outage. It is calculated at current load, at the 24-hour average load, and at the peak 5-minute load from the last 24 hours. It assumes no solar, so it is a worst case.
- **CSV log:** one file per month, with a reading every 15 seconds.

## Requirements

- Windows 10 or 11 with the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- An Enphase IQ Gateway reachable on your LAN, plus your Enlighten login and the gateway's serial number. The app uses these to get the gateway's access token. CT meters give real-time readings; without them, solar comes from the microinverters' reports (every few minutes) and grid power from the aGate.
- A FranklinWH aGate and your FranklinWH app login. Optional: without it you get Enphase data only.

## Setup

1. Build it (below), or download a release if one is available.
2. Run `FrankEnPoller.exe`. The Settings window opens on first run.
3. Enter the gateway's IP address and serial number, and your Enlighten and FranklinWH logins.
4. Set your battery capacity, which the aGate doesn't report, and the lowest charge level usable in an outage.
5. Optionally, right-click the tray icon and turn on **Start with Windows**.

Settings, logs and CSV files are stored in `%LOCALAPPDATA%\FrankEnPoller`. Passwords are encrypted with Windows DPAPI, so only your Windows account can decrypt them.

## Building

Requires the .NET 8 SDK.

```
dotnet publish -c Release -o publish
```

This produces a single `publish\FrankEnPoller.exe`.

## How the numbers are worked out

- **Enphase:** values come from the IQ Gateway's local `production.json` CT meters, or from its microinverter totals on gateways without a production CT. The gateway's own "today" figures are unreliable on current firmware, so today's solar is the lifetime production counter minus its value before sunrise, taken from the CSV log. If the log doesn't reach back that far, the app uses the aGate's daily solar total.
- **FranklinWH aGate:** the cloud is polled every 30 seconds. Battery power is positive when discharging and negative when charging. The aGate can be read over local Modbus TCP instead, but FranklinWH doesn't enable Modbus for homeowners.
- **Home load:** taken from the aGate. The aGate sits downstream of the Enphase consumption CTs, so Enphase's consumption figure includes battery charging and misses whatever the battery supplies.

## Diagnostics

Right-click the tray icon, then choose **aGate diagnostics…**. This writes a report of the raw FranklinWH data, which is useful if field meanings ever change.

## License

[GPL-3.0](LICENSE). You're free to use, modify and share FrankEnPoller. If you distribute a modified version, it must also be released under the GPL with its source code.
