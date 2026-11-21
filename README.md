# MozaHud

Shows live values from a MOZA wheelbase (tested: R5) in the MSI Afterburner / RivaTuner (RTSS) in-game overlay:

| Afterburner graph | What it is |
|---|---|
| **MOZA motor temp**, **MOZA MOSFET temp**, **MOZA MCU temp** | Temperatures (°C) |
| **MOZA torque avg** | Current force-feedback torque, lightly smoothed (~150 ms) so kerbs and road texture don't make it jump (Nm) |
| **MOZA torque peak** | Highest smoothed torque of the last second (Nm) |
| **MOZA FFB load** | Torque peak as % of the base's maximum (100 % = at the limit) |
| **MOZA clipping** | Share of the last second the motor was at its maximum torque, from the raw samples (%) |

Afterburner reads its sources once per second by default, which makes the torque look slow. For a livelier overlay
set Settings → Monitoring → **Hardware polling period** to 250 ms (or lower).

The tray icon shows the motor temperature; hover for everything.

## Stage graphs and FFB gain advice

The MozaHud window (opens at start; tray icon double-click or **Open MozaHud**) records every AC Rally stage
automatically and shows, per stage:

- **Advice**: too strong (lower the gain), a bit strong, good, or headroom (raise the gain), with a suggested
  gain when it's known (e.g. *150 → 135*). Only time while driving (over 5 km/h) counts. Rules: clipping ≥ 5 % of
  the time → lower 10–30 %; 1.5–5 % → lower 5–10 %; otherwise, if even the strongest moments (99th percentile)
  stay under 80 % of the base's 5.5 Nm → raise; else good. At the game's maximum (200) "raise" means: raise the
  base's FFB strength in Pit House.
- **Torque graph**: smoothed torque, raw 100 ms peaks (faint band), the limit (dashed), red marks where it clipped,
  grey where the wheel was at the **soft lock**. The base's rotation setting is read from the base (e.g. 450°); the
  wheel position comes from the Windows joystick axis, which spans exactly that rotation (the game's steering
  value is identical). Within 3 % of full lock clipping is the soft lock pushing back and isn't counted.
- **Temperature graph**: motor and MOSFET.
- Scroll to zoom, drag to pan, double-click for the whole stage, hover for all values at that moment.

Stage name, car and FFB gain come from AC Rally StageSwitch's log ("Applied FFB gain 150 for Monte-Carlo · Sisteron
· Hyundai i20N Rally2"), otherwise from the game's save file.

Runs are stored as CSV in `%APPDATA%\MozaHud\stages` (10 points per second); the last 100 runs are kept, none older
than 60 days. Right-click a run to delete it. Runs with less than 20 s of driving aren't saved.

How a stage is detected in AC Rally's shared memory (ACC layout; AC Rally leaves the status, lap and
`iCurrentTime` fields at 0):
- **on a stage** while the track name (static block) is filled; the menu clears it;
- **finish**: the text stage clock (graphics `currentTime`, e.g. `02:58.143`) stops while the car still moves and
  the physics keeps ticking; points after that are dropped. Fallback: the graphics block stops updating (results
  screen) while physics ticks;
- **restart**: the stage clock jumps back;
- **pause**: the physics block stops updating.

## Use

1. Run `MozaHud.exe`. It sits in the system tray; the icon shows the motor temperature
   (green, orange from 55 °C, red from 70 °C).
2. Right-click the icon → **Set up Afterburner…** (once, asks for admin rights because Afterburner lives in
   Program Files). This adds the MOZA sources to Afterburner's AIDA64 plugin (`Plugins\Monitoring\AIDA64.cfg`;
   the original is kept as `AIDA64.cfg.mozahud-backup`).
3. Restart MSI Afterburner. In Settings → Monitoring click **…** next to *Active hardware monitoring graphs* and
   tick `AIDA64.dll`. The MOZA graphs appear at the bottom of the list: tick the ones you want, select one and tick
   **Show in On-Screen Display** (and set limits, alarms, OSD layout as for any other value).
4. Optional: right-click → **Start with Windows**.

**Remove Afterburner setup…** in the tray menu undoes step 2.
When MozaHud isn't running (or can't read the base) the values show as N/A.

## Moza Pit House

Windows lets only one program at a time open the wheelbase's serial (COM) port. MozaHud keeps it open to sample the
torque, so Pit House can't connect while MozaHud runs. Right-click → **Release COM port (for Pit House)** to let
Pit House in; untick it to continue. If Pit House already has the port, MozaHud says so and retries every 5 s.

## AIDA64

MozaHud hands its values to Afterburner through the shared memory block of Afterburner's AIDA64 plugin
(`AIDA64_SensorValues`, XML-like text). If AIDA64 itself runs, it owns that block and MozaHud can't publish (the tray
says so). The AIDA64 plugin only starts AIDA64 when it's installed.

## How it works

- **Reading** – the base's USB serial port (vendor id `346E`, found via WMI) speaks the MOZA serial protocol
  as reverse-engineered by [boxflat](https://github.com/Lawstorant/boxflat) (`data/serial.yml`).
  Request `7E 03 2B 13 <id> 00 00 <checksum>` at 115200 baud; checksum = (13 + sum of the bytes) % 256. The reply comes
  back in group `AB` from device `31` with a 2-byte big-endian value. The base also sends debug text frames on the same
  port; those are skipped.
  - ids 4 = MCU, 5 = MOSFET, 6 = motor temperature, in 1/100 °C (every 2 s);
  - id 7 = `MotGetState_MotTorque`, the live force-feedback torque (about 30×/s): 500 = no force, one step = 0.1 Nm.
    Found in the command database embedded in Pit House's `bin\FirmwareManager.exe` and confirmed by logging while
    driving (`tools/torque-log.ps1`): it opposes the steering and clips at 445 / 554 (±5.5 Nm, the R5's peak).
    That one step is 0.1 Nm (and not 0.1 % of the current limit) is still to be confirmed by driving with a lower
    max torque in Pit House.
- **Clipping** counts samples within 0.15 Nm of the maximum (5.5 Nm, `TorqueWindow.MaxNm`; change it for other bases).
- **Torque** is low-passed (time constant `TorqueWindow.SmoothMs` = 150 ms, on the signed value so a quick reversal
  passes through zero); the peak is the highest smoothed value of the last second. Values are republished every 100 ms.
- An earlier version used Windows performance counters and the PerfCounter plugin. After re-registering the counter
  category with more counters, readers matched counters to the wrong values; setup now removes that registration.

Log: `%APPDATA%\MozaHud\log.txt`. `MozaHud.exe --probe` (run from a shell, with the tray app closed) reads once and
prints the values, the COM port and the setup state. `tools\torque-log.ps1 -Seconds 120` logs the raw torque
candidates to CSV for research.

## Build

```powershell
.\build.ps1
```

Uses the C# compiler that ships with Windows (.NET Framework 4.x); no SDK needed.
