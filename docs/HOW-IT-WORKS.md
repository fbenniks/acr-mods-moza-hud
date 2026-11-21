# How MozaHud works

The technical side, for the curious and for anyone who wants to build on it. For what MozaHud does and how to use
it, see the [README](../README.md).

## Talking to the wheelbase

The base's USB serial port (vendor id `346E`, found via WMI) speaks the MOZA serial protocol, as reverse-engineered
by [boxflat](https://github.com/Lawstorant/boxflat) (`data/serial.yml`). Newer command names come from the command
database embedded in Pit House's `bin\FirmwareManager.exe`.

- Frame: `7E`, payload length, group, device, payload…, checksum = (13 + sum of all previous bytes) % 256, 115200 baud.
- Replies have bit 7 set in the group (43 → `AB`) and the device id nibbles swapped (base `13` → `31`).
- The base also sends debug text frames (group `0E`) on the same port; they are skipped.

| Read | Request | What |
|---|---|---|
| MCU / MOSFET / motor temperature | group 43, ids 4 / 5 / 6 | 1/100 °C, every 2 s |
| `MotGetState_MotTorque` | group 43, id 7 | live FFB torque, about 30×/s: 500 = no force, 1 step = 0.1 Nm |
| Rotation | group 40, id 1 | half degrees (225 = 450°) |
| Max torque setting | group 40, id 18 | % |
| Product type | group 7, payload = sector 1…4 | text, e.g. `R5 Black # MOT-1-V03` |
| Hardware version | group 8, payload = sector | text, e.g. `RS21-D05-HW BM-CU-V10` |
| Firmware | group 4 | 4 bytes |

All of these are read-only queries, the same ones Pit House sends when it connects. MozaHud never writes to the base.
(The generic groups 1, 10 and 17–25 are device control, EEPROM, activation and bootloader commands: never sent.)

The torque value was found in Pit House's database and confirmed by logging while driving (`tools/torque-log.ps1`):
it opposes the steering and clips at 445 / 554 on an R5 (±5.5 Nm). That one step is exactly 0.1 Nm (and not 0.1 % of
the current limit) is checked from the other side by the ceiling check below.

## Which base

The model is matched with the patterns from Pit House's `bin\config\device_config.json` (product type, plus hardware
version for Pro/Ultra variants). The base doesn't report its rated torque, so MozaHud has a table: the number in the
name is the Nm (R9 9, R12 12, R16 16, R21 21, R25 25, R27 27; unknown future models the same way), except R3 = 3.9 and
the R5 family = 5.5. Torque limit = rated × max torque setting.

**Ceiling check**: when a base clips, its torque reading sticks at one value (R5: 5.4 Nm, hit far more often than the
value just below). If such a plateau is more than 0.3 Nm away from the expected limit, MozaHud uses the measured value
from then on (remembered per model and max torque setting in `settings.ini`, logged).

## Torque, clipping and soft lock

- **Torque** is low-passed with a 150 ms time constant (on the signed value, so a quick reversal passes through zero).
  The overlay's peak is the highest smoothed value of the last second; values are republished every 100 ms.
- **Clipping**: a raw sample within 0.15 Nm of the base's limit. Stage runs store per 0.1 s whether any sample clipped.
- **Soft lock**: the wheel position comes from the Windows joystick axis, which spans exactly the base's rotation
  (AC Rally's steering value is identical). Within 3 % of full lock, clipping is the lock pushing back and isn't counted.
- **Clipping moments**: consecutive clipped 0.1 s steps while driving are one moment; standing still or the soft lock
  ends a moment. Longest and average length come from these.

## Stage recording (AC Rally)

AC Rally's shared memory uses the ACC layout (`Local\acpmf_physics`, `_graphics`, `_static`), but leaves the status,
lap and `iCurrentTime` fields at 0. What MozaHud uses:

- **On a stage** while the track name (static block) is filled; the menu clears it.
- **Finish**: the text stage clock (graphics `currentTime`, e.g. `02:58.143`) stops while the car still moves and the
  physics keeps ticking; points after that are dropped. Fallback: the graphics block stops updating (results screen).
- **Restart**: the stage clock jumps back. **Pause**: the physics block stops updating.
- Speed and steering from the physics block.
- Stage name, car and FFB gain from AC Rally StageSwitch's log (`Applied FFB gain 150 for Monte-Carlo · Sisteron ·
  Hyundai i20N Rally2`, and the ids from `Stage loaded: … -> MonteCarlo/Sisteron`), otherwise from the game's save file.

Runs are CSV files in `%APPDATA%\MozaHud\stages` (10 points per second: smoothed torque, raw peak, clipping, soft lock,
wheel, speed, temperatures). The last 100 runs are kept, none older than 60 days; runs with under 20 s of driving
aren't saved.

## FFB gain advice

Only time while driving (over 5 km/h) and away from the soft lock counts.

The **target clipping** setting (default 2.5 %) is a goal with a green zone below it, from 60 % of the target up to
the target (1.5–2.5 % by default):

- Above the target → *a bit strong* (less than 1.5 points over) or *too strong*, with a lower gain.
- In the zone → *good*.
- Under the zone → *room to spare* (blue), with a higher gain. Only after at least 60 s of driving; at the game's
  maximum of 200 it points to the base's strength in Pit House instead.

The suggested gain is in steps of 1: the highest gain whose **predicted** clipping stays 0.3 points under the target
(and not under the zone). The prediction scales the run's raw torque peaks (one per 0.1 s) by new gain / old gain,
since AC Rally's force scales linearly with the gain:

- Going up it is exact for every peak below the limit: a peak clips once peak × new/old reaches the limit.
- Going down, a clipped peak's real size is unknown; how far past the limit they went is estimated from how fast the
  peaks thin out in the last 1 Nm below the limit (an exponential tail).

Checked on runs of the same stage at two gains: Sisteron 135 → predicted 5.7 % at 150, measured 5.35 %; Greece 180 →
4.4 % at 200, measured 4.47 %; down, Sisteron 150 → about 2.5 % at 135, measured 2.32 %. One recommendation changes the
gain by at most +15 % / −25 %, so one careful (or wild) run can't cause a big jump. `MozaHud.exe --advice | more`
prints the advice and predictions for every saved run.

## StageSwitch link

AC Rally StageSwitch listens on the local named pipe `ACRallyStageSwitch` (one tab-separated request line, one reply
line): `HELLO`, `GET rally stage car`, and `SETGAIN scope rally stage car gain` with scope `car`, `rallycar`, `stage`
or `rally`. StageSwitch changes its own settings through its own code, saves them and applies them at once when you're
on that stage.

## MSI Afterburner / RivaTuner

MozaHud hands its values to Afterburner through the shared memory block of Afterburner's AIDA64 plugin
(`AIDA64_SensorValues`, XML-like text), so no extra software or registration is needed. The one-time setup adds these
sources to `Plugins\Monitoring\AIDA64.cfg` (original kept as `AIDA64.cfg.mozahud-backup`):

| Afterburner graph | What it is |
|---|---|
| MOZA motor temp, MOZA MOSFET temp, MOZA MCU temp | Temperatures (°C) |
| MOZA torque avg | Current FFB torque, smoothed (~150 ms) (Nm) |
| MOZA torque peak | Highest smoothed torque of the last second (Nm) |
| MOZA FFB load | Torque peak as % of the base's limit |
| MOZA clipping | Share of the last second at the limit (%) |

If AIDA64 itself runs, it owns that block and MozaHud can't publish (the tray says so). An earlier version used
Windows performance counters; after re-registering the counter category, readers matched counters to the wrong
values, so setup now removes that registration.

## Files, tools and build

- Log: `%APPDATA%\MozaHud\log.txt`. Settings: `%APPDATA%\MozaHud\settings.ini`.
- `MozaHud.exe --probe` (with the tray app closed) reads the base once and prints identity, values and setup state.
- `tools\torque-log.ps1 -Seconds 120` logs the raw torque candidates to CSV.
- `tools\make-screenshots.ps1 -Exe <MozaHud.exe> -Out docs\images` renders the README screenshots from your own runs.
- Flags and car logos are drawn in code, copied from StageSwitch (`RallyStyle.cs`, `BrandMark.cs`).

```powershell
.\build.ps1
```

Uses the C# compiler that ships with Windows (.NET Framework 4.x); no SDK needed.
