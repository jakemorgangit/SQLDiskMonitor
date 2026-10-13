# SQL Disk Monitor: map Windows volumes to Azure data disk LUNs.
# Run on the SQL Server VM (PowerShell; no admin rights or modules needed), then in SQL Disk Monitor use
# Disks > Import CSV with the file this writes, and Disks > Sync from Azure. Storage pools list all their LUNs.

param([string]$OutFile = (Join-Path ([Environment]::GetFolderPath('Desktop')) "$env:COMPUTERNAME-disk-luns.csv"))

$ErrorActionPreference = 'SilentlyContinue'
$lunOf = @{}
Get-CimInstance Win32_DiskDrive | ForEach-Object { $lunOf[[int]$_.Index] = $_.SCSILogicalUnit }

function Get-Lun([string]$location) {
    if ($location -match 'LUN (\d+)') { [int]$Matches[1] }
}

$rows = foreach ($p in Get-Partition | Where-Object { $_.AccessPaths }) {
    $disk = Get-Disk -Number $p.DiskNumber
    $lun = if ($lunOf.ContainsKey([int]$p.DiskNumber)) { $lunOf[[int]$p.DiskNumber] } else { Get-Lun $disk.Location }
    $poolLuns = ''
    $label = (Get-Volume -Partition $p).FileSystemLabel

    # The OS disk and the Azure temporary disk sit on other controllers and can also report LUN 0; leave them blank.
    if ($p.IsBoot -or $p.IsSystem -or $disk.IsBoot -or $label -eq 'Temporary Storage') { $lun = '' }

    # A Storage Spaces virtual disk: report the LUNs of the physical disks behind it.
    $vd = Get-VirtualDisk | Where-Object { (Get-Disk -VirtualDisk $_).Number -eq $p.DiskNumber }
    if ($vd) {
        $poolLuns = ($vd | Get-PhysicalDisk | ForEach-Object { Get-Lun $_.PhysicalLocation } | Sort-Object) -join ','
        $lun = ''
    }

    foreach ($path in $p.AccessPaths | Where-Object { $_ -notlike '\\?\*' }) {
        [pscustomobject]@{
            'Volume'         = $path
            'LUN'            = $lun
            'Pool LUNs'      = $poolLuns
            'Disk number'    = $p.DiskNumber
            'Device / model' = $disk.FriendlyName
            'Size GB'        = [math]::Round($disk.Size / 1GB)
        }
    }
}

$file = $OutFile
$rows | Sort-Object Volume | Export-Csv $file -NoTypeInformation
$rows | Sort-Object Volume | Format-Table -AutoSize
Write-Host "Saved $file  -  import it in SQL Disk Monitor (Disks > Import CSV)." -ForegroundColor Cyan
