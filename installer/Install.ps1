# j0kers Media Server - installer.
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

# What an upgrade is allowed to replace: the program, and nothing else.
#
# This is deliberately the opposite way round from listing the config files to
# keep. That list has to be updated every time the server learns to save
# something new, and the day it is not, an upgrade quietly overwrites the file
# nobody remembered - shelf.json was already missing from it. Naming the
# program files instead cannot go stale: anything else already in the folder
# belongs to the running server and is left exactly as it is.
$ProgramFiles = @('j0kers-media-server.exe', 'ffmpeg.exe', 'ffprobe.exe')

function Write-Step($text) { Write-Host "  $text" }

Write-Host ''
Write-Host 'j0kers Media Server - install' -ForegroundColor Cyan
Write-Host ''

if (-not (Test-Path -LiteralPath $payload)) {
    throw "payload folder not found next to this script ($payload). Unpack the whole package and run Install.cmd from inside it."
}

$exeName  = 'j0kers-media-server.exe'

# What makes a folder somebody's server, rather than an empty directory.
#
# Used in two places that must agree: finding an existing install below, and
# deciding upgrade-vs-new further down. They did not agree before — the search
# threw away any folder whose exe was missing, while the upgrade test knew
# perfectly well that a folder full of accounts and channels is an install
# whether or not the program is still sitting in it. So a quarantined exe sent
# the search home empty, the script installed a fresh server at the default
# path, and repointed the desktop shortcut at it. Nothing was deleted; the
# accounts were simply somewhere the server no longer looked.
$DataNames = @('users.json', 'server.json', 'channels.json', 'signing.key',
               'settings.json', 'library.json', 'providers.json')

function Test-InstallHere($dir) {
    if (-not $dir -or -not (Test-Path -LiteralPath $dir)) { return $false }
    if (Test-Path -LiteralPath (Join-Path $dir $exeName)) { return $true }
    foreach ($n in $DataNames) { if (Test-Path -LiteralPath (Join-Path $dir $n)) { return $true } }
    return $false
}

# Find a copy already on this machine when the default target has none. An
# earlier portable build was unpacked wherever the user chose, so installing
# blindly into the default would make a second, empty install beside the real
# one - new program, none of the accounts, channels or media. Prefer the
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
    # A shortcut records where the program was, and that record outlives the
    # program — so a folder named by one still counts even with the exe gone.
    $found = @($found | Where-Object { Test-InstallHere $_ } | Select-Object -Unique)

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
        # More than one copy on this machine - a build directory beside a real
        # install, say. Picking one unprompted upgrades something the user did
        # not mean to touch, so choose deliberately or not at all.
        Write-Host 'More than one installation was found:' -ForegroundColor Yellow
        for ($i = 0; $i -lt $found.Count; $i++) { Write-Host ("  [{0}] {1}" -f ($i + 1), $found[$i]) }
        Write-Host ("  [0] none of these - install to {0}" -f $TargetDir)
        Write-Host ''
        # Not choosing must not mean "install a new one anyway".
        #
        # Both of these used to fall straight through with $TargetDir still at
        # the default, which installed a complete new server there and then
        # repointed the desktop shortcut at it — so the answer "I don't know
        # which" produced a server with no accounts and an icon that opened it.
        # Refusing costs one flag; guessing costs the account list.
        if ($Quiet) {
            Write-Host 'Ambiguous, and running unattended: nothing was changed.' -ForegroundColor Yellow
            Write-Host 'Re-run naming the one you mean, or a new folder for a fresh install:'
            Write-Host '  Install.cmd -TargetDir "<the folder you mean>"'
            Write-Host ''
            return
        }
        $ans = Read-Host 'Which one should be upgraded?'
        if ($ans -match '^\d+$' -and [int]$ans -ge 1 -and [int]$ans -le $found.Count) {
            $TargetDir = $found[[int]$ans - 1]
        }
        elseif ($ans -eq '0') {
            Write-Host ('Installing fresh to ' + $TargetDir) -ForegroundColor Yellow
        }
        else {
            Write-Host 'Nothing chosen - nothing was changed.' -ForegroundColor Yellow
            Write-Host ''
            return
        }
    }
    elseif (Test-InstallHere $TargetDir) {
        # The default folder holds data but no program — the quarantined-exe
        # case. Upgrade it rather than treating it as bare ground.
        Write-Host ('An installation with no program file is here:  ' + $TargetDir) -ForegroundColor Yellow
        Write-Host 'Its accounts and settings are being kept.'
        Write-Host ''
    }
}

$targetExe = Join-Path $TargetDir $exeName

# Is there an install here already? Asked of the DATA, not of the program.
#
# This used to be Test-Path $targetExe alone, and that is the wrong question,
# because the exe is the one file in the folder that can go missing on its own.
# A virus scanner quarantines it; somebody deletes it to force a clean install;
# or - worst, because this script causes it - Copy-Payload renames a locked exe
# aside and then fails to write the new one, leaving the folder with all of the
# user's data and no program.
#
# In every one of those the next run called itself a "new install" and copied
# the payload's defaults straight over the real server.json and providers.json.
# Measured: an install holding accounts, channels, a signing key and converted
# media was reported as "new install", and its configuration - server name,
# ports, TLS, library roots - was replaced with the shipped defaults.
#
# Any of these means somebody's server has lived here, whether or not the
# program is still present.
$dataHere = @('users.json', 'server.json', 'channels.json', 'signing.key',
              'settings.json', 'library.json', 'providers.json') |
            Where-Object { Test-Path -LiteralPath (Join-Path $TargetDir $_) }
$exeHere  = Test-Path -LiteralPath $targetExe
$upgrade  = $exeHere -or $dataHere.Count -gt 0

Write-Host ("Target:  " + $TargetDir)
Write-Host ("Mode:    " + $(if ($upgrade) { 'upgrade - existing settings and data are kept' } else { 'new install' }))
if ($upgrade -and -not $exeHere) {
    Write-Host ''
    Write-Host ('  Note: the program was missing but ' + $dataHere.Count +
                ' data file(s) are here, so this is an upgrade, not a new install.') -ForegroundColor Yellow
    Write-Host '  Your accounts and settings are being kept.' -ForegroundColor Yellow
}
Write-Host ''

if (-not $Quiet) {
    $answer = Read-Host 'Continue? [Y/n]'
    if ($answer -and $answer -notmatch '^(y|yes)$') { Write-Host 'Cancelled.'; return }
}

# A running server holds its own exe open, so it has to stop before the file
# can be replaced. Only instances running from *this* target are touched - a
# server installed elsewhere on the same machine is left alone.
$running = @(Get-CimInstance Win32_Process -Filter "Name='$exeName'" -ErrorAction SilentlyContinue |
             Where-Object { $_.ExecutablePath -and (Split-Path -Parent $_.ExecutablePath) -eq $TargetDir.TrimEnd('\') })
if ($running.Count -gt 0) {
    # Name what is actually running. "Stopping the running server" when the
    # person is sure they shut it down is confusing rather than informative:
    # the process id and path say which copy is still up, and whether it is a
    # tray icon still sitting there or something that relaunched itself.
    Write-Step "Stopping the running server:"
    foreach ($p in $running) { Write-Step ("    pid " + $p.ProcessId + "  " + $p.ExecutablePath) }
    foreach ($p in $running) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    # Stop-Process asks; it does not wait. Windows releases the file handle
    # when the process has actually gone, which is a moment later - long
    # enough that copying straight afterwards failed with "being used by
    # another process". Wait for each one to really exit.
    foreach ($p in $running) {
        for ($i = 0; $i -lt 100; $i++) {
            if (-not (Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 100
        }
    }
    Start-Sleep -Milliseconds 300
}

New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

# Clear out anything a previous upgrade had to rename aside (see below).
foreach ($old in Get-ChildItem -LiteralPath $TargetDir -Filter '*.replaced-*' -ErrorAction SilentlyContinue) {
    try { Remove-Item -LiteralPath $old.FullName -Force -ErrorAction Stop } catch { }
}

# The accounts, copied aside before anything else is touched.
#
# Nothing below is supposed to be able to harm them - users.json is not in the
# payload, and the copy loop now refuses to overwrite any existing data file.
# This is the belt to that pair of braces: accounts are the one thing here that
# cannot be rebuilt from the media on disk. Everything else an upgrade could
# spoil is a preference; a lost account list is somebody locked out of their
# own server with no way back in.
#
# One fixed name, so it is the state before the last upgrade rather than a pile
# that grows with every install.
$usersFile = Join-Path $TargetDir 'users.json'
if (Test-Path -LiteralPath $usersFile) {
    try {
        Copy-Item -LiteralPath $usersFile -Destination "$usersFile.previous" -Force -ErrorAction Stop
        Write-Step 'Accounts backed up to users.json.previous before upgrading.'
    }
    catch { Write-Step ('Could not back up users.json: ' + $_.Exception.Message) }
}

<#
  Copies one payload file over whatever is there, and copes with the file
  still being locked.

  Even after the server has gone, a virus scanner or Explorer can hold a
  freshly-closed executable open for a second or two. Retry for a while, and
  if it is still locked, rename the old file out of the way instead: Windows
  allows renaming a running or open executable even when it refuses to
  overwrite it, so the new one can be put in place regardless. The renamed
  file is deleted by the next upgrade.
#>
function Copy-Payload($source, $dest) {
    for ($i = 0; $i -lt 25; $i++) {
        try { Copy-Item -LiteralPath $source -Destination $dest -Force -ErrorAction Stop; return $true }
        catch { Start-Sleep -Milliseconds 400 }
    }
    $aside = $null
    try {
        if (Test-Path -LiteralPath $dest) {
            $aside = "$dest.replaced-" + (Get-Random)
            Move-Item -LiteralPath $dest -Destination $aside -Force -ErrorAction Stop
        }
        Copy-Item -LiteralPath $source -Destination $dest -Force -ErrorAction Stop
        return $true
    }
    catch {
        # Put the old one back. Renaming aside and then failing to write the
        # replacement left the folder with no program at all beside a full set
        # of the user's data - and the next run of this script read that as a
        # new install and copied its defaults over the lot. A failed upgrade
        # must leave the previous version running, not a hole.
        if ($aside -and (Test-Path -LiteralPath $aside) -and -not (Test-Path -LiteralPath $dest)) {
            try {
                Move-Item -LiteralPath $aside -Destination $dest -Force -ErrorAction Stop
                Write-Step ("Restored the previous " + (Split-Path -Leaf $dest) + " after a failed replace.")
            }
            catch { }
        }
        Write-Step ("Could not replace " + (Split-Path -Leaf $dest) + ": " + $_.Exception.Message)
        return $false
    }
}

$copied = 0; $kept = 0; $failed = 0
foreach ($item in Get-ChildItem -LiteralPath $payload -File) {
    $dest = Join-Path $TargetDir $item.Name
    # Not the program, and already there: it belongs to the running server.
    #
    # This no longer asks whether we decided this was an upgrade. The payload's
    # server.json and providers.json are SEED files - they exist to give a
    # first install something to start from, and there is no circumstance in
    # which writing one over a file somebody's server is already using is
    # right. Seed if absent, never replace. That was the whole of the bug
    # above: one misjudged $upgrade and a real configuration was gone.
    if (($ProgramFiles -notcontains $item.Name) -and (Test-Path -LiteralPath $dest)) {
        $kept++
        continue
    }
    if (Copy-Payload $item.FullName $dest) { $copied++ } else { $failed++ }
}

Write-Step ("Installed {0} file(s)." -f $copied)
if ($kept -gt 0) { Write-Step ("Kept {0} existing configuration file(s) untouched." -f $kept) }
if ($failed -gt 0) { throw "$failed file(s) could not be replaced - close anything using the server and run this again." }

# media\ and logs\ are the server's own working directories. They are never in
# the payload, so an upgrade leaves converted media and log history in place;
# this only makes them on a first install.
foreach ($d in @('media', 'logs')) {
    $p = Join-Path $TargetDir $d
    if (-not (Test-Path -LiteralPath $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null }
}

if (-not $NoShortcut) {
    # Refresh every shortcut that already exists, and only create one if none
    # does.
    #
    # This wrote a single fixed path on the desktop root and nothing else,
    # which is not where the shortcut people actually use ends up. Moving it
    # into a folder - "j0ker Dev" here - left the installer writing a fresh
    # one at the root on every upgrade while the moved copy was never touched
    # again. It kept working only because an in-place upgrade does not change
    # the target path; the day the install moves, the icon somebody clicks
    # points at nothing, and there is a stray duplicate at the root besides.
    #
    # So: update what is there, wherever it is, and add one only when the user
    # has none. Nothing is created beside a shortcut they have already put
    # where they want it, and nothing they rely on goes stale.
    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnkName = 'j0kers Media Server.lnk'
    $places  = @($desktop, (Join-Path $desktop 'j0ker Dev'))

    $existing = @()
    foreach ($d in $places) {
        if (-not (Test-Path -LiteralPath $d)) { continue }
        $p = Join-Path $d $lnkName
        if (Test-Path -LiteralPath $p) { $existing += $p }
    }
    # None anywhere: put one on the desktop, as before.
    if ($existing.Count -eq 0) { $existing = @(Join-Path $desktop $lnkName) }

    $shell = New-Object -ComObject WScript.Shell
    foreach ($lnk in $existing) {
        try {
            $s = $shell.CreateShortcut($lnk)
            $s.TargetPath       = $targetExe
            $s.Arguments        = '"server.json"'
            $s.WorkingDirectory = $TargetDir
            $s.Description      = 'j0kers Media Server'
            $s.Save()
            Write-Step ("Shortcut updated: " + $lnk)
        }
        catch { Write-Step ("Could not write " + $lnk + ": " + $_.Exception.Message) }
    }
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
