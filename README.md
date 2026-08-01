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
terminate buttons, RTP throughput chart, mounts with copyable `rtsp://`
URIs, HLS stream list, and the effective config. (Set
`control.openDashboardOnStart` to `false` for headless use.)

The header has a **⏻ Start/Stop** button that stops or starts the streaming
services (RTSP + HLS) while the dashboard stays up, and a **⚙ Config**
dialog for the server name, hostname/bind address, and the RTSP/HLS/control
ports — saved to a `settings.json` sidecar and applied by restarting the
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

- **Media library** — pick any folder; movies (`mp4/mkv/avi/…`) and music
  (`mp3/flac/…`) transcode to HLS on the fly and play inline (converted
  once, cached under the HLS media root), and pictures open in a lightbox
  viewer.
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
`server.json` is never rewritten) and carry a ✕ button to remove them
again. Mounts defined in `server.json` stay read-only in the GUI.

Check the control API:

```bash
curl http://localhost:9090/api/status
curl http://localhost:9090/api/sessions
```

## Configuration

Everything is driven by one JSON file (comments allowed) — see
[config/server.json](config/server.json) for the fully commented reference.
Pass the path as the first argument, via `J0KERS_CONFIG`, or drop
`server.json` next to the binary. With no config at all, the server starts
with defaults and a `/test` tone mount.

Common environment overrides: `J0KERS_RTSP_PORT`, `J0KERS_HLS_PORT`,
`J0KERS_CONTROL_PORT`, `J0KERS_BIND_ADDRESS`, `J0KERS_LOG_LEVEL`.

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
| `GET /` | web dashboard (embedded, no extra files needed) |
| `GET /api/status` | identity, uptime, session counts |
| `GET /api/config` | effective config (token redacted) |
| `GET /api/mounts` | configured mounts + announcement URI |
| `GET /api/sessions` | live RTSP sessions with RTP stats |
| `DELETE /api/sessions/{id}` | force-terminate a session |
| `GET /api/preview?mount=/x` | live WAV audio of a mount (dashboard player) |
| `GET /api/browse?path=C:\x` | drive / folder / file listing (dashboard picker; no `path` = drives) |
| `POST /api/mounts` | add a mount at runtime (persisted to `mounts.json`) |
| `DELETE /api/mounts?path=/x` | remove a runtime-added mount |
| `POST /api/play` `{file}` | transcode a media file to HLS (returns playlist path) |
| `GET/POST/DELETE /api/channels` | list / add / remove live channels (persisted to `channels.json`) |
| `GET /api/image?path=` | serve a picture for the library viewer |
| `POST /api/server/start` / `stop` | start / stop the streaming services |
| `GET/POST /api/settings` | read / save hostname + ports (persisted to `settings.json`) |

Binds to loopback by default; set `control.authToken` before exposing it
more widely.

## Layout

```
Configuration/  JSON config model, env overrides, validation
Rtsp/           RTSP parser, server, sessions, SDP
Rtp/            RTP packetization, RTCP sender reports, port allocator
Hls/            RFC 8216 playlist generation + segment serving
Control/        HTTP/JSON control API
Media/          G.711 sources (tone generator, file looper)
wwwroot/        dashboard single-page app (embedded into the binary)
config/         sample/default server.json (runtime media/ and clips/ live here too)
```

## Build

```bash
dotnet build
```
