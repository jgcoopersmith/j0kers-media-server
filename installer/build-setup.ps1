<#
.SYNOPSIS
    Builds the self-installing package - one .exe that installs or upgrades
    j0kers Media Server - and leaves it on the desktop.

.DESCRIPTION
    The package is the setup stub with the whole payload appended to it:

        [ stub ][ payload.zip ][ 8-byte length ][ 16-byte marker ]

    One file, because it has to be carried to another machine. Self-contained,
    because that machine is a bare Windows 11 box with no .NET, no 7-Zip and no
    ffmpeg. Nothing on the target is assumed beyond Windows itself.

    ffmpeg and ffprobe travel with it for that reason, and they are most of the
    size. They do not change between versions, but a package that leaves them
    out only installs onto a machine that already has them, which is not what
    "copy this to another box" means.

.EXAMPLE
    .\installer\build-setup.ps1
#>
[CmdletBinding()]
param(
    # Where the finished package lands. The desktop, because that is where it
    # is looked for and where it gets copied from.
    [string] $OutputDir = [Environment]::GetFolderPath('Desktop'),
    # Keep older packages instead of removing them.
    [switch] $KeepOld
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 does not load this one by itself.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repo 'J0kersMediaServer.csproj'

$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version |
           Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "no <Version> found in $csproj" }

# The name deliberately carries no version.
#
# It used to. Every build then produced a differently-named file and deleted
# the previous one, so a path copied out of a message, a shortcut, or a
# half-finished copy to another machine stopped resolving the moment the next
# build ran - "this file no longer exists" for something that was there a
# minute ago. Six versions in one afternoon made that constant.
#
# One stable path instead. Which version it is lives in the file's own version
# information (right-click - Properties - Details) and is printed by the
# installer as it runs, both of which travel with the file rather than being
# rubbed off by the next build.
$name    = "j0kers Media Server Setup.exe"
$target  = Join-Path $OutputDir $name
$staging = Join-Path ([IO.Path]::GetTempPath()) ("j0kers-pkg-" + [Guid]::NewGuid().ToString('n').Substring(0,8))
$payload = Join-Path $staging 'payload'

Write-Host ''
Write-Host "Building $name" -ForegroundColor Cyan
Write-Host ''

try {
    New-Item -ItemType Directory -Path $payload -Force | Out-Null

    # --- the server, with the runtime inside it -----------------------------
    Write-Host '  publishing the server (self-contained)...'
    dotnet publish $csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -o $payload -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw 'publishing the server failed' }
    # publish drops a .pdb and the odd stray beside it; the payload is only the program
    Get-ChildItem $payload -File | Where-Object { $_.Extension -in '.pdb', '.xml' } | Remove-Item -Force

    # --- ffmpeg, so a machine that has never heard of it still works --------
    # Taken from this install first: it is the pair the server has been running
    # against, so what ships is what was tested, not whatever winget has today.
    $installed = Join-Path $env:LOCALAPPDATA 'Programs\j0kers Media Server'
    # Wrapped in @() around the WHOLE pipeline, not just the list: a
    # Where-Object that keeps one item returns that item, not a one-element
    # array, and indexing [0] into a string hands back its first character.
    $ffSources = @(@(
        (Join-Path $installed 'ffmpeg.exe'),
        (Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Recurse -Filter 'ffmpeg.exe' -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName)
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
    if (-not $ffSources) { throw 'ffmpeg.exe not found - install it (winget install Gyan.FFmpeg) or run the server once' }
    $ffmpeg  = $ffSources[0]
    $ffprobe = Join-Path (Split-Path -Parent $ffmpeg) 'ffprobe.exe'
    if (-not (Test-Path -LiteralPath $ffprobe)) { throw "ffprobe.exe not found beside $ffmpeg" }
    Write-Host ('  bundling ffmpeg + ffprobe from ' + (Split-Path -Parent $ffmpeg) + '...')
    Copy-Item $ffmpeg  (Join-Path $payload 'ffmpeg.exe')  -Force
    Copy-Item $ffprobe (Join-Path $payload 'ffprobe.exe') -Force

    # --- defaults, written only on a first install --------------------------
    # server.json is the only default that must be there: it is what a first
    # run reads. Install.ps1 never overwrites an existing one.
    Copy-Item (Join-Path $repo 'installer\default-server.json') (Join-Path $payload 'server.json') -Force

    # The free-TV provider list is optional and is not in the repository - the
    # older packaging script assumed it was and failed there. Take whichever
    # copy exists, and ship without it if neither does; the server writes its
    # own on first use.
    $providers = @(
        (Join-Path $repo 'config\providers.json'),
        (Join-Path $installed 'providers.json')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($providers) { Copy-Item $providers (Join-Path $payload 'providers.json') -Force }
    else { Write-Host '  no providers.json anywhere - shipping without it' }

    # --- the installer itself, beside the payload ---------------------------
    foreach ($f in 'Install.cmd', 'Install.ps1', 'README.txt') {
        Copy-Item (Join-Path $repo "installer\$f") $staging -Force
    }
    # Install.ps1 expects a "payload" folder next to it, which is what $payload is.

    # --- zip it -------------------------------------------------------------
    Write-Host '  packing...'
    $zip = Join-Path ([IO.Path]::GetTempPath()) ("j0kers-payload-" + [Guid]::NewGuid().ToString('n').Substring(0,8) + '.zip')
    # Fastest on purpose: half a gigabyte of already-compressed executables
    # barely shrinks whatever level is asked for, and the wait is real.
    [IO.Compression.ZipFile]::CreateFromDirectory($staging, $zip,
        [IO.Compression.CompressionLevel]::Fastest, $false)

    # --- the stub -----------------------------------------------------------
    Write-Host '  building the setup stub...'
    $stubDir = Join-Path ([IO.Path]::GetTempPath()) ('j0kers-stub-' + [Guid]::NewGuid().ToString('n').Substring(0,8))
    # Stamped with the server version, because the file name no longer carries
    # it: this is what Properties - Details shows, and it is how you tell two
    # copies apart once they are on different machines.
    dotnet publish (Join-Path $repo 'installer\Setup\Setup.csproj') -c Release -o $stubDir -v q --nologo `
        -p:Version=$version -p:FileVersion=$version -p:AssemblyVersion=$version `
        -p:InformationalVersion=$version
    if ($LASTEXITCODE -ne 0) { throw 'building the setup stub failed' }
    $stub = Join-Path $stubDir 'j0kers-setup-stub.exe'
    if (-not (Test-Path -LiteralPath $stub)) { throw "stub not found at $stub" }

    # --- stub + zip + footer ------------------------------------------------
    Write-Host '  assembling the package...'
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
    $out = [IO.File]::Create($target)
    try {
        foreach ($part in @($stub, $zip)) {
            $in = [IO.File]::OpenRead($part)
            try { $in.CopyTo($out, 1MB) } finally { $in.Dispose() }
        }
        $len = (Get-Item -LiteralPath $zip).Length
        $out.Write([BitConverter]::GetBytes([int64]$len), 0, 8)
        $out.Write([Text.Encoding]::ASCII.GetBytes('J0KERSMEDIAPKG01'), 0, 16)
    }
    finally { $out.Dispose() }
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue

    # --- the desktop launcher ---------------------------------------------
    # Publishing replaces the installed binary, and for the moment it is gone
    # Windows treats the desktop shortcut as broken - three times in one
    # afternoon it removed it outright, leaving no way to start the server from
    # the desktop and no sign of why. Rebuilding it here costs nothing and
    # makes the end of a round self-healing rather than something to remember.
    $installedExe = Join-Path $installed 'j0kers-media-server.exe'
    if (Test-Path -LiteralPath $installedExe) {
        $lnk = Join-Path $OutputDir 'j0kers Media Server.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $sc = $shell.CreateShortcut($lnk)
        $sc.TargetPath       = $installedExe
        $sc.WorkingDirectory = $installed
        $sc.Arguments        = '"server.json"'
        $sc.IconLocation     = "$installedExe,0"
        $sc.Description      = 'j0kers Media Server'
        $sc.Save()
        # Read it back: a shortcut that saved but points nowhere is the exact
        # failure this is here to stop, and it is invisible until it is needed.
        $check = $shell.CreateShortcut($lnk)
        if ($check.TargetPath -eq $installedExe) {
            Write-Host ('  desktop shortcut -> ' + $installedExe)
        }
        else {
            Write-Host '  WARNING: the desktop shortcut did not save correctly' -ForegroundColor Yellow
        }
    }

    $mb = [math]::Round((Get-Item -LiteralPath $target).Length / 1MB)
    Write-Host ''
    Write-Host "Built: $target  (${mb} MB)" -ForegroundColor Green
    Write-Host ''

    # Sweep up the version-stamped packages left by earlier builds. The current
    # one is not among them - it has no version in its name any more - so this
    # removes history rather than the thing just built, and the guard below
    # keeps it that way if the naming ever changes again.
    if (-not $KeepOld) {
        Get-ChildItem -LiteralPath $OutputDir -File -Filter 'j0kers Media Server Setup *.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne $name } |
            ForEach-Object {
                try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop; Write-Host ('  removed older package: ' + $_.Name) }
                catch { Write-Host ('  could not remove ' + $_.Name + ' (in use) - left in place') }
            }
    }
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    if ($stubDir) { Remove-Item -LiteralPath $stubDir -Recurse -Force -ErrorAction SilentlyContinue }
}
