# MozaHud

Shows live values from a MOZA wheelbase in the MSI Afterburner / RivaTuner (RTSS) in-game overlay, and records
AC Rally stages with FFB gain advice. Tested on an R5; other MOZA bases are recognised (see *Which base*).

## Which base

MozaHud asks the connected base who it is, the way Pit House does on connect (read-only queries):
product type (group 7, e.g. `R5 Black # MOT-1-V03`), hardware version (group 8, `RS21-D05-HW BM-CU-V10`),
firmware (group 4), the **max torque** setting (`[40,18]`, %) and the rotation (`[40,1]`, half degrees).
The model is matched with the patterns from Pit House's `bin\config\device_config.json` (Pro/Ultra variants by
hardware version). The base doesn't report its rated torque, so MozaHud has a table: the number in the name is the Nm
(R9 9, R12 12, R16 16, R21 21, R25 25, R27 27; unknown future models the same way), except R3 = 3.9 and the
R5 family = 5.5. The torque limit used for clipping and the graphs = rated × max torque setting.

**Ceiling check**: when a base clips, its torque reading sticks at one value (R5: 5.4 Nm, hit far more often than the
value below it). If such a plateau is more than 0.3 Nm away from the expected limit, MozaHud uses the measured value
from then on (remembered per model and max torque setting in `settings.ini`, logged). This covers uncertain ratings
and the open question whether the reading scales with the max torque setting.

In Afterburner the torque graphs' limit is set to 6 Nm; for a bigger base raise *Graph limits* in Afterburner's
monitoring settings.

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

- **Advice** against **Allowed clipping** (top right of the window, one decimal, default 2.5 %; saved in
  `%APPDATA%\MozaHud\settings.ini`). Only time while driving (over 5 km/h) counts.
  - Clipping above the allowed share → *a bit strong* (less than 1.5 points over) or *too strong*, with a suggested
    gain (e.g. *150 → 135*): the step grows with the excess (0.6 points over → −5 %, 2.9 over → −10 %, max −30 %).
  - At or below it → *good* (green), with the found clipping explained against the setting, and at most a small hint:
    *room to spare* (clipping under half the allowed share and the strongest moments under 80 % of 5.5 Nm, with how
    much more gain would fit; at the game's maximum of 200: raise the base's strength in Pit House) or *close to your
    limit* (within 0.5 points, −5 % if it feels harsh).
- **Torque graph**: smoothed torque (line) and the raw 100 ms peaks (orange band). Every 100 ms in which a raw peak
  hit the limit gets a **red bar** up to that peak, exactly that wide; at the **soft lock** the bar is **grey** (same
  colours as the marks along the top). The line is red/grey only between two such moments in a row: it's smoothed, so
  at a short clip it stays below the limit while the raw peak touches it. The limit is dashed. The stage list and header show the rally's flag and the car's logo (drawing code copied from StageSwitch's
  `RallyStyle.cs` / `BrandMark.cs`). The base's rotation setting is read from the base (e.g. 450°); the
  wheel position comes from the Windows joystick axis, which spans exactly that rotation (the game's steering
  value is identical). Within 3 % of full lock clipping is the soft lock pushing back and isn't counted.
- **Key figures** (hover the ⓘ labels for an explanation): clipping %, **longest clip** (the longest single stretch at
  the limit; 0.1–0.2 s is a bump or kerb, 0.5 s or more a whole part of a corner), **average clip** length with the
  number of separate clipping moments (consecutive clipped 0.1 s steps count as one), average and strongest torque,
  soft-lock time, temperatures, base, rotation and FFB gain.
- **Temperature graph**: motor and MOSFET.
- Scroll to zoom, drag to pan, double-click for the whole stage, hover for all values at that moment.

**Apply in StageSwitch…**: when AC Rally StageSwitch is running and a stage has a suggested gain, the advice box has
this button. It asks StageSwitch (local named pipe `ACRallyStageSwitch`) for the current values and opens a dialog
with four choices: this car on this stage, this car on all the rally's stages, this stage for all cars, or all the
rally's stages for all cars (the gain can still be adjusted). StageSwitch saves it and applies it at once when you're
on that stage. New runs store StageSwitch's ids (`stage_id: MonteCarlo/Sisteron`); older runs use the stage title.

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
- **Clipping** counts samples within 0.15 Nm of the base's torque limit (see *Which base*).
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
