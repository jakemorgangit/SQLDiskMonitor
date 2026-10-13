# Builds the self-contained app and the MSI into ./artifacts.
# Usage: pwsh build/build.ps1 [-Version 2.0.0]
param([string]$Version = "2.2.0")

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

dotnet publish (Join-Path $root 'src\SQLDiskMonitor\SQLDiskMonitor.csproj') -c Release -o $publish "-p:Version=$Version"
if ($LASTEXITCODE) { throw "publish failed" }

dotnet build (Join-Path $root 'installer\SQLDiskMonitor.Installer.wixproj') -c Release -o $artifacts "-p:Version=$Version" "-p:PublishDir=$publish\"
if ($LASTEXITCODE) { throw "installer build failed" }

Get-ChildItem $artifacts -Filter *.msi | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
