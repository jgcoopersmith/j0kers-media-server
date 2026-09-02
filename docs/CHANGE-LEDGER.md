# Change ledger

An audit trail of work sessions on this server: what was changed, what was run
against the live install, and what was cleaned up afterwards. Git history says
what the code became; this says what was *done to the machine* along the way —
which processes were inspected, which files were read or written outside the
repository, and what the verification actually proved.

One section per session, newest last.

---

## 2026-09-01 — "Transcode selected" did nothing (v2.0.233 → v2.0.234)

**Reported:** selecting `G:\Archive\Movies\Comedy` in the Transcode panel and
pressing *Transcode selected* had no visible effect.

### Investigation (read-only)

| What was inspected | Finding |
|---|---|
| `logs/j0kers.log`, 20:15:59 | `transcode: 0 file(s) queued from 1 selection(s) (717 video file(s) found; the remaining 705 are being added)` — then nothing. No follow-up line, no error, for the next 20 minutes. |
| `transcode-queue.json` | `{"MaxParallel":12,"StaggerSeconds":15,"Waiting":[]}` — last written 19:17, an hour before the press. Nothing was ever queued. |
| Running processes | Three `ffprobe.exe` alive: two started 18:11:42, one at 20:15:59 — the exact second of the button press. |
| Their command lines (`Win32_Process`) | All three on Comedy/TenaciousD files; the 20:15:59 one on the same file that had already wedged two probes at 18:11:42. |
| `G:\Archive\Movies\Comedy` file census | 717 files by the server's own extension list (311 mp4, 256 vob, 93 avi, 37 mkv, 10 mpeg, 6 mpg, 2 wmv, 2 mov) — matching the log exactly, confirming the selection. |
| Conversion status of those 717 (script replicating `FfmpegManager.VodStreamName`) | 64 already converted, 653 not — of which **266 have containers that force conversion** (`.vob` / `.mkv`) and so could never have been skipped legitimately. The batch was not "nothing to do". |
| `ffprobe` run by hand on the wedged file | Exits 0 and reports `h264/aac` — but writes **39,273 bytes to stderr** despite `-v error` (`Invalid NAL unit size`, `Error splitting the input into NAL units`, …). |

### Root cause

`TvCodecs.Probe` and five siblings shared this shape:

```csharp
var output = p.StandardOutput.ReadToEnd();
p.WaitForExit(20_000);
```

Both pipes were redirected, only stdout was read. A redirected pipe is a kernel
buffer of a few kilobytes; ffprobe filled stderr, blocked in its own write, and
never exited or closed stdout. `ReadToEnd` has no timeout, so it waited on a
close that could not come — and the `WaitForExit` timeout on the next line was
unreachable. It had never once fired.

The batch walk hit that file thirteen entries in and stopped there for ever, so
the 266 files that genuinely needed converting were never looked at, and the
task that would have reported the outcome never reached its log line.

### Changes (commit `9b1e66a`, v2.0.234)

- `Services/ProcessJob.cs` — new `Run(psi, timeoutMs)`: both pipes drained
  concurrently and *before* the wait, a timeout that can fire, `Kill(tree)` when
  it does, and stdin closed so a child cannot block on input either.
- `Media/TvCodecs.cs`, `Media/FfmpegManager.cs` (×2), `Media/SubtitleManager.cs`,
  `Services/SecretFile.cs` — converted to it. A probe that times out is now
  logged instead of vanishing.
- `Control/ControlApi.cs` — the batch tail always reports what it queued,
  including "0 more", with the count read and elapsed time. The silent version
  was indistinguishable from the task never finishing.
- `tests/J0kersMediaServer.Tests/ProcessRunTests.cs` — three regression tests:
  a child that floods the unread pipe, a child that never exits, and the
  ordinary case.
- The two sites that redirect only stderr were never at risk and are unchanged.

### Verification

| Check | Result |
|---|---|
| Full test suite | 134 passed, 0 failed |
| Shipped `ProcessJob.Run` vs the real ffprobe and the real wedged file | returned in **0.05 s**, `h264,video / aac,audio`, all 39,273 stderr bytes drained, **0** leftover ffprobe processes |
| Live server after upgrade | `20:33:48 [probe] codec prefetch: read 20 file(s) the transcode list was unsure about` — the probe round that used to wedge now completes |
| Orphaned ffprobes | all three gone (killed with the old process via the job object) |
| Installed binary | 2.0.234, from commit `9b1e66a` |
| Desktop shortcut | re-read after writing: target, arguments, working directory and version all match the install |

### Live-system actions taken

- **Read only:** logs, `transcode-queue.json`, `probe-cache.json`, `server.json`,
  `settings.json`, `sessions.json`, process list and command lines, and the
  Comedy tree (metadata only — no media file was opened for writing).
- **Written:** nothing under `G:\Archive`. No conversion was started, no queue
  was modified, no media was touched.
- **Server restarted once** by the repo's own post-commit hook, which found it
  idle (no media served in the preceding two minutes).
- **Desktop shortcut** re-stamped to 2.0.234 and read back to confirm.

### Test residue and cleanup

Created and then removed: a scratch `probecheck` console project and its build
output, the status-check and patch scripts, and captured ffprobe output — all
under the session scratchpad, now empty. No `j0kers-tests-*` directories were
left behind.

A `Stop` hook was added at `G:\Claude\.claude\hooks\clean-test-residue.sh` so
this is no longer a thing to remember: on every stop it removes leftover
`j0kers-tests-*` temp directories, clears the session scratchpad, and sweeps
`*.inuse` executables the post-commit hook leaves in the install between
commits. It touches nothing in the repository, the install's config, or
converted media.

### Still true, not changed

`publish/j0kers-media-server.exe` in the repository is a stale 2.0.205 build
from 30 Aug. Nothing launches it — the desktop shortcut and the running server
both use the install under `%LOCALAPPDATA%` — so it is leftover scratch, not a
second server waiting to be started by accident.
