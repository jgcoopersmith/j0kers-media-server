/* The three read-outs of what the server has been doing: the log panel, the
   recently-watched list, and terminating a session from the sessions table.
   Split out of dashboard.html; see dashboard-core.js for why every function
   here stays global. */
"use strict";
/* ---- the log panel: what the console window used to show ----
   Lines are kept client-side and asked for by sequence, so a poll fetches
   the two new ones rather than the last five hundred. The level filter is
   applied here rather than on the server, so dropping to "errors only"
   shows the errors already in hand instead of waiting for a new one. */
const LOG_LEVELS = { TRACE: 0, DEBUG: 1, INFO: 2, WARN: 3, ERROR: 4 };
const LOG_KEEP = 500;              // matches the server's ring
let logLines = [], logSeq = 0, logFollow = true, logMissed = false;
let logLevelSynced = false;
let logViewFile = "";              // "" = live ring; otherwise a rotated file on disk

async function refreshLog() {
  // the dropdown on the log card IS the server's real logging level, not
  // just a client-side filter — show what it's actually set to, once,
  // rather than defaulting to "Normal" and lying about the current state
  if (!logLevelSynced) {
    logLevelSynced = true;
    try {
      const s = await api("/api/settings");
      if (s && s.streamRemoveAction) streamRemoveAction = String(s.streamRemoveAction).toLowerCase();
      if (s.logLevel) $("log-filter").value = s.logLevel.toUpperCase();
    } catch { /* keep the dropdown's default */ }
    loadLogFiles();               // fill the history picker once, at startup
  }
  // Reviewing a file from disk: the live poll must not overwrite it. It resumes
  // the moment the picker is set back to Live.
  if (logViewFile) return;
  let data;
  try { data = await api("/api/log?since=" + logSeq); }
  catch { return; }               // not an admin, or the server blinked
  if (!data.entries.length && !data.missed) return;
  if (data.missed && !logMissed) logMissed = true;
  logSeq = data.last;
  logLines = logLines.concat(data.entries).slice(-LOG_KEEP);
  renderLog();
}

// Picking a level here changes what the server logs (and re-applies on the
// next restart or crash, via the same settings.json the ⚙ Config dialog
// writes to) — not only what this dropdown hides from the lines already on
// screen.
async function onLogFilterChange() {
  renderLog();
  try {
    const r = await fetch("/api/settings", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers() },
      body: JSON.stringify({ logLevel: $("log-filter").value.toLowerCase() }),
    });
    if (!r.ok) { const d = await r.json().catch(() => ({})); alert(d.error || "could not change the log level"); }
  } catch { alert("could not reach the server to change the log level"); }
}

// The rotated log files on disk, so earlier sessions (last night, a crash) can
// be reviewed here — the live view above is only ever the current run.
async function loadLogFiles() {
  try {
    const d = await api("/api/log/files");
    const sel = $("log-file");
    const keep = sel.value;
    let h = '<option value="">🔴 Live</option>';
    for (const f of d.files) {
      const when = new Date(f.modified).toLocaleString();
      const mb = f.bytes < 1048576 ? (f.bytes / 1024).toFixed(0) + " KB"
                                   : (f.bytes / 1048576).toFixed(1) + " MB";
      const label = (f.active ? "● current · " : "") + when + " · " + mb;
      h += '<option value="' + esc(f.name) + '">' + esc(label) + "</option>";
    }
    sel.innerHTML = h;
    sel.value = keep;             // keep the current selection across a refresh
  } catch { /* not an admin, or the server blinked */ }
}

async function onLogFileChange() {
  logViewFile = $("log-file").value;
  const box = $("log");
  if (!logViewFile) {            // back to the live ring
    logLines = []; logSeq = 0; logMissed = false; logFollow = true; paintLogFollow();
    box.innerHTML = '<div style="color:var(--muted)">resuming live…</div>';
    refreshLog();
    return;
  }
  box.innerHTML = '<div style="color:var(--muted)">loading ' + esc(logViewFile) + "…</div>";
  try {
    const d = await api("/api/log/file?name=" + encodeURIComponent(logViewFile));
    const note = d.truncated
      ? '<div style="color:var(--muted)">… showing the last ' + d.shown + " lines of " + esc(d.name) + "</div>\n"
      : "";
    box.innerHTML = note
      + '<div style="white-space:pre-wrap;word-break:break-word;color:var(--ink-2)">' + esc(d.text) + "</div>";
    box.scrollTop = box.scrollHeight;
  } catch {
    box.innerHTML = '<div style="color:var(--critical)">could not load ' + esc(logViewFile) + "</div>";
  }
}

function renderLog() {
  const box = $("log");
  const floor = LOG_LEVELS[$("log-filter").value] ?? 2;
  const shown = logLines.filter(e => (LOG_LEVELS[e.level] ?? 2) >= floor);

  let h = logMissed
    ? '<div style="color:var(--muted)">… earlier lines have scrolled out of memory — the log file has them</div>'
    : "";
  for (const e of shown) {
    const colour = e.level === "ERROR" ? "var(--critical)"
      : e.level === "WARN" ? "var(--warning)"
      : e.level === "DEBUG" || e.level === "TRACE" ? "var(--muted)"
      : "var(--ink-2)";
    h += '<div style="color:' + colour + ';white-space:pre-wrap;word-break:break-word">'
      + '<span style="color:var(--muted)">' + esc(e.at) + "</span> "
      + '<span style="color:var(--muted)">[' + esc(e.area) + "]</span> "
      + esc(e.message) + "</div>";
  }
  box.innerHTML = h || '<div style="color:var(--muted)">nothing at this level yet</div>';
  if (logFollow) box.scrollTop = box.scrollHeight;
}

/* Following is off the moment you scroll up — reading anything is
   impossible if the newest line keeps yanking the view back — and on again
   when you return to the bottom yourself. */
$("log").addEventListener("scroll", () => {
  const box = $("log");
  const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
  if (atBottom !== logFollow) { logFollow = atBottom; paintLogFollow(); }
});

function toggleLogFollow() {
  logFollow = !logFollow;
  paintLogFollow();
  if (logFollow) $("log").scrollTop = $("log").scrollHeight;
}

function paintLogFollow() {
  $("log-follow").textContent = logFollow ? "⏬ Scrolling" : "⏸ Paused";
  $("log-follow").title = logFollow
    ? "Keeping the newest line in view — scroll up to pause"
    : "Scrolled up; new lines are still arriving. Click to follow again.";
}

/* ---- recently watched: the last ten, since the table above only ever
   shows what is live. Marked as viewed, and re-playable with a click. ---- */
let historyLoaded = 0, lastHistory = [], historySig = "";

async function refreshHistory(force) {
  if (!force && Date.now() - historyLoaded < 4000) return;
  historyLoaded = Date.now(); // stamp first: overlapping ticks shouldn't pile up
  try {
    const data = await api("/api/history?count=10");
    lastHistory = data.history || [];
  } catch {
    historyLoaded = 0; // failed, so don't sit out the next tick as well
    return;
  }
  renderHistory();
}

/* Starting something is recorded server-side twice over: the file is
   prepared now, and the viewing itself lands a moment later when the player
   makes its first request. Ask again after that, or the list stays a step
   behind whatever was just launched. */
function noteWatched() {
  refreshHistory(true);
  setTimeout(() => refreshHistory(true), 2500);
  setTimeout(() => refreshHistory(true), 7000);
}

function renderHistory() {
  const sel = $("recent");
  // Rewriting the options drops any highlighted row, so only do it when
  // something actually changed. (This used to skip the render whenever the
  // box had focus — which meant that once it was clicked, it never updated
  // again until the page was reloaded.)
  // the rendered "ago" text, not the timestamp — it changes on its own
  const sig = JSON.stringify(lastHistory.map(e =>
    [e.name, fmtSince(e.startedUtc), e.plays, e.missing, e.viaDlna,
     // the resume point too, or a row that has moved on never repaints
     e.canResume ? Math.floor((e.positionSeconds || 0) / 30) : 0])) + "|" + currentMediaPath + "|" + currentHlsStream;
  if (sig === historySig) return;
  historySig = sig;

  let h = '<option value="">Recently watched'
        + (lastHistory.length ? " · " + lastHistory.length : "") + "…</option>";
  for (const i in lastHistory) {
    const e = lastHistory[i];
    // everything in this list has been watched — the one still on screen is
    // the exception, so mark that rather than repeating "viewed" ten times
    const playing = (currentMediaPath && e.path === currentMediaPath)
      || (e.stream && currentHlsStream === e.stream);
    // a TV over DLNA is nobody's account, so it would otherwise read as
    // something this user watched — the icon says where it was played
    const mark = playing ? "▶" : e.viaDlna ? "📺" : "✓";
    /* How far in, when there is somewhere to pick up from. This is the whole
       point of storing a position: the list is where somebody decides what to
       carry on with, so it has to say which rows are part-watched and roughly
       how far. Shown as time and, when the length is known, a percentage —
       "1h 04m in (48%)" answers "is this nearly over?" and a bare timestamp
       does not. */
    const resume = !e.missing && e.canResume
      ? " · ⏵ " + hmsShort(e.positionSeconds)
        + (e.durationSeconds > 0
            ? " in (" + Math.round(e.positionSeconds / e.durationSeconds * 100) + "%)"
            : " in")
      : "";
    const note = (e.missing ? " · no longer available"
      : e.plays > 1 ? " · watched ×" + e.plays
      : "") + (e.viaDlna ? " · on a TV" : "") + resume;
    // the index, not the path: an entry may be replayable by file or by
    // stream, and the row itself knows which
    h += '<option value="' + i + '"' + (e.missing ? " disabled" : "") + ">"
      + esc(mark + " " + e.name + " · " + fmtSince(e.startedUtc) + " ago" + note) + "</option>";
  }
  if (!lastHistory.length)
    h += '<option value="" disabled>nothing watched yet</option>';
  sel.innerHTML = h;
}

/* Picking a title plays it again; the box drops back to its label so it
   reads as an action rather than a setting that is now "on". */
function playFromHistory(sel) {
  const e = lastHistory[sel.value];
  sel.selectedIndex = 0;
  if (!e) return;
  clearQueueState();
  // Both open the player. A file goes back through the transcoder, since the
  // quality setting may have moved since — playMedia waits for the playlist
  // and then plays it, where prepareMedia only readies it. A stream that was
  // never a local file plays directly, through mediaUrl: the player fetches
  // it from the HLS port with a signed token, not from this page's origin.
  if (e.kind === "file" && e.path) playMedia(e.path, 0);
  else if (e.stream) playHls(mediaUrl("/" + encodeURIComponent(e.stream) + "/index.m3u8"));
}

async function killSession(id) {
  if (!confirm("Terminate session " + id + "?")) return;
  await fetch("/api/sessions/" + encodeURIComponent(id), { method: "DELETE", headers: headers() });
  tick();
}



/* A position said the short way: "6m", "1h 04m". Used by the recently-watched
   list, where the row is already long and the exact second does not matter. */
function hmsShort(sec) {
  sec = Math.max(0, Math.floor(sec || 0));
  const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60);
  if (h > 0) return h + "h " + String(m).padStart(2, "0") + "m";
  return m > 0 ? m + "m" : sec + "s";
}
