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
$unreadable = [System.Collections.Generic.List[string]]::new()

# 'Stop' is right for the setup above - a missing media root should stop the
# run - and wrong for the loop below, which is the whole point of changing it
# here rather than at the top.
#
# Windows PowerShell turns anything a native program writes to stderr into an
# error record, and under 'Stop' that record is terminating. ffprobe writes to
# stderr for a segment it cannot read to the end - a conversion interrupted
# mid-write leaves exactly that - so the first damaged file in the library
# aborted the entire scan and reported nothing at all. Which is what happened:
# 817 conversions, and the run died on one truncated segment having checked
# only a handful.
#
# A file this cannot read is a result, not a catastrophe. It is counted and
# named at the end, and the scan carries on.
$ErrorActionPreference = 'Continue'

foreach ($d in $dirs) {
    $scanned++
    Write-Progress -Activity 'Checking conversion audio' -Status "$scanned of $($dirs.Count) - $($d.Name)" `
                   -PercentComplete ([int](100 * $scanned / $dirs.Count))

    $segs = @(Get-ChildItem -LiteralPath $d.FullName -File -ErrorAction SilentlyContinue |
              Where-Object { $_.Name -like 'seg_*.ts' -or $_.Name -like 'seg_*.m4s' } |
              Sort-Object Name)
    if ($segs.Count -eq 0) { $skipped++; continue }
    $seg = $segs[0]

    # No -probesize/-analyzeduration limit here on purpose: capping them makes
    # ffprobe answer "0" for the sample rate on these segments, which would
    # report every conversion as broken.
    #
    # Collected whole and indexed afterwards, rather than piped into
    # Select-Object -First 1: that closes the pipe while ffprobe is still
    # writing, which kills it and leaves $LASTEXITCODE at -1. The reading is
    # correct either way, but the script then exits non-zero on a completely
    # successful run, which is a lie to anything scripting against it.
    # stderr is NOT redirected here. Doing so is what wraps ffprobe's
    # complaints in error records; left alone they are just text on the
    # console, and only stdout is captured. -v error already keeps it quiet
    # unless something is genuinely wrong with the file.
    $out = @(& $Ffprobe -v error -select_streams a:0 -show_entries stream=sample_rate `
                        -of csv=p=0 $seg.FullName)
    $rate = if ($out.Count -gt 0) { "$($out[0])".Trim() } else { '' }

    # Segment zero is the least representative segment there is.
    #
    # A source whose audio is damaged at the very start - and DVD rips are
    # full of them - produces a first segment with no decodable audio frames
    # in it, which reads as rate 0 or as nothing at all, while every segment
    # after it is perfectly good. Judging the conversion by that one segment
    # condemned a film whose sound was fine from a few seconds in, and would
    # have gone on condemning it after every re-conversion, for ever.
    #
    # So a first segment that reads badly is not an answer, it is a reason to
    # look further in. One from the middle settles it.
    if (($rate -eq '' -or $rate -eq '0') -and $segs.Count -gt 1) {
        $mid = $segs[[int]($segs.Count / 2)]
        $out2 = @(& $Ffprobe -v error -select_streams a:0 -show_entries stream=sample_rate `
                             -of csv=p=0 $mid.FullName)
        if ($out2.Count -gt 0) { $rate = "$($out2[0])".Trim() }
    }

    # A reading that is not a number means the segment could not be read -
    # truncated, still being written, or damaged. That is worth naming
    # separately: it is not an audio-rate problem and re-converting for the
    # wrong reason wastes an hour of encoding.
    if ($rate -and $rate -notmatch '^\d+$') {
        $unreadable.Add($d.Name)
        continue
    }

    $source = ''
    $srcFile = Join-Path $d.FullName 'source.txt'
    if (Test-Path -LiteralPath $srcFile) { $source = (Get-Content -LiteralPath $srcFile -Raw).Trim() }

    if (-not $rate) {
        # A conversion with no audio is only worth reporting if the source
        # HAD some. Measured on this library: 114 conversions have no audio
        # and 112 of them are DVD structural VOBs - menus, logos, first-play
        # clips - whose sources are silent, so there was nothing to lose and
        # nothing to fix. Listing them buries the two that matter in a
        # hundred that do not, which is the same as not reporting them.
        #
        # The remaining two turned out to be silent as well: a declared AC3
        # track carrying 0 channels at 0 Hz, which is a stub, not audio. The
        # channel check is what tells those apart from a real track.
        if ($IncludeNoAudio -and $source -and (Test-Path -LiteralPath $source)) {
            $s = @(& $Ffprobe -v error -select_streams a:0 `
                              -show_entries stream=channels -of csv=p=0 $source)
            $ch = if ($s.Count -gt 0) { "$($s[0])".Trim() } else { '0' }
            if ($ch -match '^\d+$' -and [int]$ch -gt 0) {
                $bad.Add([PSCustomObject]@{ Rate = 'none'; Conversion = $d.Name; Source = $source })
            }
        }
        continue
    }
    if ($AcceptedRates -notcontains [int]$rate) {
        $bad.Add([PSCustomObject]@{ Rate = "$rate Hz"; Conversion = $d.Name; Source = $source })
    }
}
Write-Progress -Activity 'Checking conversion audio' -Completed

Write-Host ("scanned {0} conversion(s); {1} had no segments to read." -f $scanned, $skipped)
if ($unreadable.Count -gt 0) {
    Write-Host ''
    Write-Host ("{0} conversion(s) could not be read at all (truncated or damaged, not an audio problem):" -f $unreadable.Count)
    $unreadable | ForEach-Object { Write-Host "  $_" }
}
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
