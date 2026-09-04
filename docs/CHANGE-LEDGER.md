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

---

## 2026-09-01 — The log window could not be copied out of (v2.0.235 → v2.0.237)

**Reported:** the Log card's text could not be copied.

### Cause

`renderLog` rebuilt `#log`'s `innerHTML` on every 2-second poll. That destroys
every node a selection is anchored in, so a highlight survived at most two
seconds. The text was selectable; it just never lasted long enough to use.

### Changes

- `wwwroot/dashboard-log.js` — the render holds while a selection is inside the
  box and releases on `selectionchange`; a Copy button that takes the selection
  or every shown line, with the level word put back; the filter shared between
  the render and Copy so they cannot drift.
- `wwwroot/dashboard.html` — Copy button, a "⏸ selected" badge, `user-select:
  text` on `#log`, and `flex-wrap` on the card header, which already overflowed
  at 375px (three of six controls fitted) and would have put the new button off
  the edge.

### Verification

A throwaway page loading the **real** `dashboard-log.js` with the four helpers
it borrows stubbed, served over `python -m http.server`:

| Check | Result |
|---|---|
| selection across two polls | survived; `logRenderHeld` and the badge both set |
| releasing the selection | view caught up with no further poll |
| level change mid-selection | rendered (not swallowed) |
| Copy on `127.0.0.1` (secure) | `navigator.clipboard`, "Copied 6 lines" |
| Copy on `192.168.8.196` (insecure) | `execCommand(copy)=true`, "Copied 3 lines" |
| header at 375px | overflow 363px → 0 with wrap; desktop row unchanged |

The insecure-origin test is the one that mattered: this dashboard is reached at
`http://<lan-ip>:9090`, where `navigator.clipboard` does not exist.

### Live-system actions

- Two local HTTP servers, ports 8791 (loopback) and 8792 (**bound 0.0.0.0**,
  briefly reachable on the LAN, serving only copies of two dashboard scripts).
  Both stopped.
- **The system clipboard was overwritten twice** by real button clicks. Not
  recoverable; j0ker was notified by the tool at the time.
- Nothing on the server or under `G:\Archive` was read or written.

### Residue, and what leaked

The harness directory and both servers were removed. Two things did not go
cleanly:

- Three probe pages (`__dragprobe_9f3.html`, `__selcheck_review.html`,
  `__seltest_tmp.html`) were written into the **repository root** by review
  agents and swept into the commit by `git add -A`. Removed in `2ffe49e`.
- Eight browser tabs were left open on deleted files and only closed later,
  when the tab cap blocked other work.

Both are the same failure: `git add -A` and a shared working directory assume
nothing else is writing there.

---

## 2026-09-01 — 88% of the log was the dashboard polling itself (v2.0.238)

**Reported:** repeating `[access] … GET /api/sessions` lines, asked what they
were and then to stop showing them.

### Measurement

An open dashboard polls on timers: sessions every 2s, history 4s, channels 10s,
mounts/playlists/favourites 15s — 63 requests a minute per open window. With
three windows open, 165 access lines a minute. Over one hour: **6,738 access
lines against 930** that recorded something happening.

`/api/status` and `/api/log` were already skipped for exactly this reason. The
six were the same traffic and had never been added.

### The near-miss

The obvious change — add the six paths to the skip list — would have been
wrong, and silently. Five of them carry actions on the **identical path**:

    POST/DELETE /api/channels    POST/DELETE /api/mounts
    POST/DELETE /api/playlists   POST/DELETE /api/favorites
    DELETE /api/history

Nine real actions, the exact events this log exists to record, would have
stopped being written with nothing to indicate it. I had already told j0ker
those actions were on different paths, which was false and unverified.

The rule matches **method and path**. Extracted to `AccessLog.IsHeartbeat` so it
can be tested without an HttpListener; 25 tests pin both halves.

### Verification (live)

| | access lines/min |
|---|---|
| before, 3 windows | 165 |
| after, 1 window polling | **0** |

And the record still works, from the same log: `GET /api/log/files` and
`POST /api/server/closing` both still written.

---

## 2026-09-02 — A read-only account could not open the Media Library (v2.0.239)

**Reported, with a screenshot:** a passwordless guest saw the library folder
chip and got "cannot open: this account is read-only" on clicking it.

### Cause

`/api/browse` served two jobs through one door: *pick any path on this machine*
(an editor adding a library folder) and *walk the folders already shared* (the
Media Library card). It was gated at `Edit`, the level the first job needs.

The inconsistency was stark — `/api/play`, `/api/image` and the thumbnail route
were all `Read`, confined by `IsShared`. A guest was allowed to **play** a file
inside the library and not allowed to **find** one.

### Change

`Control/ControlApi.cs` — two doors. The drive list (no `path`) still requires
`Edit`; a path is `Read` and goes through `DenyUnshared`, the same rule play and
image already apply. `DenyUnshared` returns false for `Edit` and above, so an
operator is unaffected. `TranscodeScan` reuses `Browse` for its drive list and
now passes its caller through.

### Verification

159 tests pass. Confirmed working by j0ker in the running dashboard before I
could sign in as `guest` myself.

### Live-system actions

`users.json` was read to confirm a passwordless read-only account existed —
usernames, roles and the passwordless flag only, no hashes. Nothing written.

---


## 2026-09-02 — Two log lines nobody could act on (v2.0.241, v2.0.242)

### The URL-credential warning was the server scolding itself (v2.0.241)

**Reported:** asked what use this line is —
`credentials are arriving in URLs (?key=/?token=), 6365 so far`.

The warning throttles to one line per ten minutes, and the count climbed by
exactly **59 between consecutive lines** — 5.9 requests a minute, which is the
dashboard's liveness link reopening every 20.5s on each of two open pages.

`EventSource` cannot set an `Authorization` header; `openLiveLink` says so in
its own comment and puts the token in the query string because the browser
offers nothing else. So the server advised a change the caller cannot make,
about a request its own page makes, forever — **537 lines** across these logs.
That also buried the case the warning exists for: a third-party script really
putting a key in a URL would have been one line among hundreds.

`AuthService.CanSendAHeader` now exempts `/api/server/session` alone. The
credential is read and honoured on every path exactly as before; only the
logging changed. Eleven tests, including that lookalike paths do not inherit
the exemption — otherwise a caller could silence the warning by inventing one.
170 tests pass.

### The Holding Open / Stays Up pill (v2.0.242)

Removed on request: added in an earlier session without being asked for. The
markup and the block in `tick()` that painted it are gone. Nothing else read
`status.pagesOpen`, `stopsOnClose` or `pagesFrom`, so the API is unchanged —
other clients may want those fields.

### Live-system actions

Read-only: the log files, and `wwwroot` sources. Two server restarts from the
publish hook. Nothing under `G:\Archive` and no config touched.

---

## Across all of the above

- Every commit republishes and restarts the server, and each start honours
  `control.openDashboardOnStart` - so each one opens another dashboard window.
  Across these sessions the log records **23** of them; the count of open
  windows reached three before j0ker closed two, and each open window costs a
  full duplicate of the dashboard's polling. The hook is replacing a server
  that was already running and should not be opening a window at all.
  **Not fixed.**
- `[control] page opened from …` is written every 20.5s per open page — the
  liveness link doing its job, but at INFO and worded as though a new window
  appeared. **Not changed.**
- `/api/transcode/scan` is polled every ~6s by the Transcode panel and is still
  written to the access log. Same class as the six above. **Not changed.**

---

## 2026-09-03 — A television waited a minute for every folder of films (v2.0.244)

**Reported:** folders on the TV open instantly, media inside them takes over a
minute.

### Cause

The folders were the clue: a container needs no lookup, a file needs two. For
every file, `Item()` called `FullResTranscodeFor`, which

1. called the **blocking** `NeedsConversion` — an ffprobe launch, up to 20s, for
   any file the codec cache had not read; and
2. read **every** conversion's `source.txt` in turn until one matched.

| Measured on this install | |
|---|---|
| conversion folders | 2,904 |
| one full `source.txt` sweep | 1,321 ms cold, 127 ms warm |
| `Movies\Action` | 50 media files |
| scan cost for that one folder | **66 s cold**, 6.3 s warm |

The set gave up part way through the reply and dropped the connection — 20 ×
`HTTP 500` with `request failed: The specified network name is no longer
available` on 2026-09-03 — then retried, restarting the scan.

### Changes

- **`Media/VodIndex.cs`** (new) — source file → conversion, built once in the
  background at startup. Only a map of names: whether a conversion is finished
  and whole is still decided by reading its playlist and checking every segment
  at the moment of use, so a half-written or gap-toothed one still falls back to
  the original. Maintained by a directory **count** check (12 ms vs ~1 s to
  read), at most once per 10 s.
- **`FullResTranscodeFor`** — index lookup instead of the scan, and
  `NeedsConversionCached` instead of the blocking probe, so no unread file can
  stall a listing.
- **`DlnaService.ShouldList` / `NoteBrowsed`** — files a set cannot play with no
  conversion yet are held back, as asked; so are unread files, and browsing a
  folder now queues it for reading so "unknown" is short-lived. The filter runs
  **before paging**, or `NumberReturned`/`TotalMatches` would disagree with the
  rows and a set paging through would stop early.

### Verification (live, on the endpoint the TV actually calls)

| | |
|---|---|
| index build | 2,893 conversions in **177 ms**, background |
| 50 lookups in `Action` | **0.4 ms** (was 6.3 s warm / 66 s cold) |
| `POST /dlna/control` root | 200 in **5 ms** |
| browse `Movies` | 11 ms, 9 containers |
| browse `Movies\Action` | **116 ms**, 317 entries, 129 KB |
| held back there | 2 `.mkv` with no conversion, one of them HEVC/x265 |
| held back library-wide | **11 of 5,366 files (0.2%)** |

178 tests pass, 16 of them new. Quality rule tested rather than assumed: a
scaled copy is never offered, and where both exist the full-resolution one wins
whatever order the folders are walked in.

### The probe cache, asked for in the same round

It had reached **14 MB / 114,643 entries** for a library of 5,366 files. The
cause was not staleness — a first attempt at a generic prune dropped only 15%
and cost **28 seconds** of file stats. Looking at what was actually in it:

| paths | entries |
|---|---|
| `G:\Archive\Transcoded` (this server's own HLS segments) | 108,084 |
| `G:\Archive\Movies` (real library files) | 6,482 |

Nothing ever asks whether a television can play `seg_00417.ts`, but the
transcode panel can be pointed at the conversions folder and that queued every
segment for probing. So `TvCodecs` now knows the media root, never probes or
caches anything under it, and drops those entries **without a stat**.

**114,643 → 3,802 entries. 14 MB → 613 KB. 0.3 s instead of 28.4 s.**

### Live-system actions

Read-only: logs, the probe cache (worked on a **copy** in scratch, never the
installed file), the conversions folder, the library. One `POST /dlna/control`
against the running server — a read. Two server restarts from the publish hook.
Nothing under `G:\Archive` written.
