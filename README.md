# MozaHud

Shows the **motor, MOSFET and MCU temperature** of a MOZA wheelbase (tested: R5) in the MSI Afterburner /
RivaTuner (RTSS) in-game overlay, and as a number in the system tray.

## Use

1. Run `MozaHud.exe`. It sits in the system tray; the icon shows the motor temperature
   (green, orange from 55 °C, red from 70 °C). Hover for all three values.
2. Right-click the icon → **Set up Afterburner…** (once, asks for admin rights). This
   - registers the Windows performance counters `MOZA Wheelbase\Motor temperature`, `…\MOSFET temperature`, `…\MCU temperature`;
   - adds them as sources to Afterburner's PerfCounter plugin (`Plugins\Monitoring\PerfCounter.cfg`;
     the original is kept as `PerfCounter.cfg.mozahud-backup`).
3. Restart MSI Afterburner. In Settings → Monitoring the graphs **MOZA motor temp**, **MOZA MOSFET temp** and
   **MOZA MCU temp** appear. Select one and tick **Show in On-Screen Display** (and set graph limits, alarms,
   OSD layout as for any other value). If they're missing, click **…** next to *Active hardware monitoring graphs*
   and tick `PerfCounter.dll`.
4. Optional: right-click → **Start with Windows**.

**Remove Afterburner setup…** in the tray menu undoes step 2.

When MozaHud isn't running (or can't read the base) the values read 0.

## Moza Pit House

Windows lets only one program at a time open the wheelbase's serial (COM) port. MozaHud opens it for a moment
every 2 seconds and closes it again. While Pit House has the port open, MozaHud can't read and the tray says so;
close Pit House while driving if you want the temperatures.

## How it works

- **Reading** – the base's USB serial port (vendor id `346E`, found via WMI) speaks the MOZA serial protocol
  as reverse-engineered by [boxflat](https://github.com/Lawstorant/boxflat) (`data/serial.yml`).
  Request `7E 03 2B 13 <id> 00 00 <checksum>` at 115200 baud, ids 4 = MCU, 5 = MOSFET, 6 = motor; checksum =
  (13 + sum of the bytes) % 256. The reply comes back in group `AB` from device `31` with a 2-byte big-endian value in
  1/100 °C. The base also sends debug text frames on the same port; those are skipped.
- **Afterburner** – values are published as integer performance counters in 1/100 °C; the PerfCounter sources divide by 100.

`MozaHud.exe --probe` (run from a shell) reads once and prints the values, the COM port and the setup state.

## Build

```powershell
.\build.ps1
```

Uses the C# compiler that ships with Windows (.NET Framework 4.x); no SDK needed.
