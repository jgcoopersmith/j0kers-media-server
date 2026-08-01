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
| `GET /api/status` | identity, uptime, session counts |
| `GET /api/config` | effective config (token redacted) |
| `GET /api/mounts` | configured mounts + announcement URI |
| `GET /api/sessions` | live RTSP sessions with RTP stats |
| `DELETE /api/sessions/{id}` | force-terminate a session |

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
config/         sample/default server.json (runtime media/ and clips/ live here too)
```

## Build

```bash
dotnet build
```
