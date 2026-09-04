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
