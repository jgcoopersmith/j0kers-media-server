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

---

## 2026-09-03 — A paused film resumed in four colours (v2.0.246)

**Reported:** pause a film on the TV over DLNA and press play again; the video
looks like four colours only, but it plays and the audio is fine.

### What it was not

Two hypotheses ruled out by measurement before touching anything:

- **Not the byte arithmetic.** A mid-file range from the live server came back
  byte-for-byte identical to the same offset read from disk, with a correct
  `Content-Range: bytes 99737384-99745575/199474768`.
- **Not ffmpeg being interrupted** (the owner's own guess). No ffmpeg runs
  during playback of a finished conversion at all — the log shows only the
  `/dlna/file` requests — because it is static segments handed over as one file.
- **Not missing parameter sets in the file.** A whole segment decoded from its
  start reports `h264 720x576 yuv420p` with no complaint.

### Cause

Where the set resumes. Each HLS segment opens with the H.264 parameter sets and
a keyframe and nothing repeats them in between, so an offset landing mid-segment
hands the decoder a picture it has no instructions for. It does not fail — it
decodes anyway and paints the result.

Measured with ffmpeg against the running server, same conversion:

| resume offset | result |
|---|---|
| 98,094,264 (segment start) | decodes silently |
| 98,523,656 (mid-segment) | `non-existing PPS 0 referenced`, `no frame!` |

### Change

`DlnaService.ServeTranscode` — a partial request starts at the beginning of the
segment it falls in. `Content-Range` reports the real start, so every byte
offset stays truthful and seeking still works. The rewind is bounded by one
segment.

### Verification (live)

The identical mid-segment request that produced the decoder errors now returns
`Content-Range: bytes 98094264-101523656/199474768` — a rewind of 429,392 bytes,
roughly three seconds — and decodes **clean**. 16 tests pin the arithmetic:
never forward, never before the start, never more than one segment, and an
offset already on a boundary is left exactly alone.

**Quality is unchanged** — the same bytes, starting slightly earlier.

### Live-system actions

Read-only: the log, one conversion's segments, and `GET /dlna/file` range
requests against the running server. One server restart from the publish hook.
Nothing written under `G:\Archive`.

---

## 2026-09-03 — Chasing the paused resume to its end (v2.0.248 → v2.0.250)

The v2.0.246 fix did not solve the reported case, and finding out why took two
instrumented builds and ended in a wall. Recorded because the dead ends are the
valuable part: four different fixes were proposed and none of them would have
worked.

### Why the earlier fix missed

The film was `Blade.mp4` — identified from `history.json` rather than by asking.
H.264/AAC in MP4, **no conversion exists**, so it is served as the original.
The v2.0.246 snapping only touches the conversion path. Different code, never ran.

### What an MP4 resume actually faces

| box | at | size |
|---|---|---|
| `ftyp` | 0 | 20 |
| `moov` | 20 | 3,337,629 — every parameter set lives here |
| `mdat` | 3,337,649 | 2.1 GB — raw frames, nothing else |

4 MB pulled from the exact offset the TV resumed at, handed to ffmpeg:
`moov atom not found`. In MP4 the parameter sets are in the header and are
**never repeated**, so no byte offset can carry them. Unlike MPEG-TS, there is
nothing to snap to.

"Just hold the header" does not work either: the `moov` carries **31,445
absolute chunk offsets** (first = 3,337,657) describing the whole file. Prepended
to a mid-file range it would point the set at positions not in the stream —
confidently wrong rather than obviously broken. Making it usable means rewriting
every offset, which is writing an MP4 muxer.

### The two findings that closed it (v2.0.249, instrumentation)

Every request header was logged for one viewing. The set sends **four**:

    Accept: */*
    Host: 192.168.8.196:9090
    Range: bytes=0-
    User-Agent: Mozilla/5.0 ... Chrome/39.0.2171.95 Safari/537.36

No `TimeSeekRange.dlna.org`, no `getcontentFeatures.dlna.org`, no
`transferMode.dlna.org`. It is a Chromium media element doing plain HTTP byte
ranges, not a DLNA renderer — so advertising time-seek would have been
advertising to something that cannot hear it.

And the decisive one:

    22:16:50.744  Range: bytes=0-            reads the header
    22:16:51.683  Range: bytes=1140711639-   seeks to the saved position
    22:17:33.541  response completes - 962.59 MB delivered
                  (nothing further)

962.59 MB is exactly 2,150,064,454 − 1,140,711,639: the whole remainder of the
film, in 42 seconds. **The set then plays from its own memory and never contacts
the server again.** Pause and resume happen with every byte already in hand, so
no server-side change — remux, header rewriting, keyframe snapping, time-seek —
can reach the fault. It is a decoder teardown bug in the television.

There is no AVTransport traffic either: pause is invisible here by protocol as
well as by timing.

### Abandoned, deliberately

Capping open-ended ranges so the set streams instead of bulk-downloading was
started and **stopped by the owner as a rabbit hole** — correctly: it would have
made the set talk to the server more often while buffering, without making a
pause visible. No code from it was kept; the edit was rejected before it was
written.

### v2.0.250 — putting the scaffolding away

The per-header dump was diagnostic and should have come out when the experiment
ended rather than staying at Info in a live log. Removed. Kept: one line per
`/dlna/file` naming the title, which of the two paths served it, and the Range —
which the access log cannot say, because it drops query strings on purpose.

### Live-system actions

Read-only throughout: the log, `history.json`, one film's MP4 boxes, and ranged
GETs against the running server. Three restarts from the publish hook. Nothing
written under `G:\Archive`. The owner reset the log level to trace themselves.

### Left standing

The v2.0.244 browse fix (66 s → 116 ms) and the v2.0.246 resume snapping are
real and unaffected — the latter works for files served as conversions, which
`Blade.mp4` is not. The owner's workaround stands: exit to the menu and restart,
which resumes at position.

---

## 2026-09-04 — RTSP mounts and Live channels are administrators-only (v2.0.251)

**Asked for:** hide the RTSP mounts and Live channels cards unless you are an
admin.

### Done, and why it is more than CSS

Both cards carry `admin-only`, which the page already understood. But hiding a
card makes the page honest, not the server — a read account could still fetch:

| endpoint | what it returns |
|---|---|
| `GET /api/mounts` | each mount's **source path on this machine** |
| `GET /api/channels` | each channel's **URL**, which for an IPTV provider routinely carries credentials |

Checked before claiming it: the six channels configured here have no
credentials in their URLs today (counted by shape, values never printed), so
this was clutter rather than an active leak — but the endpoint would carry them
for anyone who adds an IPTV source.

So both paths are now `AccessLevel.Admin`, for **every method** rather than only
the reads. An account that cannot see the card has no business adding a mount,
and a GET gated above a POST on the same path is a rule nobody can reason about.

The dashboard also stops polling both unless the account can see them, so a read
account is not collecting a 403 every fifteen seconds for a card that is not on
its page.

### Verification

Signed in as the real passwordless `guest` account (role `read`) against the
running server:

| endpoint | result |
|---|---|
| `/api/mounts` | **403** |
| `/api/channels` | **403** |
| `/api/status` | 200 |
| `/api/library` | 200 |
| `/api/favorites` | 200 |

202 tests pass.

### Test residue

The sign-in created a `guest` session in `sessions.json`. `POST /api/auth/logout`
returned 411 (it wants a body) and the cookie jar had already been removed, so it
was cleared the way the server clears every session — the restart this ledger
commit causes.

Checked rather than assumed, because a `guest` session was present afterwards and
the first draft of this entry claimed the file was empty. The log says otherwise
and says whose it is:

    10:13:59  passwordless login: guest (read) from 192.168.8.196   <- the test
    10:16:17  server starting                                       <- sessions cleared
    10:16:18  guest POST /api/auth/session                           <- a browser reconnecting

The surviving session was re-established by a browser holding a guest cookie, a
second after the restart. The test's own session is gone. No other state was
touched; nothing under `G:\Archive` was read or written.

---

## 2026-09-04 — Page options belong to the account (v2.0.254)

**Asked for:** hide the Transcode card unless admin, and save the page's
options per user — window view preference and colour.

### Transcode: not changed, deliberately

It is already `server-admin-only`, which is **stricter** than the `admin-only`
asked for. Matching the request would have *loosened* it — handing a plain admin
a panel that reaches any path on disk and can saturate the GPU. Left alone and
said so rather than doing it quietly.

### Preferences: a real defect, confirmed

`UserAccount` has no preferences field, and all 35 option call sites went
straight to `localStorage` under bare keys. localStorage is **per browser**, so
where two accounts sign in on one machine they shared a single set: a guest
choosing the light theme changed it for the owner, and the owner's card layout
arrived for the guest.

Affected: theme, per-card view mode, card order, folded state, last transcode
folder, transcode sort and conversion order, HLS order, library root, shuffle,
loop, playback speed, resolution, subtitle language, tuner host.

### Change

Every key is suffixed with the account. The **token is excluded** — a credential,
not a preference, and sign-out already clears it.

Two things needed handling rather than assuming:

- **The account is unknown at first paint.** The theme is set by an inline
  `<head>` script to avoid a flash, and the card order is applied before
  `refreshAuth` answers. So the last account is kept under a plain key as the
  best guess, and `refreshAuth` corrects the page when the server disagrees.
- **A first sign-in adopts existing bare keys**, so nobody's layout is thrown
  away — but only where that account has nothing of its own, so adoption can
  never overwrite a choice already made.

### Verification

A browser harness loading the **real** `dashboard-core.js`, two accounts sharing
one localStorage. 16 assertions, all passing: adoption on first sign-in,
isolation in both directions, switching back and forth, adoption never
overwriting, the token never namespaced and never copied, the first-paint hint
recorded, signed-out falling back to bare keys, and the too-early theme and
layout re-applied on a switch.

One self-inflicted bug caught and fixed during the work: the helpers were
inserted into `dashboard-core.js` *before* the mechanical pass that rewrote
every call site, so the migration function got its own raw reads rewritten and
would have suffixed already-suffixed keys. Found by auditing the remaining raw
accesses rather than by the tests.

### Test residue

A local HTTP server on 127.0.0.1:8795 and a copy of `dashboard-core.js` under
the scratch directory. Both removed; no python processes left, scratch empty.
No server state touched, nothing written under `G:\Archive`.

---

## 2026-09-04 — The per-account preferences did not work (v2.0.256 → v2.0.258)

**Reported:** the view of each window was not being saved as guest.

### Three faults, all introduced with v2.0.254

1. **Two key names were written from memory, not read off the code.** The
   adoption list said `j0kers-cards` for the card order (really
   `j0kers-card-order`) and looked for folded state under `j0kers-fold-`
   (really `fold:` + slug). Neither ever matched, so a first sign-in silently
   dropped the card order and folded state somebody already had. The list now
   names the file and constant each key comes from, beside it.
2. **Folded state was restored only while the fold buttons were built** — at
   load, long before `refreshAuth` says whose preferences these are. Nothing
   re-applied it on a switch. Split into `applySavedFolding()` and re-run.
3. **The theme and card views acted only when they found a saved value.** An
   account with no choice of its own is not "no preference" — the value on
   screen belongs to whoever was there before. Both now assign either way,
   falling back to the same light/dark guess the inline `<head>` script makes,
   and to `default` for a view.

Fault 3 was found only by staging a browser as one account and signing in as
another — the case that matters, and the one v2.0.254 was never tried against.
The harness written for it tested a single account and passed all sixteen
assertions while three real bugs sat underneath.

### Verification (live, real accounts, real browser)

Browser staged as `j0ker` (theme `royal`, hls view `info`, all 9 cards folded),
then signed in as `guest`:

| | before the fix | after |
|---|---|---|
| theme | `royal` (j0ker's) | `light` (default) |
| hls view | `info` (j0ker's) | `default` |
| folded cards | 0/9 | 0/9 |
| j0ker's keys | — | all 11 intact |

Then guest's own choices — theme `cloud`, hls view `condensed`, one card folded
— all survived a reload, with `j0kers-theme@j0ker` still `royal` and
`j0kers-view-hls@j0ker` still `info`.

### What is now stored per account

Suffixed with the account name: theme; card order; folded state per card; view
mode per card (hls, mounts, tv, ch); last library root; shuffle; loop; playback
speed; playback resolution; subtitle language; last transcode folder; transcode
sort; conversion order; HLS stream order; tuner host.

**Not** per account, deliberately: the API token. It is a credential, not a
preference, and signing out already removes it.

### v2.0.258 — Clear list for Recently watched

The Sessions card holds two lists and only one needed clearing: the table below
is live viewings inferred from traffic, which expire on their own after 90
seconds. The Recently-watched list beside the title is what accumulates — 17
entries here, going back a week.

`DELETE /api/history` already existed and nothing called it, at `Read`. That
mattered: `Forget` removes the rows with **no account** against them as well as
the caller's own, and those are what a DLNA viewing leaves — so a guest clearing
"their" list would have cleared what the owner sees. The DELETE is `Admin` now;
only the DELETE, so recording what was watched and how far in stays open to a
read account.

Verified as the real guest: `DELETE /api/history` → **403**,
`POST /api/history/position` → **200**, history intact at 17 entries. 202 tests
pass.

### Test residue

localStorage in the test browser cleared (16 keys). A guest sign-in session,
cleared by the restart this commit causes. No files written under `G:\Archive`;
`history.json` untouched.

---

## 2026-09-04 — Preferences follow the account, not the browser (v2.0.260)

**Reported:** guest login still was not saving window view or open/closed
state, with "do not guess, test and correct".

### The hunt, and why it kept passing

Cache headers ruled out (`Cache-Control: no-store`, so the browser had current
code). The login flow ruled out (`login.html` does `location.replace("/")`, a
fresh load — the same thing the tests did). Sign-out ruled out (it removes only
the token). A full cycle including a **server restart** was run and passed.

Every test passed because the code was right. **The environment was different:
the owner was using an incognito window.** Chrome gives it a separate
localStorage that is discarded when the last incognito window closes — so
settings saved, survived reloads, and vanished with the window.

Reaching the owner's own browser was tried first (`list_connected_browsers`
returned empty, so Claude-in-Chrome was unavailable). The question of which
browser context was in use should have been asked far earlier; it was the
single fact that explained everything and none of the code reading could.

### The real limitation this exposed

Per-account **keys** in localStorage only separate accounts on one browser.
They do nothing for the same account on a phone, in a second profile, or in
incognito. v2.0.254 fixed half the problem and the half it fixed was not the
half being reported.

### Change

- **`Media/UserPreferences.cs`** (new) — a flat per-account store in
  `preferences.json`, keyed by account **id** so renaming an account keeps its
  settings. It knows nothing about what a key means; that is the dashboard's
  business. Capped at 200 keys per account, 128 chars a key, 4096 a value,
  because a signed-in account writes into it directly.
- **`GET`/`PUT /api/preferences`** at `Read` — every account has its own and
  the handlers only ever touch the caller's. `PUT` **merges**, so a page that
  has not learned about a newer setting cannot delete it; an empty value is how
  a client says forget one.
- **Client** — localStorage is demoted to a cache. It still earns its place: it
  is what the inline `<head>` script reads to set the theme before first paint,
  which the server's answer cannot arrive in time for. The page paints from the
  cache, then the account's real settings land over it. Writes are queued 400ms
  (dragging a card writes an order per drop; finding a theme means cycling
  seven) and flushed on `pagehide`. On first sign-in anything the browser knows
  that the account does not is pushed up, so existing settings become theirs
  everywhere rather than being discarded.

### Verification (live, real accounts)

Set as guest: theme `cloud`, HLS view `info`, one card folded. Confirmed on the
server, then **localStorage wiped entirely** — which is exactly what closing an
incognito window does — and the page reloaded:

| | |
|---|---|
| storage at load | empty |
| theme | `cloud` — restored |
| HLS view | `info` — restored |
| folded card | `fold:problems` — restored |
| cache | rebuilt from the server |

`preferences.json` showed the two accounts separate, and the owner's own six
settings had been pushed up from their browser by the first-sign-in path,
unprompted — the migration working on real data.

202 tests pass.

### Test residue

The three test settings written under the guest account were removed through
the API (`PUT` with empty values); `preferences.json` now holds only the
owner's. Browser localStorage cleared. A guest sign-in session, cleared by the
next restart. Nothing written under `G:\Archive`.

---

## 2026-09-04 — "Why is Avengers transcoding?" (v2.0.262)

### The question, answered

Pressing Play started a conversion of a film that needed none. Two separate
things were being conflated, both called transcoding:

- **The Transcode panel** asks *does a TV need this converted?* The probe cache
  says `h264|aac` for all four SciFi_Fantasy Avengers films, so the answer is
  no and the panel correctly never offered them. It was right.
- **Play** asks *is there an HLS copy to stream?* The browser player only
  speaks HLS, so for any plain MP4 the answer is always no, and it makes one.

`POST /api/play` at 14:15:30.184 is in the log alongside `started: vod
Avengers.Endgame`. Nothing was wrong with the file.

### An error of mine, corrected

I reported Endgame as "40 minutes in of 3 hours" with 2 hours to go. That was
segments-of-film-produced read as elapsed wall-clock. Both films had already
finished — 11m 45s and 13m 15s — because NVENC runs ~13x faster than realtime.
The owner caught it.

### Three real faults found underneath

**1. A timeout that could never fire.** `RunFfmpeg` read stderr to the end and
*then* checked the clock:

    p.StandardError.ReadToEnd();
    if (!p.WaitForExit(timeoutMs)) { p.Kill(true); }

`ReadToEnd` returns when the pipe closes, which happens when the process exits,
so for a process that never exits the timeout below is unreachable. Measured:
thumbnail grabs given 20 seconds ran **24 minutes**. An earlier session audited
these exact lines and cleared them — the check then was for an undrained-pipe
deadlock, and this is a different fault needing no full pipe at all, only a
child that does not finish. Both copies (`FfmpegManager`, `SubtitleManager`)
now use `ProcessJob.Run`.

**2. Thumbnails taken from a live playlist.** The thumbnail path falls back to
`index.m3u8`, and a playlist without `EXT-X-ENDLIST` is a *live* stream to
ffmpeg — asked to seek 60s into one listing two segments, it waits for the rest
forever, holding the file open. It was attempted **one second** after the
conversion started. It now only reads a playlist that says it is finished; the
segment attempts already cover an unfinished one.

**3. Re-encoding what only needed repackaging** — the fix asked for. See below.

Combined effect: a retry loop spawning stuck ffmpeg processes as fast as they
were killed. Two respawned within four minutes of being cleared by hand.

### The remux

When the source already carries the codecs being asked for and no particular
height was requested, the streams are copied. Measured on Endgame:

| | source | re-encode | remux |
|---|---|---|---|
| video | h264 1920x800 @ 2.38 Mbps | h264 @ 2.30 Mbps | **identical** |
| audio | aac 6ch / 5.1 | aac stereo (`-ac 2`) | **aac 6ch / 5.1** |
| 90s takes | — | ~7s of GPU | **0.17s** |

The surround loss was the worse half and was invisible: `AudioArgs` has
`-ac 2`, so every conversion folded 5.1 to stereo.

Scaling still encodes, and so does anything whose codecs do not match.
`NeedsFmp4` was taught about the per-file copy, or remuxing HEVC would have
asked for MPEG-TS segments that cannot carry it. Hardware decode setup is
skipped when nothing is decoded.

**The cost, stated:** a copy cannot place keyframes, so a segment ends where
the source already has one and a seek lands on the nearest — a scrub bar a few
seconds coarse. Accepted by the owner in exchange for the picture and the mix.

### Damage, measured

Of 2,906 conversions, **one** was left broken: Infinity War, 1497 segments and
3.4 GB on disk with a playlist listing a single segment and no `ENDLIST` —
unusable, because the stuck readers held `index.m3u8` open while the muxer
tried to finalise it. **Outstanding: whether to delete it** so it remakes as a
fast, lossless remux (~2.4 GB, ~20s) — not deleted without asking.

### Live-system actions

Killed the stuck thumbnail processes (3, then 2 respawned). Read-only
elsewhere: logs, probe cache, `ffprobe` on three sources. A 90-second remux
written to scratch and deleted. Nothing under `G:\Archive` written or removed.
202 tests pass.

---

## 2026-09-06 — One file that installs it anywhere (v2.0.263 → v2.0.264)

**Asked for:** a self-installing package that upgrades cleanly over each
previous version, sitting on the desktop, and copyable to another Windows 11
machine. Then: use this ledger to clean up after the testing and audit myself.

### What the package is

    [ setup stub ][ payload.zip ][ 8-byte length ][ 16-byte marker ]

One executable, because it has to be carried to a machine that has nothing.
`installer/Setup` is a self-contained, trimmed .NET single-file program (13 MB)
that reads the footer, streams the payload out of its own tail in 1 MB chunks
to `%TEMP%\j0kers-setup-<8hex>`, runs `Install.cmd` with whatever arguments it
was given, and deletes the temp directory in a `finally`.

ffmpeg and ffprobe travel inside it. They are most of the 235 MB and they do
not change between versions, but a package that leaves them out only installs
onto a machine that already has them, which is not what "copy this to another
box" means. They are taken from this install first, so what ships is the pair
the server has been running against — not whatever WinGet has today.

### Two packagers were tried and abandoned before this one

| Approach | Why it was dropped |
|---|---|
| 7-Zip SFX | `RunProgram` in the config is ignored by `7z.sfx`; it extracts and stops. |
| IExpress | Built a package that extracted and never launched the installer. |

Both were discarded on **observed behaviour**, not on reputation. The stub
replaced them only after checking the premise it depends on: 1 MB was appended
to a published .NET single-file exe and the exe still ran, so the payload can
ride in the tail without disturbing the host.

### Upgrading over a running server

Tested as the real thing, not simulated: the package was run `-Quiet` against a
**running** server (pid 7376). It stopped it, replaced the binary, and started
it again (pid 5596). Every config file was left as it was.

`Install.ps1` gained the half that was missing — it now records whether the
server was running *before* it started, and restarts it only in that case.
Upgrading a server that was down used to leave it running.

### PowerShell 5.1 faults found while building it

* `System.IO.Compression.FileSystem` is not loaded by default — `Add-Type` it.
* `Where-Object` returning a single item returns *the item*, not a one-element
  array, so `$ffSources[0]` handed back the character `C`. Wrapped in `@()`.
* `config/providers.json` does not exist in this repository. The older
  `build-package.ps1` assumes it does and still has that latent bug; the new
  script ships without it and lets the server write its own.

### Self-audit: my own residue in the accounts file

Every test sign-in with *remember this device* mints a 365-day API key
(`ControlApiAuth.cs:301`), and nothing prunes them. The 2026-09-04 preference
testing left **ten** on the `guest` account, created between 16:07 and 17:27
UTC, none used since 17:28 that day. j0ker's four are untouched and one is in
daily use.

`users.json.previous` — the installer's pre-upgrade backup — exists only
because of the test install above, and holds the twelve-key state.

**Not applied.** Writing to the accounts file is refused by this session's
permission gate, correctly. The removal is prepared and proven instead:
`Clean up Claude test keys.ps1` on the desktop cuts exactly those ten key
objects by id, refuses to write anything that does not parse as JSON, backs the
file up first, and stops and restarts the server around the edit because the
running server holds the accounts in memory and would write the old set back.

Verified against a copy: 10 removed, j0ker 4 and guest 2 remaining, output
**byte-identical** to a separately validated hand-edit, and the live server's
pid unchanged across the run.

### A mistake made during that verification

The first version of that script was tested before a guard was added, and the
patch adding the guard failed to apply without my noticing. It ran against a
copy but still stopped the **live server**, then failed to restart it because
it was pointed at the copy's directory. The server was down for about ninety
seconds. The guard now compares the target against the real install path and
only ever stops the server for that one.

### Live-system actions

| Action | Detail |
|---|---|
| Server stopped and started | 4 times: once deliberately, once by the ungated script above, twice to restore it. Idle each time — no media request since 16:08. |
| Package installed | `-Quiet` upgrade over the running install. Config untouched. |
| `users.json` | **Read only.** Backed up to the scratchpad. Not modified. |
| `users.json.previous` | Left in place — deleting it was refused with the same gate. |
| Dashboard windows | Each restart opens one (`openDashboardOnStart`). Two are live now; close any spares by hand. |
| Desktop | `j0kers Media Server Setup <version>.exe` replaced; older packages removed. |
| Scratchpad | `payload.zip`, `sfxtest/`, `iextest/`, `stub/`, `appendtest.exe`, test copies of `users.json` — all under the session scratchpad, none in the repository or the install. |

Checked and **not** a fault: the `page opened` line every ~20 seconds is the
dashboard's live link being deliberately closed and remade (`ControlApi.cs:899`),
two open pages reconnecting. Left alone.

### Still outstanding from 2026-09-04

Infinity War's conversion is still the one broken artefact — 1497 segments on
disk, a playlist listing one, no `ENDLIST`. Deleting it would let it remake as
a fast lossless remux. Not deleted without asking.

---

## 2026-09-06 — Start with Windows (v2.0.264 → v2.0.265)

**Asked for:** a "Start with Windows" checkbox under Config. When ticked, the
server starts at boot or relogin — into the tray if *Minimize to the system
tray* is set, otherwise just opened.

### How it starts

An entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, which
Windows runs at every interactive logon. Per-user rather than machine-wide on
purpose: HKLM needs an administrator, and a server started before anybody logs
in has no desktop to put a tray icon or a dashboard window on — which is the
half of this the setting exists for.

Raw advapi32 P/Invoke rather than `Microsoft.Win32.Registry`, whose types are
not in the reference set for this project's platform-neutral `net10.0` target.
Same reasoning, and the same shape, as `Services/TrayIcon.cs`.

The checkbox changes only *whether* the server starts, not what it starts into.
`minimizeToTray` and `openDashboardOnStart` were already independent of each
other — tray mode hides the console, the other opens a browser — so the note
under the box states the actual combination rather than explaining the rule.

### Two defects found by review, both verified against this machine

**1. A bare executable was registered when no config file was loaded** (high).
A Run entry inherits Explorer's working directory, normally
`C:\Windows\system32`. Without a config path on the command line the server
would look for `settings.json` and `users.json` there, find neither, and come
up on default ports **with no accounts at all** — `Program.cs` says as much:
"no administrator account — the dashboard and its configuration are open to
anyone on this network". The installed case was already safe (`Install.ps1`
passes `server.json`); a portable publish folder was not.

Fixed by `ServerConfig.EnsureConfigFile()`, which returns the loaded config
path or writes an empty `{}` beside the other sidecars first. The file has to
exist rather than merely be named, because a named-but-missing config is fatal
at startup by design. `Refresh` now refuses to rewrite an entry when it has no
config path to name, since a bare command is strictly worse than a stale one.

Also corrected while there: a named-but-absent config now anchors the sidecars
to *its own* directory instead of the working directory.

**2. Task Manager's Startup tab disables without deleting** (medium).
It leaves the Run value alone and records the decision under
`…\Explorer\StartupApproved\Run`, first byte 3 for disabled. Reading only the
Run key reported the box ticked while nothing started — and because the
approval is keyed by value name, untick-and-retick in the dialog rewrote the
same name and left the disabled byte in place, so the box could never be
fixed from the dashboard.

Confirmed on this machine before fixing: OneDrive, Spotify and Docker Desktop
all sit in the Run key with a 3. `IsEnabled()` now requires both halves, and
enabling clears the approval record.

The comment in `ControlApi` that claimed Task Manager *clears* the entry was
simply wrong and has been rewritten.

### Verification

| What | How |
|---|---|
| Registry round-trip | A throwaway test wrote, read back, and deleted the real entry — value identical to the composed command, no embedded NUL, delete idempotent. Deleted straight afterwards; the Run key was confirmed back to its four pre-existing entries (OneDrive, Docker Desktop, Spotify, LM Studio). |
| Suite | 214 pass (202 before this session, 211 after the first cut, 214 after the review fixes). |
| Review | 5 dimensions, 18 claims, adversarially verified; 4 survived, of which 3 were the same defect found independently. |

The registry half is deliberately **not** in the committed suite: there is one
`Run` key per user, and a test that died between writing and cleaning up would
leave this machine launching a server at every logon.

### Live-system actions

Registry: the real HKCU `Run` key was written and deleted once by the probe
above, and read several times. Verified back to baseline. Nothing else on the
machine was touched; no server restart beyond the publish hook's own.

---

## 2026-09-06 — A Log out button, and where 46 seconds went (v2.0.267 → v2.0.270)

### Log out

Signing out existed but only at the bottom of the Account dialog. It is now a
pill in the header next to the account button, and both call the same function.

That function had a real fault: it dropped the "remember this device" key from
`localStorage` without revoking it, and the key is good for a year. Every
sign-in therefore left a live credential on the account — which is exactly how
`guest` reached twelve of them earlier today. It is now revoked through the
self-service endpoint first, while the session can still authorise the call.
It also lands on `/` with `replace` rather than `reload`, so the dashboard URL
— which carries the server's self-open token — is not one Back press away.

Correction to something claimed while doing this: I reported `signOut()` as
having no caller. It had one; my grep covered only the `.js` files and missed
the button in the HTML. I also reported the tray fix as absent from the built
binary — that check searched UTF-8, and C# string literals are UTF-16.

### The desktop shortcut kept vanishing

Three times in one afternoon. Neither the post-commit hook nor
`build-setup.ps1` ever deleted it — they only mention it in messages, which is
what made it look like one of them did. Publishing replaces the installed
binary, and for the moment it is missing Windows treats the shortcut as broken.
The build script now rebuilds it and **reads it back** at the end of a round.

### The 46-second start

Measured, not guessed. The gap sat between `streaming services started` and
`listening on`, with nothing logged in between, while the three starts before
it took 1.3 seconds each.

Every step of the `ControlApi` constructor now reports its own time. The answer:

| step | time |
|---|---|
| stores | 2 ms |
| hiding pre-existing conversions | 0 ms |
| favorites, history, preferences, dlna shares | 4 ms |
| television codec cache | 29 ms |
| dlna service | 5 ms |
| codec prefetch handover | 0 ms |
| channel providers | 4 ms |
| tv proxy | 0 ms |
| **whole constructor** | **47 ms** |

So it was never the constructor. The time was inside `HttpListener.Start()`.
http.sys owns the port, not the process, and the previous server had been
force-killed rather than closed — `netstat` showed 9090 `LISTENING` under pid 4
with nothing answering it. The new listener simply waited for the old
registration to drain. There is no retry loop in `HttpListenerBinder`; it is
one blocking call.

**Not a logon-path problem, and not reproducible on a clean stop and start** —
the very next start was 47 ms end to end, bind included. The bind is now timed
too, and says so above two seconds, because it is the one step that can stop
dead in silence.

### Verified live, after the owner stopped the elevated server

| What | Evidence |
|---|---|
| Tray no longer opens a browser | `not opening the dashboard: this server is minimised to the tray`, and no `dashboard opened` line — where every earlier start had one directly after the tray line. |
| Log out is really served | The running server returned `dashboard-core.js` at 26,968 bytes, byte-identical to the repository, with the revoke call present. |
| Constructor timing | The table above, from the install's own log. |

Earlier in the session this could not be checked at all: the running server was
elevated, `Stop-Process` was denied, and it was serving a 26,036-byte copy of
that file — an older build — while the install on disk was current.

### Live-system actions

Swept the leftover `j0kers-media-server.exe.inuse.778434309`. Stopped and
started the server to measure; it is running and in the tray. Registry read
only. Nothing under `G:\Archive` touched.

### Still outstanding

`Clean up Claude test keys.ps1` on the desktop has not been run — ten guest API
keys from my own testing are still on the account.

---

## 2026-09-06 — Why an already-h264 film was re-encoded (v2.0.272 → v2.0.273)

**Reported:** the Transcode window shows nothing left to transcode, yet adding
media to an HLS stream starts converting it again. And stopping a conversion
early leaves parts of it behind — is that useful?

### It was not re-doing work

`Alice In Wonderland (H.264).mp4` converted at 21:21:04 and took 2m51s. All
2,907 conversion directories were read and their `source.txt` compared: exactly
one names that file, created at 21:23:55. There was no earlier conversion. The
server was not repeating itself; it was doing the work for the first time.

### The two windows answer different questions

The Transcode window asks *does this file need its codecs changed* — for direct
play and for DLNA. Alice's video is already h264, so the honest answer is no,
and it correctly showed nothing.

Putting a file into an **HLS stream** is a different operation: it has to be cut
into segments with a playlist, whether or not a single codec changes. Nothing in
that window tracks whether that has been done, so "converted" means two
different things depending on which part of the dashboard is asked. That is a
gap in what is shown, not a fault in what was done.

### The real fault: an all-or-nothing remux test

`CanRemuxToHls` required **both** streams to match before anything was copied:

| Alice In Wonderland (H.264).mp4 | source | wanted |
|---|---|---|
| video | h264, 624×352 | h264 (`h264_nvenc`) — matches |
| audio | **mp3**, 2ch | aac — does not match |

One mismatched soundtrack therefore condemned the picture as well. A film whose
video needed *nothing done to it* had it decoded and re-encoded h264 → h264 to
fix an audio track.

Measured on the first 120 seconds of that file:

| | video | 120s of segments | full file (108 min) |
|---|---|---|---|
| re-encode both (before) | h264 → h264 | 8,944,664 B | 2m 51s |
| copy video, convert audio (after) | **untouched** | **11,132,232 B** | **59s** |

The old path was 20% smaller because it was throwing picture away — a silent
quality reduction on a file the owner had asked only to make streamable.

`CopyableStreams` now decides each stream on its own. A requested height rules
out copying the picture and says nothing about the sound; `NeedsFmp4` takes both
flags rather than one.

### Still there, and named rather than fixed

`AudioArgs` remains `-ac 2`. Any surround track that is not already aac is still
folded to stereo when it is converted. Copied audio is untouched, so this only
bites where the codec genuinely has to change — but it is a quality reduction
nobody asked for, and it is not this change's to make silently.

### Partial conversions, honestly

Stopping early leaves the segments on disk. They cannot be resumed — the next
conversion of that file deletes the directory and starts over — so they are
dead weight until then. The server reports the count at startup and deletes
nothing by itself, which is the right default for the owner's disk but means
they accumulate. One is outstanding now: Infinity War, 1497 segments and 3.4 GB
behind a playlist listing a single segment.

### Live-system actions

Read-only on `G:\Archive`: `ffprobe` on one source, and every `source.txt` under
`G:\Archive\Transcoded` read once to answer the "was it already converted"
question. Two timed ffmpeg runs written to the session scratchpad and deleted;
809 MB at its peak, nothing under the media root. 214 tests pass.

---

## 2026-09-06 — Unfinished conversions clear themselves (v2.0.274 → v2.0.275)

**Asked for:** clean up partial conversions automatically.

### Why this is not the sweep that was removed

An earlier version deleted every `vod-*` directory whose playlist had no
`EXT-X-ENDLIST`, at startup, silently. That was removed because it destroyed
real work: this server is stopped mid-encode as a matter of routine — a
restart, an upgrade, the machine sleeping — and every conversion running at
that instant is "unfinished". An upgrade is the worst case in the set, because
it stops a converting server and starts a sweeping one.

The difference here is a grace period, not good intentions.
`ffmpeg.partialConversionGraceHours` defaults to **24**, and the age used is the
newest write **anywhere inside** the directory rather than the directory's own
stamp — on Windows a directory's `LastWriteTime` does not move when a file
inside it is appended to, so a conversion that has been writing segments for
hours looks untouched, and reading the directory alone would sweep the one
thing that must never be swept. Setting the value to 0 goes back to reporting
and deleting nothing.

Every removal names the stream, its file count, its size and when it was last
written. The count that survives is reported with the reason it survived.

### Tested on the case that matters

`IsStalePartial` was separated out and given five tests, because the guard is
the whole of the difference between this and the sweep that was deleted:

| | expected |
|---|---|
| interrupted 20 seconds ago | left alone |
| untouched for three days | cleared |
| finished 400 days ago | never touched |
| grace set to 0 | clears nothing |
| old directory stamp, segment written 5s ago | left alone |

A test that only proved "stale directories are removed" would be testing the
half that was never the problem.

### Also: the silent delete in StartVod

Starting a conversion over a directory that exists deletes it recursively, and
that only ever logged when the delete **failed**. So "did it throw away what I
already had, or convert something it had never seen?" could not be answered
from the log — it had to be reconstructed from directory timestamps and a scan
of every `source.txt` on disk. It now says which stream, how many files and how
much.

For the record, that question was asked and the answer was **no**: zero
evictions have ever been logged, zero failed clears, and the unfinished count
was 1 both before (21:20:42) and after (21:35:05) the Alice conversion. Nothing
was deleted; it was a first conversion.

### Testing audit for this session

| Leftover | State |
|---|---|
| Session scratchpads (both ids) | Empty. The 809 MB of timed ffmpeg output written while measuring the remux was deleted at the time. |
| `j0kers-tests-*` temp directories | 2 found after the suite ran, swept. `TempDir.Dispose` swallows failures, so they accumulate quietly. |
| `j0kers-pkg-*`, `j0kers-stub-*`, `j0kers-payload-*`, `j0kers-setup-*` | None. |
| `HKCU\...\Run` and `...\StartupApproved\Run` | No `j0kers` entry in either. The throwaway registry probe left nothing. |
| `ZzTempRegistryProbe.cs` | Deleted the moment it had answered. |
| `j0kers-media-server.exe.inuse.*` | Swept. |
| Repository | Only intended changes; no stray probe files. |
| `publish/staging` (72 MB) | The post-commit hook's own build area, gitignored and rebuilt every publish. Left. |
| `G:\Archive` | Read-only throughout: one `ffprobe`, and every `source.txt` read once. Nothing written or removed. |

**Still outstanding, and not mine to close:** `guest` still carries **12** API
keys, ten of them minted by my 2026-09-04 testing. Writing `users.json` is
refused by this session's permission gate — correctly — so the removal is
prepared and proven instead, as `Clean up Claude test keys.ps1` on the desktop.
It has not been run.

### Addendum — the guard was wrong on its first cut

The age check seeded itself with the directory's own timestamp before taking
the newest file, which is the exact stamp the comment beside it calls
unreliable. That stamp moves whenever an entry is added or removed, for reasons
unrelated to the conversion progressing.

Caught by watching it run rather than by assuming it worked: the first start
reported *"1 conversion did not finish … too recent to clear (within 24h)"* for
a directory whose newest segment was **two days** old and whose folder stamp was
forty seconds old. The one thing this feature existed to remove was the one
thing it protected. The files are the work, so the files are what is asked; a
directory with no files in it is the only case that falls back to the folder.

Six tests now, the extra one being that exact shape.

### Result on the live install

    removed the unfinished vod-avengers-infinity-war-2018-1080p-webrip-x264-yts-f9b407f3:
      1501 file(s), 3.3 GB, last written 2026-09-04 14:36
      — it had no end marker, so there was nothing to resume
    cleaned up 1 unfinished conversion(s), 3.3 GB back

2,907 conversions became 2,906. That directory had been outstanding since
2026-09-04 and is the one this ledger has been carrying as "not deleted without
asking" ever since. 220 tests pass.

---

## 2026-09-06 — The Transcode window answers both questions (v2.0.277 → v2.0.278)

**Said plainly by the owner:** the point of this server is high-quality media
that is there almost instantly. The Transcode window existed for two reasons —
make files a smart TV can play, *and* make sure there is already a conversion
so nothing waits for an HLS stream. It only ever reported the first.

That is why "the Transcode window shows NO MEDIA to transcode" and "adding
media to an HLS stream starts transcoding it" were both true at once. They are
different questions and the window answered one of them.

### The two questions

| | ready when |
|---|---|
| **PC/VLC** | a finished conversion exists, of **any** resolution — playback here is HLS, so without one, pressing play waits for ffmpeg |
| **Television (DLNA)** | the set can decode the original, **or** a **full-resolution** conversion exists — `VodIndex` excludes scaled names, because a 720p copy is not what a 4K set asked for |

They disagree in both directions, which is what makes four states rather than
two. A 720p conversion of an HEVC film is instant here and unplayable there. An
h264 file with no conversion is the exact reverse.

### The pills

| state | pill | meaning |
|---|---|---|
| conversion + set can play | **Ready** — green | nothing to do |
| no conversion, set can play | **Convert PC/VLC** — orange | the dashboard would wait |
| conversion, but scaled only | **Convert DLNA** — yellow | the set will not take a scaled copy |
| neither | **Needs converting** — red | no way of playing it works |
| codecs unread | *checking…* — grey | not a promise either way |

Orange and yellow are pulled well apart rather than being two ambers, and
"converting" moved off amber onto the accent colour — an in-progress row
sitting between them was the easiest thing in the list to misread. Every pill
carries the sentence behind it as a tooltip.

Folder pills and the status sort were rebuilt from the same counts, so a folder
can no longer contradict the rows inside it. The old folder headline counted
only the TV question, so a folder the dashboard would stall on every single
time read as "TV-ready".

### Tested as a table

`ControlApi.Readiness` was separated from everything it needs to construct, and
has 11 tests covering each cell — including the two that used to be invisible
(scaled-only, and no-conversion-but-decodable), unknown staying unknown, forced
substitution, and no-ffmpeg matching what `DlnaShouldList` already does.

231 tests pass.

---

## 2026-09-06 — Play the file when the file is already playable (v2.0.278 → v2.0.279)

**Asked:** "if I file browse out to that file and open it with VLC it plays fine.
Why is it marked?"

Because the pill was answering a question about the server and wearing a label
about a device.

### What was actually wrong

The server had exactly one route to a PC: `/api/play`, which is HLS, which
needs a conversion. A television has been handed originals untouched for as
long as DLNA has existed — `DlnaService.ServeFile`, range requests and all —
and no one ever offered the same thing to the dashboard. So a file that was
already finished got queued for an encode purely because that was the only road
out.

`/api/file` now serves the original with Range, at `AccessLevel.Read` behind the
same `DenyUnshared` check as every other route to media. `/api/play` returns it
instead of starting a conversion when the file is already playable and no
particular height was asked for; scaling is the one thing sending the original
cannot do, so a height still encodes. `ServeFile` became static, with the DLNA
headers optional — a browser has no use for `contentFeatures.dlna.org`, and
sending it would be describing the response as something it is not.

### Then the file in question turned out to prove the other half

`300 (H.264).mp4` is **h264 video, AC-3 audio**.

VLC plays AC-3. Chrome and Firefox cannot decode it at all. So the owner opening
that file by hand and the dashboard trying to play it are not the same test, and
"PC/VLC" was one word for two players with different abilities. The file is
genuinely not playable in the dashboard, and genuinely fine in VLC, and the pill
said something false either way.

`CanPlayDirectly` is therefore deliberately narrower than VLC: mp4/m4v/webm/mov,
h264/vp9/vp8/av1, aac/mp3/opus/vorbis. Guessing wrong in this direction hands a
browser a file it cannot decode, which reads as a broken server rather than a
slow one. HEVC is left out for the same reason — Safari plays it, Chrome mostly
does not.

The orange pill is now **"Convert for player"**, and says in its tooltip that
the TV plays it, VLC probably plays it, the dashboard cannot, and that the
conversion copies the video untouched and only redoes the audio — which is true
since the per-stream change earlier today, and is why that conversion is now
59s rather than 2m51s.

### What this changes in practice

A library of mp4/aac files stops being converted at all: no second copy, no
generation of picture spent, no wait. Only files a browser genuinely cannot open
are queued, and those now keep their video exactly as it was.

231 tests pass.

---

## 2026-09-06 — Up and Refresh again, and the reason they stopped (v2.0.279 → v2.0.280)

**Reported:** Up and Refresh do not work in the Transcode window. "Again."

### It was mine, from the previous round

`CanPlayDirectly` was put on the listing path — once per file in the folder, and
once per file in every sub-folder for the summary pills. It calls
`FfmpegManager.ProbeCodecs`, which **has no cache and starts an ffprobe every
single time**, with a 15-second ceiling each.

The distinction that made this invisible: `TvCodecs.Codecs` *does* cache, which
is why the per-file check that was already there had been fine for months. The
one I added looked identical and was not. Opening a folder became thousands of
process launches, so the request behind the button took minutes, and a button
whose request has not come back is indistinguishable from a button that does
nothing.

`TvCodecs.CodecsCached` now answers from the cache and never launches anything,
and the decision itself moved to `FfmpegManager.PlayableAsIs`, which takes the
codecs rather than fetching them. The live probe stays on `/api/play` and
`/api/file`, which are one file, on demand, where correctness is worth a probe.

### And they now act on the press

Discarding a stale answer on arrival — which the generation counter already did
correctly — is not the same as not waiting for one. The browser still held the
connection, so a slow scan behind a click meant nothing visible happened until
it finished.

A deliberate navigation now aborts whatever was in flight, and the panel
acknowledges the press immediately: the path updates and the list dims before
the request goes out, rather than after it returns. A background refresh never
cuts off a person's click, and an abort is not reported as a failure — the
reload that replaced it owns the panel.

231 tests pass.

---

## 2026-09-06 — 133 redundant duplicates removed from the library (v2.0.281 → v2.0.282)

**Asked for:** scan for `(H.264)` copies with AC-3 audio that duplicate an
already-Ready original, then — after checking — delete them.

This is the first time this session has deleted the owner's media. It was asked
for explicitly, checked twice, and sent to the Recycle Bin rather than removed.

### What the library actually holds

1,875 `(H.264)` files, from the probe cache alone — **zero ffprobe launches**,
which is the difference between reading 3,801 cached answers and starting 1,875
processes.

| audio | files | browser |
|---|---|---|
| mp3 | 1,337 | plays |
| aac | 225 | plays |
| **ac3** | **311** | refused |
| unknown | 2 | — |

Only the 311 are browser-blocked, and AC-3 is *not* a TV problem — it is in
`TvCodecs.PlayableAudio`. So those `(H.264)` conversions changed the half that
was never blocking anything and left the half that was.

### The seven checks, per file

Nothing was deleted on the strength of a name. For each candidate:

1. its audio really is AC-3 (so it does nothing for a browser)
2. a sibling original exists on disk
3. that sibling is not itself an `(H.264)` file
4. the sibling's conversion carries `EXT-X-ENDLIST`
5. **every segment its playlist lists is present** — 93,793 of them across the set
6. the conversion is full-resolution, so DLNA will accept it
7. the conversion is not empty

**133 passed all seven. 0 failed.** 133 distinct siblings, so no two candidates
leaned on the same original, and none of the 133 had a conversion of its own to
orphan.

The other 178 AC-3 files have no sibling — they *are* the only copy — and were
left alone.

### Live-system actions

`SHFileOperation` with `FOF_ALLOWUNDO`, every dialog suppressed so nothing could
block on a click. Checked first that the Recycle Bin on that volume holds
281 GB with `NukeOnDelete=0`, so 83 GB would genuinely recycle rather than be
destroyed quietly.

* 133 files, **83.0 GB**, moved to the Recycle Bin. Return code 0, nothing aborted.
* After: 0 of the deleted paths remain, the bin holds 83.0 GB, and **133/133**
  originals are still present with a finished, whole, full-resolution conversion.
* `Redundant H264 duplicates.txt` on the desktop is the restore list, naming each
  file removed and the original that covers it.

Nothing else under `G:\Archive` was touched. Paths over 260 characters needed
the long-path prefix to stat at all — without it they drop silently out of a
walk, which is worth remembering for anything that counts files here.

---

## 2026-09-07 — A second duplicate sweep, and four files it refused to delete (v2.0.283)

**Asked for:** scan all of `G:\Archive\Movies` for titles with a Ready copy and
another copy, summarise the non-Ready ones, then recycle groups A, B and D,
skipping Gladiator.

### The library

4,485 video files of 50 MB or more, 3,182 GB. Ready 3,048, not Ready 321, and
**1,116 never probed** — reported separately rather than added to "not Ready",
because conflating them would have put 1,100 GB of files nobody has ever looked
at into a deletion list.

### Two checks that changed the answer

**Running time.** Every candidate was probed against its Ready partner and had
to match within 3%. Two failed and are not duplicates at all:

| | this file | its "partner" |
|---|---|---|
| The Matrix Reloaded (2003).mp4 | 138m | 132m |
| TAKEN (H.264).mp4 | 90m | 93m |

Different cuts. Name normalisation cannot see that; duration can. The same
check *cleared* the three `spiderman2_*.mpg` files, which looked like one film
split into parts: each pairs with its own `_1/_2/_3 (H.264).mp4` of matching
length, so they are per-part duplicates rather than a film about to lose two
thirds of itself.

**Which copy is better.** Resolution and size of both sides, because "keep the
Ready one" says nothing about which one is worth keeping. Four would have
destroyed the better file:

* `Justice League Crisis on Two Earths (2010).mkv` — **1280x720, 2.19 GB**
  against a **640x360, 0.41 GB** partner. The only file in group D, and deleting
  it would have kept a quarter-size DVDRip over a 720p source.
* `spiderman2_1/_2/_3.mpg` — the `.mpg` is the source and the `(H.264).mp4`
  beside it is a re-encode *of* it. Deleting the source to keep a lossy
  derivative is the inverse of the earlier sweep, which kept originals.

Held back under the standing rule that quality is never traded away. One flag
was a false positive and was overridden after looking: *A Christmas Story*
tripped a height-only comparison (352x480 against 640x448) while the kept copy
is both wider and larger.

### Live-system actions

`SHFileOperation` with `FOF_ALLOWUNDO`, dialogs suppressed.

* **16 files, 13.5 GB** to the Recycle Bin. Return code 0, nothing aborted.
* After: none of the 16 remain and **16/16** kept counterparts are still present.
* Seven files deliberately untouched: Gladiator (excluded by request), two
  different cuts, and four better-quality originals.

### A wrong claim, corrected

Picking this up the next day I measured the Recycle Bin with `Get-ChildItem` on
`G:\$Recycle.Bin` and `-ErrorAction SilentlyContinue`, got nothing, and reported
that the bin was empty and both sweeps were permanently gone.

That folder's per-SID subdirectories deny enumeration. I had suppressed the
error and read "no results" as "no files" — an access failure treated as
evidence of absence. Free space being unchanged at exactly 860.0 GB contradicted
it plainly and I explained that away instead of following it.

The owner said the files were still there. Through the Shell API, which is how
the Recycle Bin is meant to be read: **149 items, 92.6 GB, all recoverable** —
exactly 133 from the first sweep plus 16 from this one. Nothing has been lost.

---

## 2026-09-07 — 120 byte-identical copies recycled, and a scan that was lying about 1,116 files (v2.0.284)

### The container rule, and a wrong count

The previous duplicate scans reported **1,116 files "never probed"**, which the
owner rightly challenged: everything under the media folder should have been
read by now.

It had been. `TvCodecs.UnplayableContainers` — `.vob .ifo .divx .rm .rmvb .ogm
.asf .mkv` — is answered from the extension alone, because a television refuses
those wrappers whatever is inside them. `NeedsConversionCached` returns *true*
without probing, and the prefetch then skips the file because it already has an
answer. So those files are absent from `probe-cache.json` **by design**.

My scan looked each path up in that cache and called a miss "unknown". Measured:
1,641 uncached files under Movies, of which **874 `.vob` and 763 `.mkv`** — the
missing set is the unplayable-container set almost exactly. The server knew the
answer all along; the scan asked the wrong oracle.

Rerun with the container rule applied first:

| | before | after |
|---|---|---|
| Ready | 3,048 | **4,057** |
| not Ready | 321 | **305** |
| genuinely unknown | 1,116 | **0** |

Nothing is unread. And the duplicate picture collapses to almost nothing: **1
same-folder group** (DANE COOK, 2.5 GB) and 4 groups elsewhere (11 files,
8.4 GB) where no copy is Ready.

### Full hashes, because sampling was not enough

125 `X (H.264).mp4` / `X (H.264) (2).mp4` pairs exist. A first pass hashed three
8 MB windows — head, middle, tail — and called 121 identical.

Hashing both files **in full**, 112 GB read in 45 minutes, called **120**
identical. The extra exclusion is `Tenacious D - 1x02 - Angel in Disguise`:
same size, matching at all three sample points, different overall. Sampling
would have deleted a file that is not a duplicate.

Five pairs excluded in total: three differ in size, two are the same size with
different content.

### Live-system actions

* **120 files, 55.90 GB** to the Recycle Bin via `SHFileOperation` with
  `FOF_ALLOWUNDO`. Return code 0, nothing aborted. None remain on disk;
  **120/120** originals verified still present.
* The bin now holds exactly those 120 at 55.90 GB. The 149 from the two earlier
  sweeps are gone from it — emptied by the owner — and free space rose from
  860.0 GB to 956.6 GB, which matches.

### A measurement mistake worth keeping

Reading the bin with `$item.Size` gave 39.9 GB for the same 120 files that
`System.Size` reports as 55.90 GB. The first is lazily populated and was simply
wrong. Earlier the same day, `Get-ChildItem` on `G:\$Recycle.Bin` with errors
suppressed returned nothing at all and I read that as "empty".

Two different wrong answers about the same folder in one day, both from asking
Windows casually. The Shell namespace with `System.Size` is the one that agrees
with free space.
