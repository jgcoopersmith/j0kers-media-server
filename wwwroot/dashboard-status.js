/* The header tiles and the poll that feeds them. tick() is the heartbeat of
   the whole page: it runs every POLL_MS, asks the server what it is doing,
   and hands the answer to the renderers in the other files. Split out of
   dashboard.html; see dashboard-core.js for why every function here stays
   global. */
"use strict";
function fmtUptime(s) {
  if (s < 3600) return Math.floor(s / 60) + "m " + (s % 60) + "s";
  if (s < 86400) return Math.floor(s / 3600) + "h " + Math.floor(s % 3600 / 60) + "m";
  return Math.floor(s / 86400) + "d " + Math.floor(s % 86400 / 3600) + "h";
}
const fmtRate = bps => bps >= 1048576 ? (bps / 1048576).toFixed(1) + " MB/s"
  : bps >= 1024 ? Math.round(bps / 1024) + " KB/s"
  : Math.round(bps) + " B/s";

/* How long since a sign-in last made a request — what separates a phone on
   the sofa from a browser somebody left open on Tuesday. */
const idleText = s => s < 10 ? "active now"
  : s < 60 ? s + "s idle"
  : s < 3600 ? Math.floor(s / 60) + "m idle"
  : Math.floor(s / 3600) + "h idle";

async function tick() {
  let status, sessions;
  try {
    [status, sessions] = await Promise.all([api("/api/status"), api("/api/sessions")]);
  } catch (e) {
    $("livedot").classList.remove("live");
    $("livetext").textContent = e.message === "unauthorized" ? "signed out" : "server unreachable";
    return;
  }

  const running = status.running !== false;
  $("livedot").classList.toggle("live", running);
  $("livetext").textContent = running
    ? "live · rtsp :" + status.rtsp.port + " · hls :" + status.hls.port
    : "services stopped — control panel only";
  const pw = $("powerbtn");
  pw.textContent = running ? "⏻ Stop" : "⏻ Start";
  pw.classList.toggle("on", running);
  pw.classList.toggle("off", !running);
  $("verpill").textContent = "v" + (status.version || "?");

  /* Accounts on the server, and how many of them have somebody signed in.
     Both ride this poll rather than costing requests of their own. Left at
     their placeholders when the server does not send them, so an older build
     shows "Users" and a dash rather than a confident zero. */
  if (typeof status.accounts === "number")
    $("users-count").textContent = status.accounts + (status.accounts === 1 ? " User" : " Users");
  if (typeof status.signedIn === "number") {
    $("loggedin-count").textContent =
      status.signedIn + (status.signedIn === 1 ? " User" : " Users");

    /* Hovering names them, for an administrator. Each sign-in is its own
       line — two browsers and a phone are three lines, and the same account
       appearing more than once is the point rather than a mistake, so the
       address and how long since each last spoke are what tell them apart.
       The server sends this list only to an admin; for anyone else the field
       is absent and the tooltip stays the plain description. */
    const who = status.signedInUsers;
    $("loggedinpill").title = Array.isArray(who) && who.length
      ? who.map(w => w.user + " · " + w.client + " · " + idleText(w.idleSeconds)).join("\n")
      : "Accounts with somebody signed in right now.";
  }
  noteServerClock(status);   // re-syncs the header clock on every poll
  rtspPort = status.rtsp.port; hlsPort = status.hls.port;
  hlsAddresses = status.hls.addresses || [];

  // both kinds of viewing count: RTSP sessions and people watching over HLS
  const viewers = status.hls.viewers || 0;
  $("t-sessions").textContent = status.rtsp.sessions + viewers;
  $("t-sessions-d").textContent = viewers
    ? status.rtsp.sessions + " rtsp · " + viewers + " watching"
    : "of " + status.rtsp.maxSessions + " max";
  /* Uptime is the server process's, and Stop does not end the process - it
     closes the streaming ports and leaves the dashboard up, which is what
     lets you start them again from here. A clock still counting under a
     button that says Stop reads as "it did not work", so when the services
     are stopped the tile says so instead of naming the server. */
  $("t-uptime").textContent = fmtUptime(status.uptimeSeconds);
  $("t-uptime-d").textContent = (status.running === false)
    ? "streaming stopped - server still running"
    : status.server;

  // Everything the server is pushing out right now — RTP and HLS together —
  // from the server's own monotonic byte counter, so a viewer leaving can't
  // make the rate go backwards.
  const served = status.bytesServed || 0;
  const now = performance.now();
  if (lastBytes !== null && served >= lastBytes && now > lastTime) {
    const rate = (served - lastBytes) / ((now - lastTime) / 1000);
    $("t-pps").textContent = fmtRate(rate);
    $("t-pps-d").textContent = fmtBytes(served) + " total";
  }
  lastBytes = served; lastTime = now;

  activeTranscodes = status.transcodes || [];
  transcodingNow = status.transcoding || [];
  renderTranscodes(transcodingNow);
  // A stream that is in the HLS list *and* converting — one started by
  // playing a file rather than from the Transcodes window — animates its
  // progress bar from this poll. Queued conversions are not in that list
  // and are not drawn here at all; they belong to the Transcodes window.
  if (transcodingNow.length || hadTranscodes) renderHls();
  hadTranscodes = transcodingNow.length > 0;
  renderSessions(sessions.sessions);
  // the card is server-admin-only, and so is the endpoint behind it
  if (document.body.classList.contains("is-server-admin")) {
    refreshLog();
    tcBoot();
    renderConversions(transcodingNow, status.transcodeQueue || []);
    /* Keep the listing live while conversions run, without a full reload
       wiping a selection the user is building.

       "While conversions run" used to mean tcState.converting, which is set in
       exactly one place: a FILE row in the current view whose state is
       "converting". Standing in a parent directory — which is where the folder
       pills are, and so where the "N to convert" number is read — every row is
       a folder, none of them carries a state, and that flag was therefore
       always false. So the one view whose number the user was watching was the
       one view that never refreshed: it froze at whatever it last showed and
       stayed there for the rest of the run, while files quietly finished
       behind it. Together with a count that climbed as files were read, that
       is how a pill ends up stuck at a number higher than it started.

       The server already tells us whether it is busy, in this very poll, so
       ask that instead. Throttled, because each of these re-walks the folder
       tree recursively and this fires every two seconds. */
    const tcBusy = tcState.converting
                || transcodingNow.length > 0
                || (status.transcodeQueue || []).length > 0;
    if (tcState.booted && tcBusy && tcState.selected.size === 0 && tcState.path && !tcState.search
        && performance.now() - tcLastAutoReload > 6000) {
      tcLastAutoReload = performance.now();
      tcReload(tcState.path, true);   // quiet: keeps the "N file(s) queued" message on screen
    }
  }
  refreshHistory();
  refreshMounts();   // cheap, cached-ish

  // The HLS stream list changes only when a conversion starts, a conversion
  // finishes, or the user deletes/rebuilds one. The first two both show up
  // here as the set of active transcodes changing between polls; the third
  // is handled by those actions calling refreshHls() themselves. So fetch
  // the list once on load and then only when that set changes — instead of
  // hitting the HLS server every single poll for a directory that is almost
  // always identical. renderHls() above still runs each tick to animate the
  // in-progress bars from the fresh status, working off the cached list.
  const transcodeSig = transcodingNow.map(t => t && t.stream).sort().join("\n");
  if (lastHls === null || transcodeSig !== lastTranscodeSig) refreshHls();
  lastTranscodeSig = transcodeSig;
  await refreshChannels(false);
  refreshPlaylists(false);
  refreshFavorites(false);

  if (!libraryBooted) {
    libraryBooted = true;
    await refreshLibraryRoots(true);
    if (libraryRoots.length) loadLibrary(libraryRoots[0]);
  }

  // the lineup is a few hundred rows off a remote service — fetch it once,
  // not on every poll
  if (!tvBooted) {
    tvBooted = true;
    loadProviders();
  }
}
let libraryBooted = false;
let tvBooted = false;

let activeTranscodes = [];

const fmtBytes = n => n >= 1073741824 ? (n / 1073741824).toFixed(1) + " GB"
  : n >= 1048576 ? (n / 1048576).toFixed(1) + " MB"
  : n >= 1024 ? Math.round(n / 1024) + " KB" : n + " B";

/* "watching for 12m", from when this viewing first appeared */
function fmtSince(iso) {
  if (!iso) return "";
  const s = Math.max(0, Math.round((Date.now() - new Date(iso)) / 1000));
  return s < 60 ? s + "s" : s < 3600 ? Math.floor(s / 60) + "m" : Math.floor(s / 3600) + "h " + Math.floor(s % 3600 / 60) + "m";
}

/* Transcodes tile: how many conversions are running and how far along.
   The bar tracks the least-advanced job, since that's the one deciding
   when everything is finished. A job whose source length couldn't be
   probed has no percentage — it shows elapsed output time instead and a
   dimmed full bar, rather than a misleading 0%. */
function renderTranscodes(list) {
  const v = $("t-tc"), d = $("t-tc-d"), bar = $("t-tc-bar");
  v.textContent = list.length;
  if (!list.length) {
    d.textContent = "none running";
    d.title = "";
    bar.className = "bar";
    // back to empty, or the next job animates down from the last one's
    // width and reads as progress going backwards
    bar.firstElementChild.style.width = "0";
    return;
  }

  const known = list.filter(t => typeof t.percent === "number");
  const slowest = known.length ? Math.min(...known.map(t => t.percent)) : null;

  d.textContent = list.length === 1
    ? (list[0].title || list[0].stream)
      + (typeof list[0].percent === "number"
          ? " · " + list[0].percent + "%"
          : " · " + fmtClock(list[0].doneSeconds))
    : list.map(t => typeof t.percent === "number" ? t.percent + "%" : "…").join(" · ");
  d.title = list.map(t => (t.title || t.stream)
    + (typeof t.percent === "number"
        ? " — " + t.percent + "% of " + fmtClock(t.durationSeconds)
        : " — " + fmtClock(t.doneSeconds) + " converted")).join("\n");

  bar.className = slowest === null ? "bar on unknown" : "bar on";
  bar.firstElementChild.style.width = (slowest === null ? 0 : slowest) + "%";
}

const fmtClock = s => {
  s = Math.max(0, Math.round(s || 0));
  const m = Math.floor(s / 60);
  return m >= 60
    ? Math.floor(m / 60) + "h " + (m % 60) + "m"
    : m + ":" + String(s % 60).padStart(2, "0");
};

function renderSessions(list) {
  if (!list.length) {
    const note = activeTranscodes.length
      ? '<div class="empty">Nobody watching · ⏳ ' + activeTranscodes.length + ' transcode' + (activeTranscodes.length > 1 ? 's' : '') + ' running (see HLS Streams)</div>'
      : '<div class="empty">Nobody watching — play something here, on a phone, or open '
        + '<span class="mono">rtsp://' + location.hostname + ':' + rtspPort + '/test</span> in VLC.</div>';
    $("sessions").innerHTML = note;
    return;
  }
  let h = '<table><tr><th>Via</th><th>Stream</th><th>State</th><th>Client</th><th>Watching</th><th>Sent</th><th></th></tr>';
  for (const s of list) {
    // rtsp is the odd one out: it has a real session with an id worth
    // showing. hls and dlna are both inferred from request traffic, and for
    // those the device and the account are what identify a viewer.
    const rtsp = s.protocol === "rtsp";
    // for HLS the useful identity is the device and the account, not an
    // opaque id — "Android · kid" tells you who is streaming what
    const who = !rtsp
      ? esc(s.client) + '<div style="color:var(--muted);font-size:11px">' + esc(s.player)
        + (s.user ? " · " + esc(s.user) : "") + "</div>"
      : esc(s.client) + '<div style="color:var(--muted);font-size:11px">' + esc(s.id) + "</div>";
    h += '<tr><td><span class="badge' + (rtsp ? " admin" : "") + '">' + esc(s.protocol) + "</span></td>"
      // the readable title, with the stream id a hover away for when the
      // exact directory is what you actually need
      + '<td' + (rtsp ? ' class="mono"' : '') + ' title="' + esc(s.mount) + '">'
      + esc(s.title || s.mount) + '</td>'
      + '<td><span class="state ' + esc(s.state) + '"><span class="dot"></span>' + esc(s.state) + '</span></td>'
      + '<td class="mono">' + who + '</td>'
      + '<td>' + esc(fmtSince(s.startedUtc)) + '</td>'
      + '<td>' + fmtBytes(s.bytes || 0) + '</td>'
      + '<td style="text-align:right">'
      + (s.terminable
          ? '<button class="danger" data-act="session-kill" data-arg="' + esc(s.id) + '">Terminate</button>'
          : '<span style="color:var(--muted);font-size:11px" title="'
            + (s.protocol === "dlna"
                ? "DLNA has no session to end. Untick the folder, or switch DLNA off, to stop it."
                : "An HLS viewer is a series of file requests — there is no connection to cut. Revoke their key or account to stop them.")
            + '">—</span>')
      + "</td></tr>";
  }
  $("sessions").innerHTML = h + '</table>';
}

