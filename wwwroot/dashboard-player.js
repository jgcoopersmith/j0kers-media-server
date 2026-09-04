/* The inline player: subtitle tracks, the transport controls, the hls.js
   bootstrap for browsers without native HLS, and handing playback to a tab
   of its own. Split out of dashboard.html; see dashboard-core.js for why
   every function here stays global. */
"use strict";
/* ---- subtitles ----
   Tracks are discovered per stream (embedded tracks + sidecar files next
   to the media) and served as WebVTT by the HLS server; users can also
   attach any subtitle file by hand. */
let currentStreamName = null;

function streamNameFromUrl(url) {
  const m = url.match(/\/([^/]+)\/index\.m3u8/);
  return m ? decodeURIComponent(m[1]) : null;
}

/* The name to show over the picture.
   Prefers the readable title the server already derived for the stream list
   over the stream id, which is a slug with a hash on the end. A free-TV
   channel has no stream of its own — it is proxied — so its name comes from
   the lineup instead. */
function playerTitleFor(url) {
  const tv = url.match(/[?&]id=([^&]+)/);
  if (url.startsWith("/api/tv/") && tv) {
    const id = decodeURIComponent(tv[1]);
    const ch = (typeof lineup !== "undefined" ? lineup : []).find(c => c.id === id);
    if (ch) return ch.name;
  }
  const name = streamNameFromUrl(url);
  if (!name) return "";
  const known = lastHls && lastHls.streams
    ? lastHls.streams.find(s => s.name === name) : null;
  return (known && known.title) || name;
}

/* Names what is playing, and leaves it named. It sits above the picture
   rather than on it, so there is nothing to fade out of the way — it stays
   until playback stops. */
function setPlayerTitle(text) {
  $("vtitle-text").textContent = text || "";
  $("vtitle").classList.toggle("hidden", !text);
}

async function loadSubtitles(url) {
  const video = $("hlsvideo"), sel = $("pc-subs");
  // clear previous tracks
  [...video.querySelectorAll("track")].forEach(t => t.remove());
  sel.innerHTML = '<option value="">Off</option>';
  currentStreamName = streamNameFromUrl(url);
  if (!currentStreamName) return;

  // Keep the signed token. Slicing the playlist URL at its last "/" also
  // cuts off "?exp=…&sig=…", which left this asking the media port for a
  // track list with nothing to authorize it — and the media port is a
  // different origin from the dashboard, so fetch withholds the cookie that
  // would otherwise have covered it. The request was refused every time, and
  // the only sign was an "unauthorized … subs.json" line in the log while
  // the film itself played normally.
  const q = url.indexOf("?");
  const pathOnly = q === -1 ? url : url.slice(0, q);
  const query = q === -1 ? "" : url.slice(q);
  const base = pathOnly.slice(0, pathOnly.lastIndexOf("/"));
  let data;
  try {
    const r = await fetch(base + "/subs.json" + query);
    if (!r.ok) return;
    data = await r.json();
  } catch { return; }

  let unsupported = 0;
  for (const t of data.tracks || []) {
    if (!t.supported) { unsupported++; continue; }
    const track = document.createElement("track");
    track.kind = "subtitles";
    track.id = t.id;               // matched on, so duplicate labels don't collide
    track.label = t.label;
    if (t.language) track.srclang = t.language;
    track.src = base + "/subs/" + encodeURIComponent(t.id) + ".vtt";
    video.appendChild(track);
    const opt = new Option(t.label, t.id);
    opt.dataset.lang = t.language || "";
    sel.appendChild(opt);
  }
  if (unsupported) {
    const dead = new Option(`(${unsupported} image-based track${unsupported > 1 ? "s" : ""} — not shown)`, "__none");
    dead.disabled = true;
    sel.appendChild(dead);
  }

  // remember the chosen LANGUAGE (not the positional id) so "English on"
  // carries across movies without enabling the wrong track
  const wantLang = prefGet("j0kers-sub-lang") || "";
  if (wantLang) {
    const match = [...sel.options].find(o => o.dataset && o.dataset.lang === wantLang);
    if (match) { sel.value = match.value; applySubtitleChoice(false); }
  }
}

function applySubtitleChoice(remember = true) {
  const video = $("hlsvideo"), sel = $("pc-subs"), want = sel.value;
  for (const tt of video.textTracks)
    tt.mode = want && tt.id === want ? "showing" : "disabled";
  if (remember) {
    const opt = [...sel.options].find(o => o.value === want);
    prefSet("j0kers-sub-lang", (opt && opt.dataset && opt.dataset.lang) || "");
  }
}

$("pc-subs").addEventListener("change", applySubtitleChoice);

$("pc-subadd").addEventListener("click", async () => {
  if (!currentStreamName) { alert("Play a stream first, then attach a subtitle file to it."); return; }
  const file = await pickPath({ mode: "file", title: "Pick a subtitle file (.srt, .ass, .vtt…)" });
  if (!file) return;
  const r = await fetch("/api/subtitles", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers() },
    body: JSON.stringify({ stream: currentStreamName, file }),
  });
  const data = await r.json().catch(() => ({}));
  if (!r.ok) { alert(data.error || "could not attach that file"); return; }
  // reload the list and switch to the new track
  const url = mediaUrl("/" + encodeURIComponent(currentStreamName) + "/index.m3u8");
  await loadSubtitles(url);
  $("pc-subs").value = data.added;
  applySubtitleChoice();
});

/* Puts the player where it can actually be watched.
   The player sits underneath the stream list, which on a server with a few
   streams is well over a thousand pixels tall — so picking a film scrolled
   the list into view and left the video that far below the fold, playing
   to nobody. Called whenever playback starts, whatever started it.

   It only scrolls when the player is genuinely not on screen. A playlist
   advancing to its next track calls this too, and someone already watching
   should not have the page tugged out from under them each time. */
function bringPlayerIntoView() {
  const box = $("hlsplayer");
  // A timeout rather than requestAnimationFrame: rAF does not run in a
  // background tab, so starting something there and switching to it would
  // arrive with the player still out of sight. Either way this waits a beat
  // for the display change above to be laid out before measuring.
  setTimeout(() => {
    const r = box.getBoundingClientRect();
    const h = window.innerHeight || document.documentElement.clientHeight;
    if (!h || !r.height) return;                       // not laid out yet
    const visible = Math.min(r.bottom, h) - Math.max(r.top, 0);
    if (visible >= Math.min(r.height, h) * 0.5) return; // already watchable
    box.scrollIntoView({ behavior: "smooth", block: "center" });
  }, 0);
}

/* Stop button for HLS/mount video playback: ends whatever the inline
   player is doing (single stream or playlist) and hides it. */
function stopHlsVideo() {
  stopQueue(); // tears down the video element and any playlist state
  setPlayerTitle("");   // nothing playing, nothing to name
  currentMediaPath = null;
  $("hlsplayer").style.display = "none";
  $("hlsmsg").textContent = "";
}

/* ---- player controls: seek, speed, quality ----
   The position playHls still intends to enforce once playback begins, or
   null once the viewer has picked a spot themselves. See playHls: the
   correction that keeps a live-looking transcode from starting at its edge
   must never override a deliberate seek. */
let pendingStart = null;

/* Anything the viewer does to choose a position. A player's own jump to the
   live edge never comes with a click or a keypress, which is what makes
   this a usable signal of intent. */
function viewerTookControl() { pendingStart = null; }

/* How far into this stream playback can actually go right now.
   duration is the wrong bound while a file is still being converted: the
   playlist grows as ffmpeg writes it, so duration reports only what exists
   so far and keeps moving. Seeking to it lands on a fragment that has not
   been written, which the player answers with an error and a recovery that
   restarts the stream — the film jumping back to the beginning after enough
   presses to reach the converted edge. seekable is the honest bound. */
function playableEnd(v) {
  if (v.seekable && v.seekable.length) return v.seekable.end(v.seekable.length - 1);
  return isFinite(v.duration) ? v.duration : Infinity;
}

/* How far the skip buttons and the arrow keys move, in seconds. One
   constant, used by both, and the same number the watch page uses — a skip
   that means something different depending on which player you happen to be
   in is worse than either value. */
const SKIP_SECONDS = 15;

function seekPlayerBy(delta) {
  viewerTookControl();
  const v = $("hlsvideo");
  if (delta < 0) { v.currentTime = Math.max(0, v.currentTime + delta); return; }
  // Anywhere in the film. The playlist covers its whole length from the
  // first moment now, and a segment that has not been converted yet is made
  // when the player asks for it, so there is no converted edge to stop at.
  const end = isFinite(v.duration) ? v.duration : playableEnd(v);
  const wanted = v.currentTime + delta;
  v.currentTime = isFinite(end) ? Math.min(wanted, Math.max(0, end - 0.5)) : wanted;
}
$("pc-back").addEventListener("click", () => seekPlayerBy(-SKIP_SECONDS));
$("pc-fwd").addEventListener("click", () => seekPlayerBy(SKIP_SECONDS));

/* Keyboard controls for the dashboard player: arrows skip, space or K plays
   and pauses.

   Space has been wrong twice, both times because I reasoned about what the
   browser would do with it instead of measuring, and both times the guess
   was that one toggle was happening when two were. The video element brings
   its own handling of space and I cannot reliably observe, from here,
   whether a given browser's control bar acts before this handler, after it,
   or at all.

   So this no longer depends on knowing. It states an intention — play, or
   pause — and then holds the player to it for a moment afterwards. If
   anything flips the player back within that window, whatever it was and
   whenever it ran, it is put right. One keypress produces one outcome even
   if two handlers fire, and it would still be correct if a browser stopped
   double-firing tomorrow.

   The window is short and cancelled by any click on the player, so pressing
   space and then immediately clicking pause does what you asked, rather than
   being overruled by the key you pressed a moment earlier.

   The listener is registered in the CAPTURE phase, and that part is not
   decoration. Holding the player to an intention only works if the
   intention was read before anything else touched it: a listener on the
   video runs in the target phase, which is after document capture and
   before document bubble. Registered as a bubble listener — which is what
   it was — a press while paused could be read as "it is playing, so pause
   it", because something else had already started it. Capture is what makes
   "what state was it in when the key went down" a question with one answer. */
const TOGGLE_HOLD_MS = 500;
let toggleWant = null, toggleAt = 0;

function togglePlayback() {
  const v = $("hlsvideo");
  viewerTookControl();                 // a deliberate act: don't drag them elsewhere
  toggleWant = v.paused ? "play" : "pause";
  toggleAt = performance.now();
  applyToggleWant();
}

function applyToggleWant() {
  const v = $("hlsvideo");
  if (toggleWant === "play") { if (v.paused) v.play().catch(() => {}); }
  else if (toggleWant === "pause") { if (!v.paused) v.pause(); }
}

// The enforcement. A play or pause event that contradicts what was just
// asked for, inside the hold window, is the second toggle — undo it.
for (const ev of ["play", "pause"]) {
  $("hlsvideo").addEventListener(ev, () => {
    if (!toggleWant) return;
    if (performance.now() - toggleAt > TOGGLE_HOLD_MS) { toggleWant = null; return; }
    applyToggleWant();
  });
}
// A click on the player is the viewer changing their mind, and outranks a
// key they pressed half a second ago.
$("hlsvideo").addEventListener("pointerdown", () => { toggleWant = null; });

document.addEventListener("keydown", e => {
  if (e.altKey || e.ctrlKey || e.metaKey) return;
  const isSkip  = e.key === "ArrowLeft" || e.key === "ArrowRight";
  const isPause = e.key === " " || e.key === "Spacebar" || e.key === "k" || e.key === "K";
  if (!isSkip && !isPause) return;
  if ($("hlsplayer").style.display === "none") return;

  const t = e.target;
  if (t && (t.tagName === "INPUT" || t.tagName === "SELECT" || t.tagName === "TEXTAREA" || t.isContentEditable)) return;
  // space is also how a focused button or link is pressed; leave it theirs
  if (isPause && t && (t.tagName === "BUTTON" || t.tagName === "A" || t.tagName === "SUMMARY")) return;

  e.preventDefault();     // or the page scrolls a screen on every space
  if (e.repeat) return;   // holding the key down is one instruction, not forty

  if (isSkip) seekPlayerBy(e.key === "ArrowRight" ? SKIP_SECONDS : -SKIP_SECONDS);
  else togglePlayback();
}, true);   // capture — see below

/* A brief note under the player, cleared after a few seconds. */
let playerMsgTimer = 0;
function flashPlayerMsg(text) {
  const el = $("hlsmsg");
  el.textContent = text;
  clearTimeout(playerMsgTimer);
  playerMsgTimer = setTimeout(() => { if (el.textContent === text) el.textContent = ""; }, 4000);
}
// the native control bar: scrubbing, arrow keys, tapping the timeline
["pointerdown", "keydown"].forEach(ev =>
  $("hlsvideo").addEventListener(ev, viewerTookControl));
$("pc-speed").addEventListener("change", () => {
  $("hlsvideo").playbackRate = parseFloat($("pc-speed").value) || 1;
  prefSet("j0kers-speed", $("pc-speed").value);
});
$("pc-res").addEventListener("change", async () => {
  prefSet("j0kers-res", $("pc-res").value);
  // switching quality mid-play: restart the same file at the new height,
  // resuming from the current position (playHls enforces the resume point)
  if (!currentMediaPath || mediaKind(currentMediaPath) !== "video") return;
  const resume = $("hlsvideo").currentTime;
  await playMedia(currentMediaPath, resume > 1 ? resume : 0);
});
// restore remembered speed/quality
if (prefGet("j0kers-speed")) $("pc-speed").value = prefGet("j0kers-speed");
if (prefGet("j0kers-res")) $("pc-res").value = prefGet("j0kers-res");

/* ---- inline HLS player: native where supported, else hls.js from CDN ----
   startAt: 0 = beginning (recordings), null = live edge (channels), or a
   specific second to resume at (quality switch). */
let currentHlsStream = null; // what the player is on, for the history marks

/* ---- playback opens in a tab of its own ----
   The player page is a black, chromeless full-window video: opened in its
   own tab, the picture gets the whole screen instead of a slot halfway down
   a dashboard.

   The tab has to be opened during the click that asked for it — a browser
   only allows that with a user gesture, and anything opened after an await
   is a popup as far as it is concerned. So a click opens the tab straight
   away and whatever is still loading points it somewhere afterwards; see
   openPlayerTab / pointPlayerTab. */
const POPUP_BLOCKED_NOTE =
  "Playing here — the browser blocked the player tab. Allow pop-ups for this site to use it.";
let playingHereBecauseBlocked = false;

function playerPageUrl(src, title, resumeAt) {
  return "/player?src=" + encodeURIComponent(src)
    + (title ? "&title=" + encodeURIComponent(title) : "")
    // A fragment, so it never leaves the browser: the watch page is on the
    // media port and cannot ask the control port where it got to.
    + (resumeAt > 0 ? "#t=" + Math.floor(resumeAt) : "");
}

/* Where this stream was left, from the history the page already polls. Only
   offered when it is worth offering - see CanResume on the server: not the
   first half-minute, and not the last stretch, where "resume" would drop
   somebody into the credits. */
function resumePointFor(stream) {
  if (!stream || !Array.isArray(lastHistory)) return 0;
  const e = lastHistory.find(h => h.stream === stream || h.path === stream);
  return e && e.canResume ? (e.positionSeconds || 0) : 0;
}

/* The watch tab reports its position here, because it cannot reach the
   control port itself. Origin-checked: this listener runs on the dashboard
   and anything on the internet can postMessage to a window it has a handle
   to, so a message is only believed when it came from where the media is
   actually served. */
window.addEventListener("message", async ev => {
  const d = ev.data;
  if (!d || d.j0kers !== "position" || !d.stream) return;
  const mediaOrigins = (hlsAddresses || []).map(a => a.replace(/\/$/, ""));
  const ok = mediaOrigins.some(o => { try { return new URL(o).origin === ev.origin; } catch { return false; } })
          || ev.origin === location.origin;
  if (!ok) return;
  try {
    await send("POST", "/api/history/position",
               { key: d.stream, seconds: d.seconds, duration: d.duration });
  } catch { /* a position not recorded is not worth surfacing */ }
});

/* The same page as an absolute link, for pasting into another tab or
   sending to another device.

   This exists because the two URLs a channel has are not interchangeable
   and look alike. The playlist (…:8080/ch-x/index.m3u8) is what VLC and
   ffmpeg want; paste it into desktop Chrome and nothing plays, because a
   browser has no HLS engine of its own. The watch page is the one that
   carries hls.js, and it is what a browser tab needs. Handing out only the
   first and calling it "the stream URL" is how someone ends up with three
   tabs of a file the browser cannot play. */
function watchPageUrl(src, title) {
  const host = shareHost();
  const port = location.port || (location.protocol === "https:" ? "443" : "80");
  return location.protocol + "//" + host + ":" + port + playerPageUrl(src, title);
}

/* Opens a blank tab now, to be pointed at the player once the URL is known.
   Returns null if the browser refused, and the caller carries on inline. */
function openPlayerTab() {
  try { return window.open("", "_blank"); } catch { return null; }
}

function pointPlayerTab(win, src, title, resumeAt) {
  const url = playerPageUrl(src, title, resumeAt);
  if (win && !win.closed) { win.location.replace(url); return true; }
  // no tab in hand (no gesture, or a blocker): this may still be allowed
  const fresh = window.open(url, "_blank");
  return !!fresh;
}

async function playHls(url, startAt, tab) {
  playingHereBecauseBlocked = false;
  currentHlsStream = streamNameFromUrl(url);
  // playing an existing stream never touches /api/play — this is the only
  // notice the recently-watched list gets
  noteWatched();

  // The whole point: the media plays in its own tab, not in the card.
  // pick up where this was left, unless the caller asked for a specific spot
  const resumeAt = startAt > 0 ? startAt : resumePointFor(currentHlsStream);
  if (pointPlayerTab(tab ?? openPlayerTab(), url, playerTitleFor(url), resumeAt)) return;

  // A blocked popup shouldn't mean nothing happens, so the card's player is
  // still here as the fallback — with the reason, since "it played in the
  // wrong place" is otherwise a mystery.
  playingHereBecauseBlocked = true;
  const box = $("hlsplayer"), video = $("hlsvideo"), msg = $("hlsmsg");
  box.style.display = "block";
  bringPlayerIntoView();
  setPlayerTitle(playerTitleFor(url));
  msg.textContent = POPUP_BLOCKED_NOTE;
  if (window._hls) { window._hls.destroy(); window._hls = null; }

  // a restreamed channel, or a provider channel coming through the TV proxy
  const isChannel = /\/ch-[^/]*\//.test(url) || url.startsWith("/api/tv/");
  // default: recordings from 0, channels from the live edge
  const target = startAt !== undefined ? startAt : (isChannel ? null : 0);

  video.addEventListener("loadedmetadata", function once() {
    video.removeEventListener("loadedmetadata", once);
    video.playbackRate = parseFloat($("pc-speed").value) || 1;
  });
  // An in-progress transcode has no ENDLIST, so the player treats it as live
  // and seeks to the edge after loading. Pull it back to `target` once, after
  // any such seek.
  //
  // It has to yield to the viewer, though. A big film starts playing seconds
  // after it is clicked, and anyone who skips ahead in the meantime used to
  // be dragged back to the start the moment the first `playing` landed —
  // which reads as "the skip button restarted the film", since tapping it a
  // few times is what puts you in that window. The correction cannot tell a
  // player's own jump from a deliberate seek, so any sign of the viewer
  // choosing a position cancels it. hls.js is given startPosition regardless,
  // which handles the same thing properly on that path.
  pendingStart = target;
  if (target !== null) {
    video.addEventListener("playing", function once3() {
      video.removeEventListener("playing", once3);
      if (pendingStart === null) return;               // the viewer moved; leave them there
      if (Math.abs(video.currentTime - pendingStart) > 2) {
        try { video.currentTime = pendingStart; } catch {}
      }
      pendingStart = null;
    });
  }

  loadSubtitles(url);

  if (!window.Hls) {
    msg.textContent = "loading player…";
    // hls.js is embedded in the server; CDN is only a fallback
    for (const src of ["/hls.min.js", "https://cdn.jsdelivr.net/npm/hls.js@1/dist/hls.min.js"]) {
      await new Promise(resolve => {
        const s = document.createElement("script");
        s.src = src;
        s.onload = resolve;
        s.onerror = resolve;
        document.head.appendChild(s);
      });
      if (window.Hls) break;
    }
    // don't wipe the reason this is playing in the card at all
    msg.textContent = playingHereBecauseBlocked ? POPUP_BLOCKED_NOTE : "";
  }
  if (window.Hls && Hls.isSupported()) {
    // startPosition is the setting that actually pins where playback begins;
    // startLoad() alone still lets hls.js choose its own start point
    const opts = {};
    if (target !== null) opts.startPosition = target;
    if (isChannel) {
      // hls.js keeps every played-out second by default, which on a channel
      // left running all evening is a lot of memory for footage nobody is
      // going back to. Half an hour is a generous DVR and a bound.
      opts.backBufferLength = 1800;
    }
    window._hls = new Hls(opts);

    // Recover in place. A fragment that fails to load — most often one the
    // conversion has not written yet — is fatal to hls.js, and its recovery
    // reloads the stream from startPosition, which for a recording is zero.
    // That is the jump back to the beginning: the error is momentary, but
    // being returned to the start is not. Remember where the viewer was and
    // put them back.
    window._hls.on(Hls.Events.ERROR, (_, data) => {
      if (!data || !data.fatal) return;                 // non-fatal: hls.js copes
      const was = video.currentTime;
      try {
        if (data.type === Hls.ErrorTypes.NETWORK_ERROR) window._hls.startLoad();
        else if (data.type === Hls.ErrorTypes.MEDIA_ERROR) window._hls.recoverMediaError();
        else return;                                    // unrecoverable; leave it
      } catch { return; }
      if (was > 1) {
        const restore = () => {
          video.removeEventListener("canplay", restore);
          if (Math.abs(video.currentTime - was) > 2) { try { video.currentTime = was; } catch {} }
        };
        video.addEventListener("canplay", restore);
      }
    });

    window._hls.loadSource(url);
    window._hls.attachMedia(video);
    video.play().catch(() => {});
    return;
  }
  // Native HLS is the fallback rather than the first choice. A browser that
  // claims it can play HLS itself was taking this path and giving a live
  // stream duration=Infinity with no seekable range at all — the control bar
  // drew, but there was nothing to drag. Through hls.js the same stream
  // reports a real window that grows as it plays, so the scrubber works.
  // Kept for iOS, where there is no MSE and this is the only way to play.
  if (video.canPlayType("application/vnd.apple.mpegurl")) {
    video.src = url;
    video.play().catch(() => {});
    return;
  }
  msg.textContent = "hls.js failed to load — open the URL in VLC instead.";
}

