# Builds the installable package, named with the version it contains.
#
# The name carries the version because a folder called "Setup" tells you
# nothing once a second one exists: the machine that got installed from the
# wrong copy looked like a bad install rather than an old build. The version is
# read from the csproj, so it is whatever was last committed - never typed.

[CmdletBinding()]
param(
    # Where the finished package folder is created. Defaults to the desktop's
    # j0ker Dev folder (the desktop may be redirected into OneDrive, so it is
    # resolved rather than assumed).
    [string] $OutputRoot = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'j0ker Dev'),
    # Keep older package folders instead of removing them.
    [switch] $KeepOld
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$csproj = Join-Path $repo 'J0kersMediaServer.csproj'

$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "no <Version> found in $csproj" }

$name    = "j0kers Media Server Setup $version"
$dest    = Join-Path $OutputRoot $name
$payload = Join-Path $dest 'payload'

Write-Host ''
Write-Host ("Building $name") -ForegroundColor Cyan
Write-Host ''

if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
New-Item -ItemType Directory -Path $payload -Force | Out-Null

# The server, with the .NET runtime inside it so a machine with nothing
# installed can run it.
Write-Host '  publishing the server (self-contained)...'
dotnet publish $csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o $payload -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# ffmpeg and ffprobe travel with it, so transcoding and live TV work on a
# machine that has never heard of ffmpeg.
$ff = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Recurse -Filter 'ffmpeg.exe' -ErrorAction SilentlyContinue |
      Select-Object -First 1
if (-not $ff) { throw 'ffmpeg.exe not found (winget install Gyan.FFmpeg)' }
Write-Host '  bundling ffmpeg + ffprobe...'
Copy-Item $ff.FullName (Join-Path $payload 'ffmpeg.exe') -Force
$fp = Join-Path (Split-Path -Parent $ff.FullName) 'ffprobe.exe'
if (Test-Path -LiteralPath $fp) { Copy-Item $fp (Join-Path $payload 'ffprobe.exe') -Force }

# Defaults, written only on a first install - Install.ps1 keeps whatever an
# existing install already has.
Copy-Item (Join-Path $repo 'config\providers.json') (Join-Path $payload 'providers.json') -Force
Copy-Item (Join-Path $repo 'installer\default-server.json') (Join-Path $payload 'server.json') -Force

foreach ($f in 'Install.cmd', 'Install.ps1', 'README.txt') {
    Copy-Item (Join-Path $repo "installer\$f") $dest -Force
}

# One "Setup" folder per version is the point; several is the confusion it was
# meant to remove.
if (-not $KeepOld) {
    Get-ChildItem -LiteralPath $OutputRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'j0kers Media Server Setup*' -and $_.Name -ne $name } |
        ForEach-Object { Write-Host ("  removing older package: " + $_.Name); Remove-Item -LiteralPath $_.FullName -Recurse -Force }
}

$mb = [math]::Round(((Get-ChildItem -LiteralPath $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 0)
Write-Host ''
Write-Host ("Built: $dest  (${mb} MB)") -ForegroundColor Green
Write-Host ''
