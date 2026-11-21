# Logs candidate live-torque values from a MOZA base to CSV, for working out which one is the
# "torque curve" Pit House shows. Read-only: only sends read requests (groups 43 and 40).
#
#   .\tools\torque-log.ps1 -Seconds 120              # writes tools\logs\torque-<time>.csv
#
# Close Moza Pit House first (it holds the COM port). MozaHud may keep running: it just reports "in use" meanwhile.
param(
    [int]$Seconds = 60,
    [string]$Port = 'COM3',
    [string]$Out = ''
)
$ErrorActionPreference = 'Stop'

# name, group, device, id
$fields = @(
    @('mot_torque',   43, 19, 7),    # MotGetState_MotTorque
    @('phase_a',      43, 19, 10),   # MotGetState_APhaseCurrent
    @('phase_b',      43, 19, 11),
    @('phase_c',      43, 19, 12),
    @('encoder_raw',  43, 19, 14),   # MotGetState_EncoderRaw
    @('sensor_torque',43, 19, 16),   # MotGetState_SensorTorque
    @('state',        43, 19, 1),    # MotGetState_SteerState
    @('torque_buf',   40, 19, 38)    # MotGetSteer_SteerTorqueBufVal
)

function New-Frame([int]$grp, [int]$dev, [int]$id) {
    $b = [byte[]](0x7E, 3, $grp, $dev, $id, 0, 0, 0)
    $s = 13; for ($k = 0; $k -lt 7; $k++) { $s += $b[$k] }
    $b[7] = [byte]($s % 256)
    return , $b
}
$frames = $fields | ForEach-Object { , (New-Frame $_[1] $_[2] $_[3]) }
$lookup = @{}; foreach ($f in $fields) { $lookup["$(($f[1]) -bor 0x80):$($f[3])"] = $f[0] }

if (-not $Out) {
    $dir = Join-Path $PSScriptRoot 'logs'; New-Item -ItemType Directory -Force $dir | Out-Null
    $Out = Join-Path $dir ("torque-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.csv')
}

$p = New-Object System.IO.Ports.SerialPort $Port, 115200
$p.ReadTimeout = 5; $p.DtrEnable = $true
for ($try = 0; $try -lt 40 -and -not $p.IsOpen; $try++) { try { $p.Open() } catch { Start-Sleep -Milliseconds 100 } }
if (-not $p.IsOpen) { throw "Could not open $Port (Pit House running?)" }

$w = New-Object System.IO.StreamWriter($Out, $false, [Text.Encoding]::UTF8)
$w.WriteLine('t_ms,' + (($fields | ForEach-Object { $_[0] }) -join ','))
$cur = @{}
$buf = New-Object System.Collections.Generic.List[byte]
$chunk = New-Object byte[] 4096
$sw = [Diagnostics.Stopwatch]::StartNew()
$rows = 0; $lastPrint = 0
Write-Host "Logging to $Out for $Seconds s ..."
try {
    while ($sw.ElapsedMilliseconds -lt $Seconds * 1000) {
        foreach ($f in $frames) { $p.Write($f, 0, $f.Length) }
        $until = $sw.ElapsedMilliseconds + 40
        while ($sw.ElapsedMilliseconds -lt $until) {
            try { $n = $p.Read($chunk, 0, $chunk.Length); for ($k = 0; $k -lt $n; $k++) { $buf.Add($chunk[$k]) } } catch [TimeoutException] {}
            # parse complete frames: 7E len grp dev payload(len) checksum
            $i = 0
            while ($i -lt $buf.Count) {
                if ($buf[$i] -ne 0x7E) { $i++; continue }
                if ($i + 1 -ge $buf.Count) { break }
                $tot = $buf[$i + 1] + 5
                if ($i + $tot -gt $buf.Count) { break }
                if ($buf[$i + 1] -eq 3 -and $buf[$i + 3] -eq 0x31) {
                    $key = "$($buf[$i + 2]):$($buf[$i + 4])"
                    if ($lookup.ContainsKey($key)) { $v = ([int]$buf[$i + 5] * 256) + [int]$buf[$i + 6]; if ($v -ge 32768) { $v -= 65536 }; $cur[$lookup[$key]] = $v }
                }
                $i += $tot
            }
            if ($i -gt 0) { $buf.RemoveRange(0, [Math]::Min($i, $buf.Count)) }
        }
        $t = $sw.ElapsedMilliseconds
        $w.WriteLine("$t," + (($fields | ForEach-Object { $cur[$_[0]] }) -join ','))
        $rows++
        if ($t - $lastPrint -ge 500) {
            $lastPrint = $t
            Write-Host ("{0,6:0.0}s  torque {1,6}  A {2,6} B {3,6} C {4,6}  enc {5,6}" -f ($t / 1000), $cur.mot_torque, $cur.phase_a, $cur.phase_b, $cur.phase_c, $cur.encoder_raw)
        }
    }
}
finally {
    $w.Close(); $p.Close()
    Write-Host "$rows rows written to $Out"
}
