/* The header tiles and the poll that feeds them. tick() is the heartbeat of
   the whole page: it runs every POLL_MS, asks the server what it is doing,
   and hands the answer to the renderers in the other files. Split out of
   dashboard.html; see dashboard-core.js for why every function here stays
   global. */
"use strict";

/* The build this page's scripts came from — taken from the first status that
   answers, because the page cannot read its own version any other way. */
let pageVersion = null;
let upgradeNoticeShown = false;

/* Says the server has been upgraded under this page, and offers the reload.
   Deliberately not automatic: reloading while somebody is part way through
   ticking files for conversion would throw their selection away. */
function showUpgradeNotice(serverVersion) {
  if (upgradeNoticeShown) return;
  upgradeNoticeShown = true;
  const bar = document.createElement("div");
  bar.id = "upgrade-bar";
  bar.style.cssText = "position:fixed;left:50%;transform:translateX(-50%);bottom:16px;z-index:9999;"
    + "display:flex;align-items:center;gap:10px;padding:10px 14px;border-radius:10px;"
    + "background:var(--surface-2,#222);color:var(--ink,#eee);border:1px solid var(--grid,#444);"
    + "box-shadow:0 6px 24px rgba(0,0,0,.4);font-size:13px";
  const text = document.createElement("span");
  text.textContent = "Server updated to v" + serverVersion + " — this page is still running v"
    + pageVersion + ".";
  const btn = document.createElement("button");
  btn.className = "primary";
  btn.textContent = "Reload";
  btn.onclick = () => location.reload();
  const dismiss = document.createElement("button");
  dismiss.textContent = "Later";
  dismiss.onclick = () => bar.remove();
  bar.appendChild(text); bar.appendChild(btn); bar.appendChild(dismiss);
  document.body.appendChild(bar);
}

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

/* One poll at a time.

   setInterval does not wait for the last run to finish, so a server that was
   answering slowly got a fresh pair of requests every two seconds on top of
   the ones already outstanding. That turns a server which is briefly busy
   into one which is buried, and it is the reason a slow start looked like a
   dead server rather than a slow one. */
let pollInFlight = false;

async function tick() {
  if (pollInFlight) return;
  pollInFlight = true;
  let status, sessions;
  try {
    [status, sessions] = await Promise.all([api("/api/status"), api("/api/sessions")]);
  } catch (e) {
    $("livedot").classList.remove("live");
    /* Say what actually happened. "server unreachable" was the answer to
       every failure including the ones where the server was perfectly
       reachable and simply had not answered yet — which is the failure that
       cost the most time to explain, because the page was hiding the one
       fact that identified it. */
    $("livetext").textContent =
      e.message === "unauthorized" ? "signed out"
      : /no answer in/.test(e.message) ? "server is not answering — it is up, but busy"
      : "server unreachable";
    return;
  } finally {
    pollInFlight = false;
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

  /* The scripts this page is running came from one build; the server
     answering it may now be another. Nothing told anybody, and this page
     stays open for days — so an upgrade would land, the server would restart
     underneath it, and the tab would carry on running the old code. A fix
     that had shipped, been published and been verified on disk still looked
     broken to the only person who could see it, because the page holding it
     was from before. That cost three rounds of chasing a bug that was
     already fixed.
     Not a silent reload: a page reloading itself while somebody is halfway
     through ticking two hundred files for conversion is its own fault. It
     says so, once, and the reload is theirs to press. */
  if (status.version) {
    if (!pageVersion) pageVersion = status.version;
    else if (status.version !== pageVersion) showUpgradeNotice(status.version);
  }

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
  /* What is holding the server open, and whether that matters here.
     In background mode the answer is "nothing will stop it either way", so
     the pill says that instead of a number that means nothing. Otherwise it
     is a count, and the count is the whole mechanism: one page is you, and
     closing it stops the server about three seconds later. Anything above
     what you can account for is the thing to look at, and for an admin the
     tooltip names the addresses. */
  if (typeof status.pagesOpen === "number") {
    const stops = status.stopsOnClose !== false;
    const from = status.pagesFrom;
    if (stops) {
      $("hold-count").textContent = status.pagesOpen
        + (status.pagesOpen === 1 ? " Page" : " Pages");
      $("hold-label").textContent = "Holding Open";
      $("holdpill").title =
        (status.pagesOpen === 1
          ? "One page is open — this one. Closing it stops the server in about 3 seconds."
          : status.pagesOpen + " pages are open. The server stops about 3 seconds after the last one closes.")
        + (Array.isArray(from) && from.length ? "\n\nHeld from:\n" + from.join("\n") : "");
    } else {
      $("hold-count").textContent = "Background";
      $("hold-label").textContent = "Stays Up";
      $("holdpill").title =
        "Background mode is on, so closing this page will NOT stop the server — "
        + "untick 'Minimize to the system tray' in ⚙ Config to change that."
        + (Array.isArray(from) && from.length ? "\n\n" + status.pagesOpen + " page(s) open:\n" + from.join("\n") : "");
    }
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
    /* The Problems card. The count rides this poll; the list itself is only
       fetched when the count changes, so a healthy server never asks for it
       at all. Same tier as the log, and for the same reason — these name
       file paths. */
    if (typeof status.problems === "number") paintProblems(status.problems);
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

    /* The last conversion is the one whose result nobody ever saw.

       The throttled refresh above runs only WHILE something is converting, so
       the newest listing it ever fetches is one taken up to six seconds before
       the final file finished. The moment the queue empties the condition goes
       false and no further poll is made, leaving the pills showing the counts
       as they were mid-run: "12 to convert" on a folder that has just finished
       converting all twelve. Navigating away and back fixed it, which is
       exactly what "the pill numbers don't update after transcodes" was.

       So the busy→idle edge gets one more read. It deliberately ignores the
       throttle (this fires once per run, not every tick) and the selection and
       search guards above (a quiet reload preserves both, and the whole point
       is that the person is most likely looking at the panel right now,
       waiting for it to say the work is done). */
    const tcIdleNow = !tcBusy;
    if (tcState.booted && tcIdleNow && tcWasBusy && tcState.path) {
      tcLastAutoReload = performance.now();
      tcReload(tcState.path, true);
    }
    tcWasBusy = tcBusy;
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



/* ---- Problems ----
   Shown only when there is something wrong. A card reading "0 problems" on a
   healthy server is one people learn to skip, which would defeat it: the
   value here is that seeing this card at all means something needs looking
   at. Server-admin only, by the class on the card. */
let probLastCount = -1;

async function paintProblems(count) {
  const card = $("problemcard");
  if (!card) return;
  if (!count) { card.style.display = "none"; probLastCount = 0; return; }
  card.style.display = "";
  $("prob-count").textContent = "· " + count + (count === 1 ? " item" : " items");
  // Only re-fetch the detail when the count actually moved. The list is a
  // handful of rows and this poll runs every two seconds.
  if (count === probLastCount) return;
  probLastCount = count;
  try {
    const d = await api("/api/problems");
    renderProblems(d.problems || []);
  } catch { /* a read-only account cannot see these; the card is hidden anyway */ }
}

const PROB_LABEL = { conversion: "conversion failed", probe: "could not be read", source: "source missing" };

function renderProblems(items) {
  const list = $("prob-list");
  if (!list) return;
  list.innerHTML = "";
  for (const it of items) {
    const row = document.createElement("div");
    row.className = "tc-row";
    row.style.cursor = "default";

    const icon = document.createElement("span");
    icon.style.flex = "none";
    icon.textContent = it.kind === "conversion" ? "🎞" : it.kind === "probe" ? "🔍" : "📄";

    const nm = document.createElement("span");
    nm.className = "tc-name";
    nm.textContent = it.name;
    nm.title = it.path + "\n\n" + it.detail;   // the full path and ffmpeg's own words

    const what = document.createElement("span");
    what.className = "tc-badge b-need";
    what.textContent = PROB_LABEL[it.kind] || it.kind;

    row.appendChild(icon); row.appendChild(nm); row.appendChild(what);

    if (it.count > 1) {
      const n = document.createElement("span");
      n.className = "tc-badge b-ok";
      n.title = "How many times this same failure has happened";
      n.textContent = "\u00d7" + it.count;
      row.appendChild(n);
    }

    const when = document.createElement("span");
    when.className = "tc-badge b-ok";
    when.style.flex = "none";
    when.textContent = agoText(it.whenUtc);
    row.appendChild(when);

    const x = document.createElement("button");
    x.className = "danger"; x.style.flex = "none"; x.textContent = "\u2715";
    x.title = "Forget this one";
    x.onclick = () => forgetProblem(it.path);
    row.appendChild(x);

    list.appendChild(row);
  }
}

function agoText(whenUtc) {
  const s = Math.max(0, (Date.now() - new Date(whenUtc).getTime()) / 1000);
  if (s < 60) return "just now";
  if (s < 3600) return Math.floor(s / 60) + "m ago";
  if (s < 86400) return Math.floor(s / 3600) + "h ago";
  return Math.floor(s / 86400) + "d ago";
}

async function forgetProblem(path) {
  await send("POST", "/api/problems/clear?path=" + encodeURIComponent(path));
  probLastCount = -1;   // force the next poll to re-read
}

async function clearProblems() {
  await send("POST", "/api/problems/clear");
  probLastCount = -1;
  renderProblems([]);
  $("problemcard").style.display = "none";
}
