param([string]$Exe, [string]$Out)
# Renders MozaHud's window in this process (English formatting) and saves screenshots of each feature.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PW { [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags); }
"@
$en = [Globalization.CultureInfo]::GetCultureInfo('en-US')
[Threading.Thread]::CurrentThread.CurrentCulture = $en
[Threading.Thread]::CurrentThread.CurrentUICulture = $en
New-Item -ItemType Directory -Force $Out | Out-Null

$asm = [Reflection.Assembly]::LoadFile($Exe)
$T = { param($n) $asm.GetType("MozaHud.$n") }
$bfI = [Reflection.BindingFlags]'NonPublic,Public,Instance'
$bfS = [Reflection.BindingFlags]'NonPublic,Public,Static'
(& $T 'Settings').GetMethod('Load').Invoke($null, $null)

function Pump($ms) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.ElapsedMilliseconds -lt $ms) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 } }
function Grab($ctrl, $name, $rect) {
    $bmp = New-Object Drawing.Bitmap $ctrl.Width, $ctrl.Height
    $g = [Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [void][PW]::PrintWindow($ctrl.Handle, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
    if ($rect) { $c = $bmp.Clone($rect, $bmp.PixelFormat); $bmp.Dispose(); $bmp = $c }
    $bmp.Save((Join-Path $Out $name), [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Host "saved $name"
}
function Field($obj, $name) { $obj.GetType().GetField($name, $bfI).GetValue($obj) }
# rectangle of a child control inside the PrintWindow image of the whole window (title bar and borders included)
function RectOf($ctrl, $pad) {
    $s = $ctrl.PointToScreen([Drawing.Point]::Empty)
    New-Object Drawing.Rectangle ($s.X - $win.Left - $pad), ($s.Y - $win.Top - $pad), ($ctrl.Width + 2 * $pad), ($ctrl.Height + 2 * $pad)
}

$rec = [Activator]::CreateInstance((& $T 'StageRecorder'))
$rec.GetType().GetField('<Status>k__BackingField', $bfI).SetValue($rec, 'Waiting for AC Rally')
$dot = [string][char]0x00B7; $deg = [string][char]0x00B0
$lines = [string[]]@("R5 $dot 5.5 Nm", "Motor 31.2 ${deg}C $dot MOSFET 38.0 ${deg}C $dot MCU 39.0 ${deg}C", "Torque 2.1 Nm $dot peak 4.8 Nm $dot clipping 0 %", "Rotation 450$deg")
$func = [Func[string[]]]{ return ,$lines }
$icon = (& $T 'TrayApp').GetMethod('MakeAppIcon', $bfS).Invoke($null, $null)

$win = [Activator]::CreateInstance((& $T 'MainWindow'), @($rec, $func, $icon))
$win.StartPosition = 'Manual'; $win.Location = New-Object Drawing.Point(-4000, 100); $win.Size = New-Object Drawing.Size(1180, 800)
$win.Show(); Pump 2500
$list = Field $win '_list'
$chart = Field $win '_chart'

# 1. a stage that clipped too much (red advice, Apply in StageSwitch button)
$list.SelectedIndex = 1; Pump 1500
Grab $win 'overview-too-strong.png' $null
$advice = Field $win '_advice'
$stats = Field $win '_stats'
Grab $win 'advice-too-strong.png' (RectOf $advice 0)
Grab $win 'key-figures.png' (RectOf $stats 6)

# 2. the same stage after lowering the gain (green advice)
$list.SelectedIndex = 0; Pump 1500
Grab $win 'overview-good.png' $null
Grab $win 'advice-good.png' (RectOf $advice 0)
$lr = RectOf $list 0
Grab $win 'stage-list.png' (New-Object Drawing.Rectangle $lr.X, ($lr.Y - 32), $lr.Width, 150)
$hdr = (Field $win '_status').Parent
Grab $win 'header.png' (RectOf $hdr 0)

# 3. zoomed in on clipping, with the hover box
$list.SelectedIndex = 1; Pump 1000
$run = Field $win '_shown'
$first = ($run.Points | Where-Object { $_.Clip -and -not $_.Lock } | Select-Object -First 1).T
$chart.GetType().GetField('_viewFrom', $bfI).SetValue($chart, [double]($first - 4))
$chart.GetType().GetField('_viewTo', $bfI).SetValue($chart, [double]($first + 8))
$tr = $chart.GetType().GetProperty('TorqueRect', $bfI).GetValue($chart)
$hx = [int]($tr.Left + (4.0 / 12.0) * $tr.Width)
$chart.GetType().GetField('_hoverX', $bfI).SetValue($chart, $hx)
$chart.Invalidate(); Pump 500
Grab $chart 'graph-zoom-clipping.png' $null

# 4. the whole stage graph
$chart.GetType().GetField('_hoverX', $bfI).SetValue($chart, -1)
$chart.GetType().GetField('_viewFrom', $bfI).SetValue($chart, [double]0)
$chart.GetType().GetField('_viewTo', $bfI).SetValue($chart, [double]$run.Points[$run.Points.Count - 1].T)
$chart.Invalidate(); Pump 500
Grab $chart 'graph-whole-stage.png' $null

# 5. Apply in StageSwitch dialog (real values from StageSwitch)
$link = & $T 'StageSwitchLink'
try {
    $now = $link.GetMethod('Get').Invoke($null, @('MonteCarlo', 'Sisteron', 'Hyundai i20N Rally2'))
    $d = [Activator]::CreateInstance((& $T 'ApplyGainDialog'), @($now, 'Hyundai i20N Rally2', 135, 'MozaHud suggests 135 (was 150): clipping 5.4 % against your allowed 2.5 %.', $icon))
    $d.StartPosition = 'Manual'; $d.Location = New-Object Drawing.Point(-4000, 100); $d.Show(); Pump 800
    Grab $d 'apply-in-stageswitch.png' $null
    $d.Close(); $d.Dispose()
} catch { Write-Host "dialog skipped: $($_.Exception.Message)" }

$win.Dispose()
