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
        ForEach-Object {
            # Tidying up must never fail the build: an old folder can be held
            # open by Explorer, by OneDrive syncing it, or by a server being
            # run from it, and losing the finished package over that is absurd.
            try {
                Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Stop
                Write-Host ("  removed older package: " + $_.Name)
            }
            catch { Write-Host ("  could not remove " + $_.Name + " (in use) - left in place") }
        }
}


$mb = [math]::Round(((Get-ChildItem -LiteralPath $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 0)
Write-Host ''
Write-Host ("Built: $dest  (${mb} MB)") -ForegroundColor Green
Write-Host ''
# Pack it into a single archive to hand over. A folder of 534 MB is awkward to
# copy to another machine; one file with the version in its name is not, and it
# is the thing that actually gets carried to a media server.
$rar = @(
    (Join-Path $env:ProgramFiles 'WinRAR\Rar.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'WinRAR\Rar.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

$archive = Join-Path $OutputRoot "$name.rar"
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }

if ($rar) {
    Write-Host '  packing the archive...'
    # -ep1 keeps the package folder as the archive's root rather than storing
    # the whole path; -r recurses; -m1 is fast, since 534 MB of already
    # compressed executables barely shrinks whatever setting is used.
    & $rar a -r -ep1 -m1 -idq $archive (Join-Path $dest '*') | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host ("  rar returned " + $LASTEXITCODE) }
}
else {
    # No WinRAR: a zip is still one file somebody can carry.
    Write-Host '  WinRAR not found - packing a .zip instead...'
    $archive = [IO.Path]::ChangeExtension($archive, '.zip')
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    Compress-Archive -Path (Join-Path $dest '*') -DestinationPath $archive -CompressionLevel Fastest
}

# One archive on the desktop at a time, same as the folder.
Get-ChildItem -LiteralPath $OutputRoot -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'j0kers Media Server Setup*' -and
                   $_.Extension -in '.rar', '.zip' -and
                   $_.Name -ne (Split-Path -Leaf $archive) } |
    ForEach-Object {
        try {
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop
            Write-Host ("  removed older archive: " + $_.Name)
        }
        catch { Write-Host ("  could not remove " + $_.Name + " (in use) - left in place") }
    }

if (Test-Path -LiteralPath $archive) {
    $amb = [math]::Round(((Get-Item -LiteralPath $archive).Length / 1MB), 0)
    Write-Host ("Packed: $archive  (${amb} MB)") -ForegroundColor Green
    Write-Host ''
}
