# Builds MozaHud.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'MozaHud.exe'
$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$ico = Join-Path $root 'src\MozaHud.ico'   # app logo for the exe and shortcuts, made by tools\make-icon.ps1
& $csc /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /out:$out /win32icon:$ico `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll `
    $src
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $out"
