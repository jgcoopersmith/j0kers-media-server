<#
.SYNOPSIS
    Cleanly restart j0kers Media Server.

.DESCRIPTION
    Stops any running j0kers-media-server instance, then relaunches the published
    exe with the REPO ROOT as its working directory. That working directory is the
    critical detail: config resolution finds .\config\server.json there and loads
    your real settings (HTTPS, tray, accounts, channels). Launching from any other
    directory falls through to built-in defaults and the dashboard never comes up.

    openDashboardOnStart in config\server.json reopens the dashboard automatically.

.EXAMPLE
    .\restart-server.ps1
#>
[CmdletBinding()]
param(
    [int]$WaitSeconds = 6
)

$ErrorActionPreference = 'Stop'

# Repo root = this script's folder. The exe lives in .\publish\.
$RepoRoot = $PSScriptRoot
$Exe      = Join-Path $RepoRoot 'publish\j0kers-media-server.exe'
$Ports    = 8554, 8080, 9090   # RTSP, HLS(https), dashboard(https)

if (-not (Test-Path $Exe)) {
    Write-Error "Executable not found: $Exe"
    exit 1
}

Write-Host "== j0kers Media Server restart ==" -ForegroundColor Cyan

# --- stop ---
$existing = Get-Process j0kers-media-server -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host ("Stopping {0} instance(s): {1}" -f $existing.Count, ($existing.Id -join ', '))
    Stop-Process -Name j0kers-media-server -Force
    Start-Sleep -Milliseconds 900
    if (Get-Process j0kers-media-server -ErrorAction SilentlyContinue) {
        Write-Error "Failed to stop existing instance(s)."
        exit 1
    }
} else {
    Write-Host "No running instance found."
}

# --- start (from repo root, so it loads .\config\server.json) ---
Write-Host "Launching from $RepoRoot ..."
Start-Process -FilePath $Exe -WorkingDirectory $RepoRoot
Start-Sleep -Seconds $WaitSeconds

# --- verify ---
$proc = Get-Process j0kers-media-server -ErrorAction SilentlyContinue
if (-not $proc) {
    Write-Error "Server process is not running after launch. Check config\logs\j0kers.log."
    exit 1
}

$bound = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
         Where-Object { $_.LocalPort -in $Ports } |
         Select-Object -ExpandProperty LocalPort -Unique | Sort-Object
$missing = $Ports | Where-Object { $_ -notin $bound }

Write-Host ("PID {0} running. Listening ports: {1}" -f $proc.Id, ($bound -join ', ')) -ForegroundColor Green
if ($missing) {
    Write-Warning ("Expected ports not yet bound: {0} (dashboard = 9090). Check config\logs\j0kers.log." -f ($missing -join ', '))
    exit 2
}

Write-Host "Dashboard: https://localhost:9090/  (also this machine's LAN address on :9090)" -ForegroundColor Green
Write-Host "Clean restart complete." -ForegroundColor Green
exit 0
