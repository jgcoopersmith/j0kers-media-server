<#
.SYNOPSIS
Stops a running j0kers Media Server the way its own Exit does, and forces it
only if that does not finish.

.DESCRIPTION
The post-commit hook stops the installed server so it can replace the exe.
It used to ask with CloseMainWindow and then insist with Stop-Process -Force,
and a server minimised to the tray has no window to close - so the insisting
was all that ever happened, and a forced stop runs no shutdown at all.

This sets the server's stop signal first (Services/StopSignal.cs: a named event
per process, Local\j0kers-media-server-stop-<pid>), which runs the same
shutdown as the tray's Exit. A build from before the signal existed has no such
event; for that, and only that, the old window close is tried. Whatever is
still running after -WaitSeconds is forced, and said so.

Prints one line per server: "stopped cleanly", "did not stop in Ns - forced",
or "could not be asked (a build without the stop signal) - forced".

.PARAMETER Path
Stop only servers running from this exe - the hook passes the installed one,
so a test server or a second copy elsewhere is left alone.

.PARAMETER Id
Stop exactly these process ids (the tests pass their own server's).

.PARAMETER WaitSeconds
How long a clean stop may take before it is forced. The server's own shutdown
gives up after about seven seconds (the tray's two, then a five-second
watchdog), so the default leaves room for a busy machine.
#>
param(
    [string]$Path,
    [int[]]$Id,
    [int]$WaitSeconds = 20
)

$ErrorActionPreference = 'Stop'

$servers = @(Get-Process j0kers-media-server -ErrorAction SilentlyContinue | Where-Object {
    if ($Id) { $Id -contains $_.Id }
    elseif ($Path) {
        try { [string]::Equals($_.Path, [IO.Path]::GetFullPath($Path), [StringComparison]::OrdinalIgnoreCase) }
        catch { $false }
    }
    else { $true }
})
if ($servers.Count -eq 0) { Write-Output 'no server running'; exit 0 }

$how = @{}
foreach ($p in $servers) {
    $how[$p.Id] = 'none'
    try {
        $signal = [System.Threading.EventWaitHandle]::OpenExisting("Local\j0kers-media-server-stop-$($p.Id)")
        try { [void]$signal.Set(); $how[$p.Id] = 'signal' } finally { $signal.Dispose() }
    } catch {
        # No such event: a build from before it existed. Its console window,
        # when it has one, is the only other polite way to ask.
        try { if ($p.MainWindowHandle -ne 0 -and $p.CloseMainWindow()) { $how[$p.Id] = 'window' } } catch { }
    }
}

$asked = @($servers | Where-Object { $how[$_.Id] -ne 'none' })
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline -and @($asked | Where-Object { -not $_.HasExited }).Count -gt 0) {
    Start-Sleep -Milliseconds 250
    foreach ($p in $asked) { $p.Refresh() }
}

$forced = $false
foreach ($p in $servers) {
    $p.Refresh()
    if ($p.HasExited) { Write-Output "server $($p.Id): stopped cleanly"; continue }
    if ($how[$p.Id] -eq 'none') { Write-Output "server $($p.Id): could not be asked (a build without the stop signal) - forced" }
    else { Write-Output "server $($p.Id): did not stop in ${WaitSeconds}s - forced" }
    try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { }
    $forced = $true
}
if ($forced) { exit 1 } else { exit 0 }
