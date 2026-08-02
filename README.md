# j0kers Media Server

A highly configurable media server in C# / .NET 10 implementing:

| Capability | Guiding RFCs |
|---|---|
| RTSP streaming control (OPTIONS/DESCRIBE/SETUP/PLAY/PAUSE/TEARDOWN, UDP + TCP-interleaved transports) | RFC 2326, RFC 7826 |
| RTP/RTCP media delivery (PCMU audio, sender reports, even/odd port pairs) | RFC 3550 (+ RFC 3551 payload types) |
| HTTP Live Streaming (VOD + sliding-window live playlists served from disk) | RFC 8216 |
| Streaming operations knobs (caching headers, CORS, port ranges, session caps, DSCP) | RFC 9317 |
| HTTP/JSON control API (session audit + forced teardown) | RFC 5167 (requirements) |
| Announcement service `rtsp://host/annc?play=clip` | RFC 4240 (adapted to RTSP) |

See [docs/RFC-COMPLIANCE.md](docs/RFC-COMPLIANCE.md) for exactly what is and
isn't covered from each document.

## Quick start

```bash
dotnet run
```

That's it — from the repo root it picks up [config/server.json](config/server.json)
automatically (pass a path or set `J0KERS_CONFIG` to use a different one).

Then play the built-in test tone:

```bash
ffplay rtsp://localhost:8554/test
```

or in VLC: *Media → Open Network Stream* → `rtsp://localhost:8554/test`.

The **dashboard** opens automatically in your browser at
[http://localhost:9090/](http://localhost:9090/) — live session table with
terminate buttons, server throughput, mounts with copyable `rtsp://`
URIs, HLS stream list, and the effective config. (Set
`control.openDashboardOnStart` to `false` for headless use.)

### Run it in the background (Windows)

Tick **Minimize to the system tray** in the dashboard's ⚙ Config dialog —
it applies immediately, no restart. (Windows puts new tray icons in the
hidden **^** area of the taskbar; drag it out to pin it.) The same thing
is available at startup:

```bash
dotnet run -- -t
```

`-t` (or `"minimizeToTray": true` in the config) hides the console and puts
a joker icon in the notification area. Double-click it to open the
dashboard; right-click for a menu — open dashboard, show/hide the console,
start/stop the streaming services, exit. Closing the dashboard doesn't stop
the server in this mode, so it keeps serving your phone and other devices
until you pick Exit. `--no-tray` overrides the config for one run. On
macOS/Linux use your init system (systemd/launchd) or `nohup` instead.

The **Sessions** card lists everyone watching, both kinds at once. RTSP has
real sessions, so those can be terminated. HLS has none — a viewer is a
series of unrelated file requests — so one is inferred instead: requests
from the same client for the same stream are one viewing, live until they
stop for 90 seconds (long enough that a player which has buffered ahead
and gone quiet doesn't vanish mid-film; it shows as *buffered* rather than
*playing*). There's no connection to cut, so those rows have no Terminate
button — revoke the account or key instead. The **Throughput** tile is the
whole server's output rate, RTP and HLS together, taken from a monotonic
byte counter so a viewer leaving can't push the rate negative.

The header has a **⏻ Start/Stop** button that stops or starts the streaming
services (RTSP + HLS) while the dashboard stays up, a **👥 Users** dialog for
accounts and keys, a **👤 Account** panel for your own password and keys, and
a **⚙ Config**
dialog for the hostname/bind address and the RTSP/HLS/control ports — saved to a `settings.json` sidecar and applied by restarting the
services live (a control-port change takes effect on the next full server
restart).

Every mount has a **▶ Play** button that streams its audio right in the
browser (the server feeds a live WAV over HTTP and the page plays it
gaplessly with Web Audio — no plugins). HLS streams play inline too, via
native HLS or hls.js.

The dashboard also ships a reusable **`pickPath()`** file browser (drives →
folders → files, backed by `/api/browse`): any dashboard feature can call
`await pickPath({ mode: "file" | "folder" | "any", title, startPath })` and
get an absolute path back, or `null` on cancel. The 📁 **Browse** button in
the header uses it to copy a path to the clipboard.

### Any media, one dashboard

With **ffmpeg** installed (`winget install Gyan.FFmpeg` — auto-detected, or
set `ffmpeg.path`), the dashboard becomes a full media center:

- **Media library** — add any number of source folders (➕ Add folder,
  removable chips, persisted server-side in `library.json`). Contents are
  grouped into **Folders / Videos / Music / Pictures** sections with
  counts; videos and pictures render as thumbnail tiles (ffmpeg frame
  grabs, cached under the media root) with icon fallbacks. Movies and
  music transcode to HLS on the fly and play inline; pictures open in a
  lightbox viewer.
- **Codecs** — transcode output codecs are configurable:
  `ffmpeg.videoCodec` (h264 default, h265/hevc, vp9, av1, mpeg2, mpeg4,
  `copy`, or any raw ffmpeg encoder name) and `ffmpeg.audioCodec` (aac
  default, mp3, opus, vorbis, ac3, eac3, flac, alac, pcm, `copy`, or raw
  name). Choices are validated against the installed ffmpeg's encoder
  list at startup (fallback to h264/aac with a warning), modern codecs
  automatically switch HLS to fMP4 segments, and `GET /api/codecs` lists
  every encoder your ffmpeg build ships. Input side, the library
  recognizes virtually every container/format ffmpeg can read.
  (Browser note: h264+aac plays everywhere; hevc/vp9/av1 depend on the
  viewer's browser support.)
- **Subtitles** — tracks embedded in the media *and* sidecar files next to
  it (`movie.en.srt`, `movie.ass`, …) are found automatically, converted to
  WebVTT on demand, and offered in a Subtitles selector in the player (the
  choice is remembered). **＋ Sub file** attaches any subtitle file you pick
  from disk to the current stream. Non-UTF-8 subtitles are detected and
  decoded correctly; image-based tracks (PGS/VobSub) are listed as
  unavailable since they'd need OCR. Watch pages carry the same tracks, so
  subtitles work on phones through the native CC menu.
- **Player controls** — the inline player has ⏪/⏩ 10-second seek
  buttons, playback speed (0.5×–2×), and a quality selector
  (Source/1080p/720p/480p/360p — each height transcodes and caches
  separately, and switching mid-play resumes at the same position).
  Speed and quality choices are remembered. Playback always starts at
  the beginning, and a freshly added RTSP mount starts playing its
  preview immediately.
- **Quick buttons** — pin any media item (the ☆ on tiles and music rows,
  or *⭐ Pin media* in the header to pick a file directly) and it becomes a
  one-click `⭐ name` button at the top of the library that plays the video
  or song, or opens the picture, instantly. Persisted server-side in
  `favorites.json`; unpin with the ✕.
- **Folder playlists** — *▶ Play folder* (or the *▶ All* button on any
  folder row) queues every media file in a folder and auto-advances with
  ⏮/⏭ controls; *☆ Save playlist* remembers the folder by name
  (persisted server-side in `playlists.json`) as a one-click chip above
  the library.
- **Live channels** — add any live source by URL: an HDHomeRun tuner
  (`http://<tuner-ip>:5004/auto/v5.1` for local TV channels), IPTV
  streams, RTSP/RTMP cameras, UDP/SRT feeds. The server restreams each as
  a sliding-window live HLS channel that anything can play, and channels
  persist in `channels.json` and restart with the server. `ffmpeg.liveVideoMode:
  "copy"` remuxes without transcoding when the source is already
  H.264/AAC.

**Add mounts from the GUI**: the *+ Add mount* button in the RTSP mounts
card creates a mount from a test tone or an audio file (picked with the
file browser) — live immediately, no restart. Dashboard-added mounts are
saved to a `mounts.json` sidecar next to your config (so the commented
`server.json` is never rewritten). Every mount row has a ✕ remove button:
dashboard-added mounts are deleted outright, while `server.json` mounts
are hidden via a persisted tombstone in the sidecar — the config file
itself is never touched, and re-adding the same path clears the
tombstone. HLS stream rows have a ✕ too, which deletes the stream's
playlist and segment files from disk (live-channel streams are refused —
remove the channel instead).

Check the control API:

```bash
curl http://localhost:9090/api/status
curl http://localhost:9090/api/sessions
```

## Running on macOS / Linux

The server is cross-platform .NET — the same `dotnet run` works. Notes:

- **ffmpeg**: install with `brew install ffmpeg` (macOS) or your distro's
  package manager (`apt install ffmpeg`, etc.). It's found via PATH.
- **macOS disk permissions**: browsing folders outside your home directory
  (external drives, other users' folders) requires granting **Full Disk
  Access** to whatever launches the server — *System Settings →
  Privacy & Security → Full Disk Access*, then add your terminal app
  (Terminal/iTerm) and restart it. Removable volumes alone can be
  covered by the "Files and Folders → Removable Volumes" permission,
  which macOS prompts for on first access.
- **Browser auto-open** uses `open` on macOS and `xdg-open` on Linux; on a
  headless box set `control.openDashboardOnStart` to `false` (the log
  prints the dashboard URL either way).
- **Ports below 1024** (e.g. RTSP 554) need root/`sudo` on Unix — the
  defaults (8554/8080/9090) don't.

## Configuration

Everything is driven by one JSON file (comments allowed) — see
[config/server.json](config/server.json) for the fully commented reference.
Pass the path as the first argument, via `J0KERS_CONFIG`, or drop
`server.json` next to the binary. With no config at all, the server starts
with defaults and a `/test` tone mount.

Common environment overrides: `J0KERS_RTSP_PORT`, `J0KERS_HLS_PORT`,
`J0KERS_CONTROL_PORT`, `J0KERS_BIND_ADDRESS`, `J0KERS_LOG_LEVEL`.

Command-line flags override everything:

```bash
dotnet run -- -h 0.0.0.0 -r 8554 -H 8080 -c 9090
```

| Flag | Meaning |
|---|---|
| `-h`, `--host <ip>` | bind address / hostname (`0.0.0.0` = all interfaces, `localhost`) |
| `-r`, `--rtsp-port <port>` | RTSP port |
| `-H`, `--hls-port <port>` | HLS port |
| `-c`, `--control-port <port>` | control/dashboard port |
| `--help` | usage |

Precedence, lowest to highest: `server.json` → `settings.json` (dashboard
Config dialog) → `J0KERS_*` env vars → command-line flags.

### RTSP mounts

```json
"mounts": [
  { "path": "/test",  "source": "tone", "toneFrequencyHz": 440.0 },
  { "path": "/music", "source": "file", "file": "music.ulaw" }
]
```

- `tone` — built-in sine generator (G.711 µ-law, 8 kHz).
- `file` — loops a raw 8 kHz G.711 µ-law file. Produce one with:
  `ffmpeg -i song.mp3 -ar 8000 -ac 1 -f mulaw music.ulaw`

### Announcements (RFC 4240 style)

Drop `.ulaw` clips into `services.announcementClipDirectory` (default
`clips/`) and play them ad hoc, no mount needed:

```
rtsp://localhost:8554/annc?play=welcome.ulaw
```

### HLS

Each subdirectory of `hls.mediaRoot` (default `media/`) is one stream.
Segment with ffmpeg, then play `http://localhost:8080/<name>/index.m3u8`:

```bash
ffmpeg -i input.mp4 -c:v h264 -c:a aac -f hls -hls_time 6 -hls_list_size 0 media/demo/seg_%04d.ts
```

(The server generates its own playlist from the segments on disk; set
`liveWindowSegments` > 0 for live sliding-window playlists.)

## Control API

| Endpoint | Purpose |
|---|---|
| `GET /` | web dashboard when signed in, sign-in page when not (both embedded) |
| `GET /api/status` | identity, uptime, session counts |
| `GET /api/config` | effective config (token redacted) |
| `GET /api/mounts` | configured mounts + announcement URI |
| `GET /api/sessions` | who is watching: RTSP sessions and HLS viewers |
| `DELETE /api/sessions/{id}` | force-terminate a session |
| `GET /api/preview?mount=/x` | live WAV audio of a mount (dashboard player) |
| `GET /api/browse?path=C:\x` | drive / folder / file listing (dashboard picker; no `path` = drives) |
| `POST /api/mounts` | add a mount at runtime (persisted to `mounts.json`) |
| `DELETE /api/mounts?path=/x` | remove any mount (server.json mounts get a persisted tombstone) |
| `DELETE /api/hls?stream=x` | delete an HLS stream's files from the media root |
| `POST /api/play` `{file}` | transcode a media file to HLS (returns playlist path) |
| `GET/POST/DELETE /api/channels` | list / add / remove live channels (persisted to `channels.json`) |
| `GET/POST/DELETE /api/playlists` | list / save / forget folder playlists (persisted to `playlists.json`) |
| `GET/POST/DELETE /api/library` | list / add / remove library root folders (persisted to `library.json`) |
| `GET /api/thumb?path=` | cached JPEG thumbnail for a video or picture (ffmpeg) |
| `GET/POST/DELETE /api/favorites` | list / pin / unpin quick-button media (persisted to `favorites.json`) |
| `GET /api/codecs` | active transcode codecs + every encoder in the ffmpeg build |
| `GET http://<host>:<hlsPort>/watch/<stream>` | universal player page for a stream (works on phones; links the raw m3u8 for VLC) |
| `GET http://<host>:<hlsPort>/<stream>/subs.json` | subtitle tracks for a stream |
| `GET http://<host>:<hlsPort>/<stream>/subs/<id>.vtt` | a track as WebVTT (converted and cached on first request) |
| `POST /api/subtitles` `{stream, file}` | attach a subtitle file from disk to a stream |
| `GET /api/image?path=` | serve a picture for the library viewer |
| `POST /api/server/start` / `stop` | start / stop the streaming services |
| `GET/POST /api/settings` | read / save hostname + ports (persisted to `settings.json`) |
| `GET /api/auth/state` | is auth on, is setup needed, who am I |
| `POST /api/auth/setup` | create the first administrator account (first run only) |
| `POST /api/auth/login` / `logout` | password → session cookie / drop the session |
| `POST /api/auth/session` | key → session cookie (how a remembered device skips the form) |
| `GET /api/media/token[?stream=x]` | signed-link token for the media port (all streams, or one) |
| `POST /api/auth/password` | change your own password |
| `GET/POST/DELETE /api/auth/keys` | list / mint / revoke your own keys |
| `GET/POST/PUT/DELETE /api/users` | list / create / edit / remove accounts (admin) |
| `POST/DELETE /api/users/keys?id=` | mint / revoke a key for another account (admin) |

Every endpoint above is gated — see **Accounts and access** below.

## Accounts and access

Accounts live in a `users.json` sidecar next to the rest of the config.
There are two roles:

| | admin | user |
|---|---|---|
| watch the shared library, HLS streams, mounts, sessions | ✔ | ✔ |
| ⚙ Config, ⏻ Start/Stop, 👥 Users, 📁 Browse | ✔ | — |
| add/remove library folders, channels, mounts, playlists, favorites | ✔ | — |
| reach files *outside* the shared library | ✔ | — |

A **user** is confined to what has actually been shared: the library
folders, pinned favorites, and saved playlists. Asking `/api/play`,
`/api/thumb`, or `/api/image` for anything else is refused, so an account
handed to a houseguest can't transcode `C:\Users\you\taxes.pdf`.

Browsing to the dashboard prompts for a username and password first: `GET /`
serves a sign-in page, and the dashboard itself is never sent to a browser
that hasn't signed in. Signing out, or a session that expires mid-visit,
returns there.

### First run

With no accounts yet, that same page creates the first administrator
instead — pick a username and password and it signs you straight in. The
control API stays open until then, so existing scripts keep working right
up to the moment you claim the server.

### Two ways to sign in

**Passwords** are hashed with PBKDF2-HMAC-SHA256 (210 000 iterations, a
per-user 16-byte salt) and are never stored, logged, or accepted in a URL.
A successful sign-in returns an `HttpOnly; SameSite=Strict` session
cookie: page JavaScript can't read it, another site can't make the browser
send it, and it never appears in history or a `Referer`. Failed attempts
are throttled per account *and* per source address, escalating from 15
seconds to 15 minutes; the reply is identical for a wrong username and a
wrong password. Sessions live in memory only, so a restart signs everyone
out.

**Keys** are for everything that shouldn't see a login form — a phone, a
player, a script. Tick *Remember this device* when signing in and the key
is stored in that browser; on later visits the sign-in page trades it for
a session and forwards you to the dashboard without asking. Mint more from
👤 Account (yours) or 👥 Users (anyone's). A key is 256 bits of CSPRNG
output shown exactly once — only its SHA-256 digest is stored — and is
presented as a header or, where a media element can't set one, a query
parameter:

```bash
curl -H "Authorization: Bearer jmk_…" http://localhost:9090/api/status
curl "http://localhost:9090/api/thumb?path=…&key=jmk_…"
```

Revoking a key, disabling an account, or changing a password takes effect
on the very next request.

### Media: signed links, not sessions

A player is not a browser. VLC, a Chromecast, a smart TV and a bare
`<video>` element can fetch a URL and nothing else — no header, no login
form, often no cookie. So the HLS port authorizes by **signed URL**:

```
/movie/index.m3u8?exp=1785754138&sig=2i5ceBW1XUTa8Rvw1CG6fVoqmkx9jmLYz2eAFAeHWGw
```

`sig` is HMAC-SHA256 over the scope and the expiry, keyed by a per-install
secret in `signing.key`. `GET /api/media/token` mints one — no `stream=`
for an all-streams token (what the dashboard uses and refreshes on its
own), or `?stream=x` for a single-stream link to hand to a player. The
generated playlist carries the token through to every segment URI, because
players don't inherit a playlist's query string. Tokens expire (12 h by
default, `hls.linkLifetimeHours`), carry no identity, and grant playback
only — a leaked one costs an afternoon of access to one stream, not the
server.

A signed-in browser can also just browse to the media port directly:
cookies are scoped by host, not by port, so the control-port session works
there too.

RTSP asks for credentials the way every RTSP client already understands:

```bash
ffplay "rtsp://jay:mypassword@localhost:8554/test"
```

`OPTIONS` stays open so a client can discover the server and learn it needs
credentials; everything that reveals or delivers media does not. A key
works in place of the username, for a camera or set-top box you'd rather
not hand an account password. Set `rtsp.requireAuth: false` to leave RTSP
open.

> **Why Basic and not Digest.** Digest needs the server to hold
> `MD5(user:realm:password)` — a second, weak copy of every password sitting
> next to the PBKDF2 hashes, undoing the point of hashing them. Basic hands
> over the password, so it verifies against the real hash. It does put the
> password on the wire, which on a LAN already carrying unencrypted HTTP
> and RTP is the exposure the rest of the server already has.

Both media ports stay open until an account exists, so nothing breaks on a
server you haven't claimed.

State-changing requests carry two CSRF defenses: `Sec-Fetch-Site`/`Origin`
rejection for anything cross-site, plus a required `X-J0kers-CSRF` header
whenever the caller is authenticated by cookie — which a cross-origin page
cannot set without a preflight the browser will refuse.

The legacy `control.authToken` still works and still grants full rights,
so existing scripts keep running.

> **Plain HTTP.** The dashboard is served unencrypted, so on a network you
> don't trust, reach it over a VPN or an HTTPS reverse proxy. The sign-in
> dialog says so when it isn't on loopback or HTTPS.

## Layout

```
Auth/           accounts, password hashing, sessions, API keys, signed media links
Configuration/  JSON config model, env overrides, validation
Rtsp/           RTSP parser, server, sessions, SDP
Rtp/            RTP packetization, RTCP sender reports, port allocator
Hls/            RFC 8216 playlist generation + segment serving, viewer tracking
Control/        HTTP/JSON control API
Media/          G.711 sources (tone generator, file looper)
wwwroot/        dashboard single-page app + sign-in page (embedded into the binary)
config/         sample/default server.json (runtime media/ and clips/ live here too)
```

## Build

```bash
dotnet build
```

## Publish (standalone exe)

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Produces `publish\j0kers-media-server.exe` (single file, needs the .NET 10
runtime). Point a shortcut at it with the config path as the argument, e.g.
`j0kers-media-server.exe "D:\...\config\server.json"` — sidecar data
(mounts, channels, playlists, library, favorites, media cache) lives next
to the config file, so a published exe and `dotnet run` share the same
state. On Linux/macOS use `-r linux-x64` / `-r osx-arm64`.
