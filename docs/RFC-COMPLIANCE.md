# RFC Compliance Notes

How j0kers Media Server maps to each of the guiding RFCs, and where it
deliberately stops short. This is a compact, pragmatic implementation — the
goal is correct-on-the-wire behavior for the common paths, not exhaustive
coverage of every MUST in each document.

## RFC 2326 — Real Time Streaming Protocol (RTSP 1.0)

The wire protocol spoken by `RtspServer`. Version string `RTSP/1.0` is used
because that is what deployed clients (VLC, ffmpeg, GStreamer) interoperate
with.

- Methods: OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER,
  SET_PARAMETER. Unknown methods get `501 Not Implemented` with a `Public`
  header.
- Headers: CSeq echo, Session (with `timeout=` parameter), Transport
  negotiation, Content-Base on DESCRIBE, Range and RTP-Info on PLAY.
- SDP presentation descriptions per Appendix C.
- Not implemented: RECORD/ANNOUNCE (server is play-only), REDIRECT,
  multicast transports, RTSP proxies/caching.

## RFC 7826 — RTSP 2.0

Used as the design reference for the state machine, status codes, and
framing even though the wire version is 1.0 (RTSP 2.0 has effectively no
client deployment):

- Session state machine (§13): Ready ↔ Playing per session, `454 Session Not
  Found`, `455/461` error semantics.
- Interleaved binary framing `$`-prefixed over the control TCP connection
  (§14) for `RTP/AVP/TCP` transports.
- Keep-alive via empty GET_PARAMETER (§18.19) and session expiry after the
  advertised timeout (§18.49).
- Sessions on interleaved transports are torn down with the connection;
  UDP sessions persist to TEARDOWN or timeout (§13.1).

## RFC 3550 — RTP: A Transport Protocol for Real-Time Applications

`RtpSender` implements:

- Fixed 12-byte RTP header (§5.1): V=2, marker on the first packet of a
  talkspurt, random initial sequence number / timestamp / SSRC.
- Timestamps advance by samples-per-frame at the media clock rate (8 kHz).
- RTCP Sender Reports (§6.4.1) with NTP + RTP timestamp pairs and
  packet/octet counts, sent on a configurable interval (default 5 s, §6.2).
- Even/odd port pairing: RTP on the even port, RTCP on the next odd port
  (§11), enforced by the port allocator and by config validation.
- Payload: PCMU/8000 static payload type 0 per RFC 3551 §6.
- Not implemented: RTCP receiver-report processing, jitter computation from
  received RRs, SDES/BYE/APP packets, header extensions, mixers/translators.

## RFC 8216 — HTTP Live Streaming

`HlsServer` generates Media Playlists from segment files on disk:

- Tags: EXTM3U, EXT-X-VERSION:3, EXT-X-TARGETDURATION, EXT-X-MEDIA-SEQUENCE,
  EXTINF, EXT-X-PLAYLIST-TYPE:VOD, EXT-X-ENDLIST.
- VOD mode (default) and sliding-window live mode (`liveWindowSegments > 0`),
  with EXT-X-MEDIA-SEQUENCE advancing as the window slides (§6.2.2).
- Correct MIME types: `application/vnd.apple.mpegurl`, `video/mp2t`.
- Segment production (TS/fMP4 encoding) is out of scope — point a segmenter
  such as ffmpeg at a subdirectory of `hls.mediaRoot`:
  `ffmpeg -i input.mp4 -c copy -f hls -hls_time 6 media/mystream/index_%03d.ts ...`
  Exact per-segment durations can be provided via `<segment>.duration`
  sidecar files; otherwise EXTINF uses the target duration.
- Not implemented: Master Playlists / variant selection, encryption
  (EXT-X-KEY), byte-range segments, EXT-X-DISCONTINUITY.

## RFC 9317 — Operational Considerations for Streaming Media

Informational; addressed through configuration and behavior:

- Both stateful (RTSP/RTP) and HTTP-adaptive (HLS) delivery are offered, per
  the document's discussion of delivery trade-offs.
- HLS responses carry `Cache-Control: no-cache` for playlists so caches
  revalidate live playlists, and CORS is configurable for web players.
- Port ranges, bind addresses, and session caps are all configurable so
  operators can fit the server into NAT/firewall policies (§2–4 concerns).
- DSCP marking is exposed in config as an operator knob (default off).

## RFC 5167 — Media Server Control Protocol Requirements

Requirements document (no wire format). The HTTP/JSON control API satisfies
the spirit of the core requirements from the perspective of a controlling
application:

- Auditing of active sessions and resources (REQ-MCP-08/09):
  `GET /api/sessions`, `GET /api/status`.
- Explicit session termination by the controller: `DELETE /api/sessions/{id}`.
- Transport security hooks: bearer-token auth; bind to loopback by default.
- Extensible resource description: `GET /api/mounts`, `GET /api/config`.

## RFC 4240 — Basic Network Media Services with SIP

RFC 4240 defines service URI conventions (`annc@ms`, `dialog@ms`,
`conf@ms`) for SIP-controlled media servers. This server is RTSP-controlled,
so the *announcement* convention is adapted to RTSP URI form:

    rtsp://host:8554/annc?play=<clip-name>

- The `play=` parameter selects a clip, resolved strictly inside the
  configured `services.announcementClipDirectory` (no path escapes).
- Missing/unknown clips return `404`, matching RFC 4240 §2.3's "announcement
  resource unavailable" behavior.
- `dialog` and `conf` services (VoiceXML IVR, conference mixing) are not
  implemented; the URI routing layer in `RtspServer.ResolveMount` is where
  they would attach.
