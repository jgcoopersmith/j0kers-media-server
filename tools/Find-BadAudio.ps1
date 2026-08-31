<#
.SYNOPSIS
    Lists conversions whose audio a television will refuse to play.

.DESCRIPTION
    Before 2.0.211 the converter kept whatever sample rate the source had, so
    a 16kHz or 24kHz original became a 16kHz or 24kHz AAC stream. Those files
    are not damaged and nothing failed while making them - they play correctly
    in a browser - but a television's audio decoder handles the broadcast
    rates and little else, so it reports the audio as unrecognisable and plays
    the film silent.

    Nothing in the server's log marks them, because from ffmpeg's point of
    view every one of those conversions succeeded. The only way to find them
    is to look at what came out, which is what this does: it reads the first
    segment of every conversion under the media root and reports the ones
    whose audio is at a rate a TV will not take.

    It reads only. Nothing is deleted, re-queued or changed.

    Expect it to be slow the first time - a few seconds per conversion, most
    of it waiting for a cold disk rather than for ffprobe. Progress is shown,
    and Ctrl+C is safe at any point.

.PARAMETER MediaRoot
    Where the vod-* directories are. Read from the server's own settings by
    default, so normally you do not pass this.

.PARAMETER OutFile
    Also write the SOURCE path of each affected conversion, one per line, to
    this file. That is the list to re-convert: pasting a source path back into
    the dashboard's converter is what replaces the bad copy.

.PARAMETER IncludeNoAudio
    Also report conversions with no audio track at all. Off by default,
    because the usual cause is a source that genuinely has none - DVD menu
    VOBs (VIDEO_TS.VOB, VTS_nn_0.VOB) are the common case and are not a fault.

.EXAMPLE
    .\Find-BadAudio.ps1

.EXAMPLE
    .\Find-BadAudio.ps1 -OutFile bad-audio.txt
    Writes the source paths so they can be re-converted.
#>
[CmdletBinding()]
param(
    [string] $MediaRoot,
    [string] $Ffprobe,
    [int[]]  $AcceptedRates = @(44100, 48000),
    [string] $OutFile,
    [switch] $IncludeNoAudio
)

$ErrorActionPreference = 'Stop'
$install = Join-Path $env:LOCALAPPDATA 'Programs\j0kers Media Server'

# The media root the server is actually using. settings.json (written by the
# dashboard) overrides server.json, exactly as the server itself resolves it -
# getting that order wrong would scan a directory nobody converts into.
if (-not $MediaRoot) {
    $settings = Join-Path $install 'settings.json'
    if (Test-Path $settings) {
        $s = Get-Content $settings -Raw | ConvertFrom-Json
        if ($s.mediaRoot) { $MediaRoot = $s.mediaRoot }
    }
    if (-not $MediaRoot) { $MediaRoot = Join-Path $install 'media' }
}
if (-not $Ffprobe) { $Ffprobe = Join-Path $install 'ffprobe.exe' }

if (-not (Test-Path -LiteralPath $MediaRoot)) { throw "media root not found: $MediaRoot" }
if (-not (Test-Path -LiteralPath $Ffprobe))   { throw "ffprobe not found: $Ffprobe" }

Write-Host ''
Write-Host "media root : $MediaRoot"
Write-Host ("accepted   : " + ($AcceptedRates -join ', ') + ' Hz')
Write-Host ''

$dirs = @(Get-ChildItem -LiteralPath $MediaRoot -Directory -Filter 'vod-*' -ErrorAction SilentlyContinue)
if ($dirs.Count -eq 0) { Write-Host 'no conversions found.'; return }

$bad = [System.Collections.Generic.List[object]]::new()
$scanned = 0
$skipped = 0

foreach ($d in $dirs) {
    $scanned++
    Write-Progress -Activity 'Checking conversion audio' -Status "$scanned of $($dirs.Count) - $($d.Name)" `
                   -PercentComplete ([int](100 * $scanned / $dirs.Count))

    # The first segment carries the same encoder settings as every other one,
    # so there is no reason to read more than one.
    $seg = Get-ChildItem -LiteralPath $d.FullName -File -ErrorAction SilentlyContinue |
           Where-Object { $_.Name -like 'seg_*.ts' -or $_.Name -like 'seg_*.m4s' } |
           Select-Object -First 1
    if (-not $seg) { $skipped++; continue }

    # No -probesize/-analyzeduration limit here on purpose: capping them makes
    # ffprobe answer "0" for the sample rate on these segments, which would
    # report every conversion as broken.
    #
    # Collected whole and indexed afterwards, rather than piped into
    # Select-Object -First 1: that closes the pipe while ffprobe is still
    # writing, which kills it and leaves $LASTEXITCODE at -1. The reading is
    # correct either way, but the script then exits non-zero on a completely
    # successful run, which is a lie to anything scripting against it.
    $out = @(& $Ffprobe -v error -select_streams a:0 -show_entries stream=sample_rate `
                        -of csv=p=0 $seg.FullName 2>$null)
    $rate = if ($out.Count -gt 0) { "$($out[0])".Trim() } else { '' }

    $source = ''
    $srcFile = Join-Path $d.FullName 'source.txt'
    if (Test-Path -LiteralPath $srcFile) { $source = (Get-Content -LiteralPath $srcFile -Raw).Trim() }

    if (-not $rate) {
        if ($IncludeNoAudio) {
            $bad.Add([PSCustomObject]@{ Rate = 'none'; Conversion = $d.Name; Source = $source })
        }
        continue
    }
    if ($AcceptedRates -notcontains [int]$rate) {
        $bad.Add([PSCustomObject]@{ Rate = "$rate Hz"; Conversion = $d.Name; Source = $source })
    }
}
Write-Progress -Activity 'Checking conversion audio' -Completed

Write-Host ("scanned {0} conversion(s); {1} had no segments to read." -f $scanned, $skipped)
Write-Host ''

if ($bad.Count -eq 0) {
    Write-Host 'Every conversion has a sample rate a television will accept.' -ForegroundColor Green
    return
}

Write-Host ("{0} conversion(s) will be silent on a TV:" -f $bad.Count) -ForegroundColor Yellow
Write-Host ''
$bad | Sort-Object Rate, Conversion | Format-Table -AutoSize -Property Rate, Conversion

if ($OutFile) {
    $sources = $bad | Where-Object { $_.Source } | Select-Object -ExpandProperty Source
    $sources | Set-Content -LiteralPath $OutFile -Encoding utf8
    Write-Host ("source paths written to {0} ({1} of {2} had a recorded source)" -f `
                $OutFile, @($sources).Count, $bad.Count)
}

Write-Host ''
Write-Host 'These were made before 2.0.211 and keep the rate they were made with.'
Write-Host 'Converting one again on this version replaces it with a 48kHz copy.'

exit 0
