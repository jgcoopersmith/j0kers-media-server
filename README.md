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
| Network discovery — `.local` naming and service browsing | RFC 6762 (mDNS), RFC 6763 (DNS-SD) |

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

When the server is bound to `0.0.0.0` and more than one network is
connected, each stream in the **HLS streams** card carries a watch link per
network (📶 Wi-Fi, 🔌 Ethernet), and marks the one you're already browsing
on. A link built from whichever address you happen to have the dashboard
open on is the wrong one to hand to a phone on a different subnet. With a
single address nothing extra is drawn.

**Copy gives the stream URL** — the `.m3u8` playlist itself, which is what
VLC, Kodi and anything else that plays a URL want. For a browser, click the
stream's *name* instead: that opens the `/watch/` page, an HTML player that
works on phones. (The per-network row carries a **🎬 Link** button per
address, copying the stream URL on that network.) Every link carries the
signed token, and the playlist's segments carry it too, so a player follows
them without needing an account.

Copied links also avoid `localhost`: browsing the dashboard on the machine
itself would otherwise put `localhost` in every link, which on a phone means
the phone. A copied link falls back to the server's default-route address.

**On a machine with more than one network, use the per-network buttons.**
The default route is where *this PC* reaches the internet — not necessarily
the network the phone is on, and nothing here can know the other device's
network. A PC on both Ethernet `10.0.0.x` and Wi-Fi `192.168.8.x` hands out
the Ethernet address by default, and a phone on the Wi-Fi cannot route to
it: the link works on the PC and fails on the phone. The per-network row
lists every address with its own **🎬 Link** button — pick the
one matching the network the other device is on. The main buttons' tooltips
name the address they contain, so it is never a guess.

The **Transcodes** tile counts the conversions running and how far each has
got, read from ffmpeg's own `-progress` stream against the source duration.
The bar tracks the least-advanced job, since that's the one that decides
when everything is done; hover for a per-job breakdown. A source whose
length can't be probed shows elapsed output time and a dimmed bar rather
than a misleading percentage.

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
a **🌗 theme toggle** (light/dark, remembered per browser and followed by the
sign-in page; it tracks your system setting until you pick one), and a
**⚙ Config**
dialog for the hostname/bind address, the RTSP/HLS/control ports, and how
long share links live — saved to a `settings.json` sidecar and applied by restarting the
services live (a control-port change takes effect on the next full server
restart).

Every mount has a **▶ Play** button that streams its audio right in the
browser (the server feeds a live WAV over HTTP and the page plays it
gaplessly with Web Audio — no plugins). HLS streams play inline too, via
native HLS or hls.js.

The dashboard also ships a reusable **`pickPath()`** file browser (drives →
folders → files, backed by `/api/browse`): any dashboard feature can call
`await pickPath({ mode: "file" | "folder" | "any", title, startPath })` and
get an absolute path back, or `null` on cancel — it backs *Add folder*,
*Pin media*, the mount file picker, and *＋ Sub file*.

### Any media, one dashboard

With **ffmpeg** installed (`winget install Gyan.FFmpeg` — auto-detected, or
set `ffmpeg.path`), the dashboard becomes a full media center:

- **Media library** — add any number of source folders (➕ Add folder,
  removable chips, persisted server-side in `library.json`). Contents are
  grouped into **Folders / Videos / Music / Pictures** sections with
  counts; videos and pictures render as thumbnail tiles (ffmpeg frame
  grabs, cached under the media root) with icon fallbacks. Movies and
  music transcode to HLS on the fly and play inline; pictures open in a
  lightbox viewer. **🔄 Refresh** re-reads the open folder from disk —
  files added, removed or replaced since you opened it. Listings are read
  per request rather than cached, so this is a real rescan; it also forces
  poster frames to be re-fetched, which a replaced file otherwise wouldn't
  get, since the browser caches those for a day under an unchanged URL.
- **Search** — the box at the top of the card searches the library:
  browsing answers "what is in here", search answers "where is that film".
  **Selecting a library folder scopes the search to it** — opening one is
  itself an answer to "where?", so the scope box beside the field follows
  what you have open, down through subfolders. It lists every library
  folder, plus *Choose a folder…* for anything else, and **Everywhere**
  searches the lot; picking Everywhere by hand sticks until you next open a
  folder. Terms match the readable title as
  well as the file name, so `skyfall 2012` finds
  `Skyfall.2012.1080p.BluRay.x264-YIFY.mkv`, and all terms must match.
  Matching folders are listed too. **Enter** runs it again on the same
  text — a re-search after files have moved, since typing nothing fires
  nothing. Results replace the listing until the box is cleared (or Esc),
  which puts you back in the folder you were in.
  The walk is bounded at 300 results or 5 seconds and says which one it
  hit, so a library on a slow network drive can't hang the card.
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
  H.264/AAC. **📡 Import from tuner** adds a whole local lineup at once —
  see below.

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

### Local channels from an HDHomeRun

An HDHomeRun is an antenna with an HTTP server on it, so a whole local
lineup can be imported in one go: **📡 Import from tuner** in the Live
channels card, give it the tuner's address (`192.168.1.50`, or
`hdhomerun.local` — a full `http://…/lineup.json` is accepted too), and
press **Read lineup**.

What comes back is the tuner's own scan — it does the tuning and scanning,
from its app or web page; this only reads the result. Every channel is
listed with its number and station; tick the ones you want and **Import
selected**. Channels already added and copy-protected ones (`DRM`, cable
only) are shown but can't be picked, since importing either produces a row
that never plays.

Imported channels are saved **idle**, exactly like pinning a free-TV
channel — a restream is an ffmpeg process running around the clock, so
starting forty at once should never be one click. Start the ones you
actually watch from the list below. They're named `5.1 NBC`, number first,
so they sort the way a remote does and two subchannels of one station don't
collide.

Two practical limits: a tuner has a fixed number of tuners (usually 2 or
4), which caps how many channels can run at once; and over-the-air video is
MPEG-2, so leave `ffmpeg.liveVideoMode` at `transcode` — `copy` won't play
in a browser. Budget roughly one CPU core per running channel.

### Free TV

The **Free TV** card browses free ad-supported (FAST) lineups and plays
them here. **Pluto TV** is built in and needs no account or configuration —
around 400 channels, searchable and grouped by category. ▶ **Watch** plays
one in the dashboard; 📌 **pin** saves it to *Live channels* as a local
channel.

Pinning only saves it. A restream is an ffmpeg process pulling and
transcoding around the clock, so starting one is a separate, deliberate
press: a pinned channel sits **idle** until you press ▶ **Start** on its
row, and ■ **Stop** puts it back without forgetting it. That state is
persisted, so a restart brings back the ones that were actually running and
leaves the rest alone. Once started it has its own local HLS stream that
phones, TVs and VLC can play like any other.

The **group** filter appears only for providers that publish categories.
Pluto does; most playlist-backed ones carry no `group-title` at all, and a
filter whose only option is "all" is furniture.

Three view modes, remembered per browser:

| Mode | Layout | Shows |
|---|---|---|
| **Condensed** | grid of tiles | logo and name — 6 across at 1280px, 8 at 1600 |
| **Default** | one per row | logo, name, number · category |
| **Info** | one per row | larger logo plus the channel's description |

Condensed is a grid rather than a longer list, since a few hundred channels
one-per-row is the thing you were scrolling past in the first place. The
column count follows the card width (`auto-fill`), so a narrow window or a
phone gets fewer columns instead of a sideways scrollbar — down to a single
column at phone width. The tile itself is the Watch button; at that size a
separate one would be most of the tile. 📌 still pins.

Each mode caps how many it draws — 240, 60 and 25 — because what makes a
list unwieldy is its height rather than its length, and laying out several
hundred is slower than narrowing the search. Whatever is over the cap is
counted at the bottom rather than silently dropped.

The **HLS streams** and **RTSP mounts** cards carry the same three modes,
each remembered separately. Condensed is a grid — poster and title for a
stream, the mount path for a mount; info adds the playlist URL and source
for a stream, and the source, description and origin for a mount. Switching
view re-renders from what was already fetched rather than re-hitting the
API.

Only the playlists go through the server — a few KB of text every few
seconds. The video segments are fetched by the player straight from the
provider's CDN, so browsing a 400-channel lineup costs nothing until you
pin something. What the proxy is really for is the session token: Pluto's
expires, and a player refetches its playlist for as long as the channel is
on, so each fetch is re-authorized on the way through. That is also why a
pinned channel points at this server's own `/api/tv/watch` URL rather than
at the provider — the restream survives the token rolling over.

Ads are stitched into these streams by the provider and are passed through
untouched. The segments carry HLS `AES-128` (RFC 8216 §4.3.2.4) with the
key served openly next to them — transport encryption that ffmpeg and
hls.js handle unaided, not DRM, and nothing here circumvents a licence
server.

**Other services** go in a `providers.json` sidecar next to your config,
written with a commented template on first run. Uncomment what you want:

```json
[
  { "id": "tubi", "name": "Tubi",
    "url": "https://raw.githubusercontent.com/iptv-org/iptv/master/streams/us_tubi.m3u",
    "enabled": true },
  { "id": "roku", "name": "The Roku Channel",
    "url": "https://raw.githubusercontent.com/iptv-org/iptv/master/streams/us_roku.m3u",
    "enabled": true },
  { "id": "samsung", "name": "Samsung TV Plus",
    "url": "https://raw.githubusercontent.com/iptv-org/iptv/master/streams/us_samsung.m3u",
    "enabled": true }
]
```

Restart to pick up changes — the file is read at startup. That gives Tubi
(~176), The Roku Channel (~32) and Samsung TV Plus (~410) alongside Pluto,
all clear HLS with no DRM and none needing `relaySegments`. Individual
channels come and go in a community playlist, so an occasional dead one is
normal rather than a fault here — the lineup loads, that channel 404s.

Anything that is an extended-M3U playlist works — `group-title`, `tvg-logo`
and `tvg-id` are read, and `tvg-id` is preferred as the channel's identity
so a pinned channel keeps resolving when the playlist is refetched. This
is how the services with no usable public API of their own — Tubi, The Roku
Channel, Samsung TV Plus — are reached: aim at a playlist somebody keeps
current and the churn stays with whoever maintains it, rather than in this
codebase. Add `"relaySegments": true` if playback fails with a CORS error
in the browser console; it routes the video through the server too, at the
cost of the bandwidth.

**Sling Freestream is not supported.** Its streams are Widevine DRM, so no
playlist or proxy can make them play here — that would mean breaking the
DRM, which this server does not do.

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

### Recently watched

The Sessions card only ever shows what is *live*, so a **Recently watched**
dropdown sits at the top of it with the last ten things you played. Pick one
to play it again. Each is marked `✓` for already watched — the one still on
screen is `▶` instead — with how long ago it was and how many times it has
been played. Titles whose file has since been deleted are shown as
`gone from disk` and can't be picked. Watching something a second time moves
it back to the top and counts the plays rather than repeating the title.

History is per account — you see yours and nobody else's — and is kept in
a `history.json` sidecar next to the config, fifty entries deep.

### Logging

A **Log** card sits above Sessions on the dashboard, showing what the
console window shows — the last 500 lines, scrollable, colour-coded by
level, with a filter (errors only … everything) applied to the lines
already in hand rather than only to new ones. It follows the newest line
until you scroll up, and follows again when you scroll back to the bottom.
Lines are fetched by sequence number, so a poll collects the two new ones
rather than the last five hundred. Administrators only — the log names file
paths, accounts and client addresses.

Anything that fails *before* the control API is listening — a broken
`server.json`, a port already taken — never reaches the dashboard, because
there is no dashboard yet. The log file below is what has those.

The server logs to the console and, unless you turn it off, to a rotating
file — tray mode hides the console entirely, so without the file nothing
survives the session. Everything below is in the dashboard's ⚙ Config
dialog and applies immediately, no restart:

```json
"logging": {
  "level": "info",          // trace | debug | info | warn | error
  "toFile": true,
  "directory": "logs",      // relative = next to server.json
  "rotateSizeMb": 10,       // 0 = never rotate on size
  "rotatePeriod": "daily",  // none | hourly | daily | weekly | monthly
  "maxFiles": 7
}
```

The current log is `<directory>/j0kers.log`. **Period and size combine** —
whichever comes first starts a new file, and either can be switched off
(`"none"` / `0`). Set both to off and you get one file that grows forever.
Rotated files are named `j0kers-<date>-<time>.log`; the newest `maxFiles`
of them are kept and older ones are deleted, so the log has a fixed
ceiling of roughly `rotateSizeMb × (maxFiles + 1)`.

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
| `GET /api/status` | identity, uptime, session counts, transcode progress |
| `GET /api/config` | effective config (token redacted) |
| `GET /api/mounts` | configured mounts + announcement URI |
| `GET /api/sessions` | who is watching: RTSP sessions and HLS viewers |
| `GET /api/log?since=…` | log lines after a sequence number, from a 500-line in-memory ring (admin) |
| `GET/DELETE /api/history` | the caller's last watched items (`?count=`, default 10); DELETE forgets one `?path=` or all of them |
| `DELETE /api/sessions/{id}` | force-terminate a session |
| `GET /api/preview?mount=/x` | live WAV audio of a mount (dashboard player) |
| `GET /api/browse?path=C:\x` | drive / folder / file listing (dashboard picker; no `path` = drives) |
| `POST /api/mounts` | add a mount at runtime (persisted to `mounts.json`) |
| `DELETE /api/mounts?path=/x` | remove any mount (server.json mounts get a persisted tombstone) |
| `DELETE /api/hls?stream=x` | delete an HLS stream's files from the media root |
| `POST /api/play` `{file}` | transcode a media file to HLS (returns playlist path) |
| `GET/POST/DELETE /api/channels` | list / add / remove live channels (persisted to `channels.json`) |
| `GET/POST /api/dlna` | which library folders DLNA may show; POST takes the whole list (`{"folders":[…]}`) |
| `GET /api/tuner?host=…` | an HDHomeRun's identity and channel lineup, each channel flagged if already added |
| `POST /api/channels/import` | save a batch of channels idle; per-channel failures are reported, not thrown |
| `GET/POST/DELETE /api/playlists` | list / save / forget folder playlists (persisted to `playlists.json`) |
| `GET/POST/DELETE /api/library` | list / add / remove library root folders (persisted to `library.json`) |
| `GET /api/library/search?q=…` | playable files and folders matching every term; all library roots, or one `&folder=` inside them |
| `GET /api/thumb?path=` | cached JPEG thumbnail for a video or picture (ffmpeg) |
| `GET/POST/DELETE /api/favorites` | list / pin / unpin quick-button media (persisted to `favorites.json`) |
| `GET /api/codecs` | active transcode codecs + every encoder in the ffmpeg build |
| `GET /api/tv/providers` | free-TV providers available (built-in Pluto TV + `providers.json`) |
| `GET /api/tv/lineup?provider=&q=&group=` | a provider's channels, optionally filtered |
| `GET /api/tv/watch?provider=&id=&s=` | freshly authorized HLS for one channel (signature or account) |
| `POST /api/tv/pin` `{provider,id,name}` | save a provider channel as a local channel (idle; start it separately) |
| `POST /api/channels/start?name=` | start a saved channel's restream (remembered across restarts) |
| `POST /api/channels/stop?name=` | stop the restream, keeping the channel |
| `GET http://<host>:<hlsPort>/watch/<stream>` | universal player page for a stream (works on phones; links the raw m3u8 for VLC) |
| `GET http://<host>:<hlsPort>/<stream>/subs.json` | subtitle tracks for a stream |
| `GET http://<host>:<hlsPort>/<stream>/subs/<id>.vtt` | a track as WebVTT (converted and cached on first request) |
| `POST /api/subtitles` `{stream, file}` | attach a subtitle file from disk to a stream |
| `GET /api/image?path=` | serve a picture for the library viewer |
| `POST /api/server/start` / `stop` | start / stop the streaming services |
| `GET/POST /api/settings` | read / save hostname, ports, share-link lifetime (persisted to `settings.json`) |
| `GET /api/auth/state` | is auth on, is setup needed, who am I |
| `POST /api/auth/setup` | create the first administrator account (first run only) |
| `POST /api/auth/login` / `logout` | password → session cookie / drop the session |
| `POST /api/auth/session` | key → session cookie (how a remembered device skips the form) |
| `GET /api/media/token[?stream=x]` | signed-link token for the media port (all streams, or one) |
| `POST /api/auth/password` | change your own password |
| `GET/POST/DELETE /api/auth/keys` | list / mint / revoke your own keys |
| `GET/POST/PUT/DELETE /api/users` | list / create / edit / remove accounts (admin only) |
| `POST/DELETE /api/users/keys?id=` | mint / revoke a key for another account (admin) |

Every endpoint above is gated — see **Accounts and access** below.

## Accounts and access

Accounts live in a `users.json` sidecar next to the rest of the config.
There are three roles:

| | admin | edit | read |
|---|---|---|---|
| watch the shared library, HLS streams, mounts, sessions | ✔ | ✔ | ✔ |
| add/remove library folders, channels, mounts, playlists, favorites | ✔ | ✔ | — |
| delete HLS streams, attach subtitles, browse this machine | ✔ | ✔ | — |
| reach files *outside* the shared library | ✔ | ✔ | — |
| ⚙ Config, ⏻ Start/Stop, terminate someone's session | ✔ | — | — |
| 👥 Users — create, edit, and remove accounts | ✔ | — | — |

The split is between what the server *runs* and what it *offers*. **Admin**
owns the former: ports, bind address, the power button, and the accounts
themselves — only an admin can add a user. **Edit** owns the latter: it
curates the library but never sees the Config dialog. **Read** watches and
nothing else.

A **read** account is confined to what has actually been shared: the
library folders, pinned favorites, and saved playlists. Asking `/api/play`,
`/api/thumb`, or `/api/image` for anything else is refused, so an account
handed to a houseguest can't transcode `C:\Users\you\taxes.pdf`. Edit
accounts aren't confined — they add the library folders in the first
place, so the restriction would be theatre.

The pre-three-tier role name `user` is still read as **read**, so an
existing `users.json` keeps working.

Browsing to the dashboard prompts for a username and password first: `GET /`
serves a sign-in page, and the dashboard itself is never sent to a browser
that hasn't signed in. Signing out, or a session that expires mid-visit,
returns there.

### Finding the server without typing an IP

The server announces itself on the local network, so devices can find it by
name. Three mechanisms, because no single one reaches everything:

| Mechanism | Port | Who uses it |
|---|---|---|
| **mDNS / DNS-SD** (RFC 6762/6763) | udp/5353 | `.local` names on phones, Macs, Linux |
| **SSDP** (UPnP discovery) | udp/1900 | Windows Explorer's Network folder, smart TVs |
| **DLNA** (off by default) | control port | a TV's *Media Server* input — see below |
| **UDP probe** | udp/7359 | scripts and apps; the port Jellyfin uses |

The one that matters day to day is mDNS: **http://j0kers.local:9090/**
works from any device on the network, and it resolves to *whichever address
that device can reach*. A PC on both Ethernet `10.0.0.x` and Wi-Fi
`192.168.8.x` answers a phone with the Wi-Fi address and a wired machine
with the wired one — the thing a copied link can't do, since a link has to
name one address up front. It also survives a DHCP lease changing.

Announcing says only that the server exists and where. It grants nothing:
the dashboard still wants an account and media still wants a signed link.

Turn it off with the **Announce this server on the local network** switch in
the ⚙ Config dialog — applies immediately, and the responders send their
goodbyes and release the ports, so listeners drop the entry rather than
keeping a dead one. `discovery` in the config has the finer switches
(`mdns`, `ssdp`, `udpProbe`) and sets the published name via `hostName`.

Other software on the machine may already hold these ports — Bonjour ships
with a lot of things, and Windows runs its own SSDP service. That is
expected and shared rather than exclusive; a mechanism that cannot start
logs it and the others carry on.

### DLNA — TVs and players with no browser

Tick **Serve the library over DLNA** in the ⚙ Config dialog (or set
`discovery.dlna: true`) and the server appears in a TV's *Media Server*
input, where the library folders can be browsed with the remote. It applies
immediately — the server re-announces itself as a UPnP `MediaServer:1`
rather than a generic device.

This is a different shape from everything else here. The client browses a
tree over SOAP and then fetches **the whole file** over HTTP with byte
ranges — no playlists, no segments, **no transcoding**. What plays is
whatever the device itself can decode, so a TV that can't handle a codec
will refuse the file rather than being handed a converted stream. Seeking
works (`DLNA.ORG_OP=01`), so scrubbing through a film is fine.

Only the **library folders** are served, arranged folders-first and
alphabetically, with the same readable titles the dashboard shows. Live
channels and HLS streams are deliberately absent: DLNA clients can't play
a playlist.

**Choosing what goes out.** Under the switch is the library folder list,
each with a tick, editable in place: **+ Add folder** picks one and ✕ takes
one out. These are the media library's own folders, so adding one here adds
it to the dashboard's library too, and removing one takes it out of both —
the confirmation says so, and nothing on disk is touched. A folder added
here starts ticked, since sharing it is why it was added. Until you change it every library folder is shared —
turning DLNA on should not quietly hide half the library — and after that
the choice is kept literally, the empty one included: sharing nothing is a
legitimate answer and doesn't spring back to everything on restart. Ticks
apply on **Save & apply**, so a stray click doesn't publish a folder the
moment it lands. An unshared folder isn't merely hidden from the listing:
its files can't be named or fetched either, so an id kept from before it
was unshared returns nothing. The choice lives in a `dlna.json` sidecar.

> **DLNA has no sign-in.** The protocol carries no account, cookie or
> token — a TV that finds a media server expects to browse it. Switching
> this on shares every library folder with every device on the network.
> That is why it is off by default. The server refuses DLNA requests from
> anything that is not a private LAN address, which is the only boundary
> the protocol allows, and every object id is re-checked against the
> library roots before it names a file.

Endpoints, all unauthenticated by necessity and all LAN-only:
`/dlna/cds.xml`, `/dlna/cm.xml` (service descriptions), `/dlna/control`
(SOAP), `/dlna/events` (subscriptions accepted, never fired — nothing here
changes mid-browse), `/dlna/file?id=` (the file, with ranges).

### If a settings file gets damaged

The sidecars next to your config — `favorites.json`, `library.json`,
`playlists.json`, `channels.json`, `settings.json`, `mounts.json` — are
written through a temp file and moved into place, so losing power or
force-quitting mid-save can't leave one half-written.

If one is damaged anyway, the server moves it to `<name>.json.corrupt`,
says so in the log, and starts that store empty rather than carrying on and
overwriting it with the next change. The old contents stay in the
`.corrupt` file: fix the JSON, rename it back, restart.

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
players don't inherit a playlist's query string. Tokens expire (7 days by
default — `hls.linkLifetimeHours`, or *Share links expire after* in the
⚙ Config dialog), carry no identity, and grant playback only — a leaked one costs an afternoon of access to one stream, not the
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
Media/          G.711 sources (tone generator, file looper), ffmpeg engine
                (JsonSidecar: atomic writes + quarantine for the .json stores)
Media/Providers/ free-TV lineups (Pluto TV, M3U playlists) + the HLS proxy
Discovery/      mDNS/DNS-SD, SSDP and UDP-probe responders (announcing on the LAN)
wwwroot/        dashboard single-page app + sign-in page (embedded into the binary)
config/         sample/default server.json (runtime media/ and clips/ live here too)
```

## Versioning

`.githooks/pre-commit` bumps the patch component of `<Version>` in the
csproj on every commit, so each build reports a unique, increasing version
— shown in the dashboard header and returned by `GET /api/status`. Never
edit `<Version>` by hand. New clones need it switched on once:

```bash
git config core.hooksPath .githooks
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
