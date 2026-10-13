# Regenerates docs/screenshots/*.png from the demo server, in dark and light themes.
# Usage: pwsh build/screenshots.ps1 [-Exe path\to\SQLDiskMonitor.exe]
param([string]$Exe)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'docs\screenshots'
$work = Join-Path ([IO.Path]::GetTempPath()) 'sdm-screenshots'

if (-not $Exe) {
    # WPF can't load its native DLLs from very long paths, so build to a short temp folder.
    $bin = Join-Path $work 'bin'
    dotnet build (Join-Path $root 'src\SQLDiskMonitor\SQLDiskMonitor.csproj') -c Release -o $bin | Out-Null
    $Exe = Join-Path $bin 'SQLDiskMonitor.exe'
}

New-Item -ItemType Directory -Force $out | Out-Null
foreach ($theme in 'dark', 'light') {
    $data = Join-Path $work "data-$theme"
    if (Test-Path $data) { Get-ChildItem $data -File | Remove-Item -Force }
    New-Item -ItemType Directory -Force $data | Out-Null
    '{ "CostsEnabled": true, "CurrencySymbol": "$", "CheckForUpdates": false, "MinimizeToTray": false }' |
        Set-Content (Join-Path $data 'settings.json')
    $env:SQLDISKMONITOR_DATA = $data
    $p = Start-Process $Exe -ArgumentList "--screenshot `"$out\$theme.png`" --page all --range 7d --theme $theme" -PassThru
    $p.WaitForExit(300000) | Out-Null
}
Remove-Item Env:\SQLDISKMONITOR_DATA
Get-ChildItem $out -Filter *.png | Select-Object Name, Length
