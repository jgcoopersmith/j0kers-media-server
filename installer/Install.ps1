# j0kers Media Server — installer.
#
# Installs, or upgrades in place. The point of the upgrade path is that a
# machine already running this server keeps everything it has: accounts, the
# signing key, channels, the library, saved settings, converted media and logs
# all stay exactly as they are, and only the program itself is replaced.
#
# Run Install.cmd (which calls this). No administrator rights are needed: the
# default target is under the user's own profile.

[CmdletBinding()]
param(
    # Where to install. Defaults to a per-user location so no elevation is
    # needed; pass -TargetDir to put it anywhere else.
    [string] $TargetDir = (Join-Path $env:LOCALAPPDATA 'Programs\j0kers Media Server'),
    # Skip the desktop shortcut.
    [switch] $NoShortcut,
    # Answer the prompts automatically (for unattended installs).
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$payload = Join-Path $here 'payload'

# Files the server owns once it is running. An upgrade never touches these —
# they are the difference between "upgraded" and "wiped". Anything not on this
# list that ships in the payload is program material and is replaced.
$KeepOnUpgrade = @(
    'server.json',            # ports, paths, ffmpeg settings
    'settings.json',          # what the dashboard's Config dialog saved
    'users.json',             # accounts and their keys
    'sessions.json',          # who is signed in
    'signing.key',            # invalidating this breaks every issued media link
    'server.pfx',             # the TLS certificate this machine generated
    'discovery-id',           # the identity TVs remember this server by
    'providers.json',         # which free-TV providers are on
    'channels.json',          # saved live channels
    'library.json',           # library folders
    'favorites.json',
    'playlists.json',
    'mounts.json',
    'dlna.json',
    'history.json',           # watch history
    'probe-cache.json',
    'transcode-queue.json',
    'unlinked.json'
)

function Write-Step($text) { Write-Host "  $text" }

Write-Host ''
Write-Host 'j0kers Media Server — install' -ForegroundColor Cyan
Write-Host ''

if (-not (Test-Path -LiteralPath $payload)) {
    throw "payload folder not found next to this script ($payload). Unpack the whole package and run Install.cmd from inside it."
}

$exeName  = 'j0kers-media-server.exe'

# Find a copy already on this machine when the default target has none. An
# earlier portable build was unpacked wherever the user chose, so installing
# blindly into the default would make a second, empty install beside the real
# one — new program, none of the accounts, channels or media. Prefer the
# install that is actually here. Only when -TargetDir was not given: an
# explicit target is an instruction, not a guess.
if (-not $PSBoundParameters.ContainsKey('TargetDir') -and
    -not (Test-Path -LiteralPath (Join-Path $TargetDir $exeName))) {

    $found = @()
    # a running server names its own location
    $found += @(Get-CimInstance Win32_Process -Filter "Name='$exeName'" -ErrorAction SilentlyContinue |
                Where-Object { $_.ExecutablePath } |
                ForEach-Object { Split-Path -Parent $_.ExecutablePath })
    # and so does the desktop shortcut a previous install left
    foreach ($d in @([Environment]::GetFolderPath('Desktop'),
                     (Join-Path ([Environment]::GetFolderPath('Desktop')) 'j0ker Dev'))) {
        if (-not (Test-Path -LiteralPath $d)) { continue }
        foreach ($l in Get-ChildItem -LiteralPath $d -Filter '*.lnk' -ErrorAction SilentlyContinue) {
            try {
                $t = (New-Object -ComObject WScript.Shell).CreateShortcut($l.FullName).TargetPath
                if ($t -and (Split-Path -Leaf $t) -eq $exeName) { $found += (Split-Path -Parent $t) }
            } catch { }
        }
    }
    $found = @($found | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ $exeName)) } | Select-Object -Unique)

    if ($found.Count -eq 1) {
        Write-Host ('Found an existing installation:  ' + $found[0]) -ForegroundColor Yellow
        Write-Host 'Upgrading it keeps its accounts, channels, library and media;'
        Write-Host 'installing fresh elsewhere would leave all of that behind.'
        Write-Host ''
        if ($Quiet) { $TargetDir = $found[0] }
        else {
            $ans = Read-Host ("Upgrade it? [Y/n]")
            if (-not $ans -or $ans -match '^(y|yes)$') { $TargetDir = $found[0] }
        }
    }
    elseif ($found.Count -gt 1) {
        # More than one copy on this machine — a build directory beside a real
        # install, say. Picking one unprompted upgrades something the user did
        # not mean to touch, so choose deliberately or not at all.
        Write-Host 'More than one installation was found:' -ForegroundColor Yellow
        for ($i = 0; $i -lt $found.Count; $i++) { Write-Host ("  [{0}] {1}" -f ($i + 1), $found[$i]) }
        Write-Host ("  [0] none of these — install to {0}" -f $TargetDir)
        Write-Host ''
        if ($Quiet) {
            Write-Host 'Ambiguous, and running unattended: none chosen. Re-run with'
            Write-Host '  Install.cmd -TargetDir "<the folder you mean>"'
            Write-Host ''
        }
        else {
            $ans = Read-Host 'Which one should be upgraded?'
            if ($ans -match '^\d+$' -and [int]$ans -ge 1 -and [int]$ans -le $found.Count) {
                $TargetDir = $found[[int]$ans - 1]
            }
        }
    }
}

$targetExe = Join-Path $TargetDir $exeName
$upgrade  = Test-Path -LiteralPath $targetExe

Write-Host ("Target:  " + $TargetDir)
Write-Host ("Mode:    " + $(if ($upgrade) { 'upgrade — existing settings and data are kept' } else { 'new install' }))
Write-Host ''

if (-not $Quiet) {
    $answer = Read-Host 'Continue? [Y/n]'
    if ($answer -and $answer -notmatch '^(y|yes)$') { Write-Host 'Cancelled.'; return }
}

# A running server holds its own exe open, so it has to stop before the file
# can be replaced. Only instances running from *this* target are touched — a
# server installed elsewhere on the same machine is left alone.
$running = @(Get-CimInstance Win32_Process -Filter "Name='$exeName'" -ErrorAction SilentlyContinue |
             Where-Object { $_.ExecutablePath -and (Split-Path -Parent $_.ExecutablePath) -eq $TargetDir.TrimEnd('\') })
if ($running.Count -gt 0) {
    Write-Step ("Stopping the running server ({0} instance(s))…" -f $running.Count)
    foreach ($p in $running) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 1200
}

New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

$copied = 0; $kept = 0
foreach ($item in Get-ChildItem -LiteralPath $payload -File) {
    $dest = Join-Path $TargetDir $item.Name
    # A config file that already exists is the user's, not ours.
    if ($upgrade -and ($KeepOnUpgrade -contains $item.Name) -and (Test-Path -LiteralPath $dest)) {
        $kept++
        continue
    }
    Copy-Item -LiteralPath $item.FullName -Destination $dest -Force
    $copied++
}

Write-Step ("Installed {0} file(s)." -f $copied)
if ($kept -gt 0) { Write-Step ("Kept {0} existing configuration file(s) untouched." -f $kept) }

# media\ and logs\ are the server's own working directories. They are never in
# the payload, so an upgrade leaves converted media and log history in place;
# this only makes them on a first install.
foreach ($d in @('media', 'logs')) {
    $p = Join-Path $TargetDir $d
    if (-not (Test-Path -LiteralPath $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null }
}

if (-not $NoShortcut) {
    try {
        # the real desktop, which may be redirected into OneDrive
        $desktop = [Environment]::GetFolderPath('Desktop')
        $lnk = Join-Path $desktop 'j0kers Media Server.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $s = $shell.CreateShortcut($lnk)
        $s.TargetPath       = $targetExe
        $s.Arguments        = '"server.json"'
        $s.WorkingDirectory = $TargetDir
        $s.Description      = 'j0kers Media Server'
        $s.Save()
        Write-Step ("Desktop shortcut: " + $lnk)
    }
    catch { Write-Step ("Could not create the desktop shortcut: " + $_.Exception.Message) }
}

Write-Host ''
Write-Host ($(if ($upgrade) { 'Upgraded.' } else { 'Installed.' })) -ForegroundColor Green
if ($upgrade) {
    Write-Host 'Your accounts, channels, library, settings and converted media were kept.'
} else {
    Write-Host 'On first run the dashboard opens at http://localhost:9090/ and asks you to'
    Write-Host 'create the administrator account.'
}
Write-Host ''

if (-not $Quiet) {
    $go = Read-Host 'Start the server now? [Y/n]'
    if (-not $go -or $go -match '^(y|yes)$') {
        Start-Process -FilePath $targetExe -ArgumentList 'server.json' -WorkingDirectory $TargetDir
        Write-Host 'Started.'
    }
}
