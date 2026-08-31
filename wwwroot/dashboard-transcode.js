/* The Transcode panel: browsing the disk, marking files or whole folders,
   and the queue that converts them. It is a server administrator's tool, a
   fact the markup enforces by class rather than anything in this file.
   Split out of dashboard.html; see dashboard-core.js for why every function
   here stays global. */
"use strict";
/* ---- the Transcode panel (server admin only) ----
   Browse the disk, tick files or whole folders, and convert them. Each file
   shows whether a TV needs it converted and whether a conversion exists, is
   running, or has never been made. */
const tcState = { path: "", parent: null, booted: false, selected: new Set(), converting: false, search: "" };
let tcCheckTimer = 0, tcCheckTries = 0;   // re-poll while any pill is still being read
let tcLastAutoReload = 0;                 // throttles the heartbeat's re-scan (dashboard-status.js)
let tcWasBusy = false;                    // was anything converting on the previous poll? (see below)
const TC_FOLDER_KEY = "j0kers-tc-folder";   // last folder browsed, remembered per browser

/* How much longer a conversion has, said the way somebody waiting would say
   it. Rounded deliberately coarsely above an hour: the estimate is not
   accurate to the minute and printing "2h 07m" would claim it is. */
function etaText(s) {
  if (s < 60) return "under a minute";
  if (s < 3600) return Math.round(s / 60) + " min";
  const h = Math.floor(s / 3600), m = Math.round((s % 3600) / 60);
  return h + "h" + (m >= 5 ? " " + m + "m" : "");
}

async function tcBoot() {
  if (tcState.booted) return;
  tcState.booted = true;
  tcLoadConfig();
  let saved = null;
  try { saved = localStorage.getItem(TC_FOLDER_KEY); } catch { /* private mode */ }
  if (saved) {
    await tcReload(saved);
    // saved folder gone (unplugged drive, moved dir): fall back to empty
    if (!tcState.path) { try { localStorage.removeItem(TC_FOLDER_KEY); } catch {} tcShowEmpty(); }
  } else {
    tcShowEmpty();
  }
}

/* How many conversions run at once, and the gap between starting them —
   mirrors the media conversion tool. Persisted on the server so the choice
   survives restarts. */
async function tcLoadConfig() {
  try {
    const r = await fetch("/api/transcode/config", { headers: headers() });
    if (!r.ok) return;
    const d = await r.json();
    if ($("tc-parallel")) $("tc-parallel").value = String(d.maxParallel);
    if ($("tc-stagger")) $("tc-stagger").value = String(d.staggerSeconds);
  } catch { /* leave the defaults shown */ }
}

async function tcSaveConfig() {
  const body = {
    maxParallel: parseInt($("tc-parallel").value, 10),
    staggerSeconds: parseInt($("tc-stagger").value, 10),
  };
  const msg = $("tc-msg");
  try {
    const r = await fetch("/api/transcode/config", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers() },
      body: JSON.stringify(body),
    });
    if (r.ok && msg) {
      msg.textContent = "queue: " + body.maxParallel + " at a time"
        + (body.staggerSeconds ? ", " + body.staggerSeconds + "s between starts" : "");
    }
  } catch { if (msg) msg.textContent = "could not save queue settings"; }
}

/* Jump to any drive or folder through the same browsable picker the media
   library's "➕ Add folder" uses (/api/browse — drives open properly there),
   then list that folder's files with their conversion status. */
async function tcAddFolder() {
  const p = await pickPath({ mode: "folder", title: "Add a folder to transcode", startPath: tcState.path || "" });
  if (p) tcReload(p);
}

/* Search — recursively finds video files under the current folder by name,
   like the media library search. Runs the EXACT string only when you press
   Enter, so typing "(2)" isn't searched piecemeal as "(", "(2", "(2)" — you
   get one clean search for what you actually typed. */
function tcSearchTyped() {
  // just show/hide the clear button while typing; the search itself waits for
  // Enter. Emptying the box clears an active search.
  const q = $("tc-search").value;
  $("tc-search-clear").style.display = q ? "" : "none";
  if (!q && tcState.search) tcClearSearch();
}
function tcSearchNow() {
  if (!tcState.path) { $("tc-msg").textContent = "Open a folder first, then search inside it."; return; }
  tcState.search = $("tc-search").value.trim();
  $("tc-search-clear").style.display = tcState.search ? "" : "none";
  if (tcState.search) tcReload(tcState.path);   // same path → keeps the search set above
}
function tcClearSearch() {
  $("tc-search").value = "";
  $("tc-search-clear").style.display = "none";
  const had = tcState.search;
  tcState.search = "";
  if (had && tcState.path) tcReload(tcState.path);
}

/* Delete the ticked files/folders — moved to the Recycle Bin (undoable), after
   a confirmation. */
async function tcDeleteChecked() {
  const paths = [...tcState.selected];
  if (!paths.length) return;
  if (!confirm("Move " + paths.length + " checked item" + (paths.length === 1 ? "" : "s")
      + " to the Recycle Bin?\n\nThey can be restored from the Recycle Bin if this was a mistake.")) return;
  const msg = $("tc-msg");
  $("tc-del").disabled = true;
  msg.textContent = "deleting…";
  let result = "";
  try {
    const r = await fetch("/api/transcode/delete", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers() },
      body: JSON.stringify({ paths }),
    });
    const d = await r.json();
    if (!r.ok) throw new Error(d.error || r.status);
    result = d.deleted + " item" + (d.deleted === 1 ? "" : "s") + " moved to the Recycle Bin"
      + (d.errors && d.errors.length ? " · " + d.errors.length + " couldn't be deleted (" + d.errors[0] + ")" : "");
  } catch (e) { result = "delete failed: " + e.message; }
  await tcReload(tcState.path);   // refresh the listing (files are gone now)
  msg.textContent = result;
}

/* Empty start — no drive list. You add a drive or folder with the ➕ button,
   exactly like the media library. */
function tcShowEmpty() {
  tcState.path = "";
  tcState.parent = null;
  tcState.selected.clear();
  try { localStorage.removeItem(TC_FOLDER_KEY); } catch { /* private mode */ }
  tcSyncGo();
  $("tc-path").textContent = "No folder yet";
  $("tc-up").disabled = true;
  const sp = $("tc-space"); if (sp) sp.textContent = "";
  $("tc-list").innerHTML =
    '<div class="tc-row" style="color:var(--muted);cursor:default">'
    + 'Click <b style="margin:0 4px">➕ Add folder</b> to browse to a drive or folder to convert.</div>';
}

let tcReloadGen = 0;
async function tcReload(path, quiet) {
  /* Nothing to show and the root are different things, and treating them as
     one is why Up did not work.

     Up from a drive root asks for "" — the parent of G:\ is nothing — and the
     server answers "" with the drive list, the same one the folder picker
     shows. This turned back before asking, on a falsy test that cannot tell
     "" from null, and put up the "click Add folder" prompt instead. So Up
     appeared to do nothing from the one place it had somewhere to go.

     null/undefined still means nothing to show: that is boot with no folder
     remembered. "" means the root, and the root is a real listing. */
  if (path === null || path === undefined) { tcShowEmpty(); return; }

  /* Only a person navigating claims a new generation.

     This counter exists so a slow response cannot overwrite a newer one, and
     every reload used to claim a number — including the heartbeat's quiet
     re-scan, which runs every six seconds for as long as anything is
     converting. So pressing Up or Refresh while the queue was busy was a
     race: if the heartbeat landed in the gap between the click and its
     answer, it took the newer number and the person's own reload was thrown
     away on arrival. No error, no movement, nothing in the console — the
     button simply did nothing, and did it most often when the queue was
     busiest, which is exactly when somebody is watching this panel.

     A background refresh must never cancel a deliberate act. It still stands
     aside for one: it reads the generation without claiming it, and drops its
     own result if a person has navigated since. */
  const gen = quiet ? tcReloadGen : ++tcReloadGen;
  // Moving to a different folder ends any search; refreshing the same folder
  // keeps it (that's how the search stays put while you tick and convert).
  if (path !== tcState.path) {
    tcState.search = "";
    const si = $("tc-search"); if (si) si.value = "";
    const sc = $("tc-search-clear"); if (sc) sc.style.display = "none";
    // A fresh folder gets its own allowance of re-polls. The ceiling below is
    // there so one folder of unreadable files cannot poll forever, but the
    // counter was only ever reset when a folder came back fully read — so that
    // one bad folder spent the allowance and then every folder opened
    // afterwards had none, and their pills never refreshed at all.
    tcCheckTries = 0;
  }
  const list = $("tc-list"), msg = $("tc-msg");
  // A quiet refresh is the "checking..." poll coming back for fresh pills.
  // It must not disturb what the person is doing: keep their ticks, and do
  // not clear a message they may be reading.
  if (!quiet) {
    msg.textContent = "";
    tcState.selected.clear();
    tcSyncGo();
  }
  let data;
  try {
    const url = "/api/transcode/scan?path=" + encodeURIComponent(path)
      + (tcState.search ? "&q=" + encodeURIComponent(tcState.search) : "");
    const r = await fetch(url, { headers: headers() });
    data = await r.json();
    if (!r.ok) throw new Error(data.error || r.status);
  } catch (e) { if (gen === tcReloadGen) { list.innerHTML = ""; msg.textContent = "cannot open: " + e.message; } return; }
  if (gen !== tcReloadGen) return;   // a newer search/navigation already ran

  tcState.path = data.path || "";
  tcState.parent = data.parent ?? (data.path ? "" : null);
  try { if (tcState.path) localStorage.setItem(TC_FOLDER_KEY, tcState.path); } catch { /* private mode */ }
  $("tc-path").textContent = (data.search ? "🔍 “" + data.search + "” in " : "") + (data.path || "Drives")
    + (data.search ? "  ·  " + (data.entries || []).length + " match" + ((data.entries || []).length === 1 ? "" : "es") + (data.capped ? " (first 500)" : "") : "");
  $("tc-up").disabled = !data.path;
  tcRender(data.entries || []);
  tcUpdateSpace(data);
}

/* Free space on the drive of whatever folder is open — shown at the bottom so
   you can see whether a conversion will fit before queuing it. */
function tcUpdateSpace(data) {
  const el = $("tc-space");
  if (!el) return;
  if (data && data.freeBytes != null && data.totalBytes != null && data.totalBytes > 0) {
    const gb = b => b / 1073741824;
    const free = gb(data.freeBytes), total = gb(data.totalBytes);
    const usedPct = Math.round((total - free) / total * 100);
    el.textContent = "💽 " + (data.driveName || "drive") + " — "
      + free.toFixed(1) + " GB free of " + total.toFixed(1) + " GB (" + usedPct + "% used)";
  } else {
    el.textContent = "";
  }
}


/* ---- sorting the file explorer ----
   Three orders, because three different questions get asked of this list:
   what is it called, what still needs doing, and how much of it there is.
   Clicking the button already in use reverses it. Folders stay above files
   whichever is chosen - this is a directory listing, and burying the way out
   of a folder among its files makes it hard to navigate. */
const TC_SORT_KEY = "j0kers-tc-sort";
let tcSort = { key: "name", dir: 1 };
try {
  const saved = JSON.parse(localStorage.getItem(TC_SORT_KEY) || "null");
  if (saved && saved.key) tcSort = saved;
} catch { /* private mode, or nonsense in storage: keep the default */ }

/* Where a row belongs when sorting by status: what needs attention first,
   what is settled last. A folder is ranked by the same question as a file -
   is there anything in here still to do. */
function tcStatusRank(e) {
  if (e.type === "folder") {
    const s = e.summary || {};
    if (!s.media) return 5;                 // nothing in it
    if (s.needs > 0) return 0;              // work to do
    if (s.unknown > 0) return 2;            // still being read
    return 4;                               // TV-ready
  }
  if (e.state === "converting") return 1;
  if (e.needs) return 0;
  if (e.state === "done") return 3;
  return 4;                                 // plays as-is
}

/* The number each row shows: for a folder, how many files inside still need
   converting; for a file, its size. Different units, but the same question -
   how much is here - and they never mix, because folders sort among folders. */
function tcSortNumber(e) {
  // the same figure the pill shows, so sorting by "how much is here" agrees
  // with the number beside it rather than with a half-probed subset of it
  if (e.type === "folder") { const s = e.summary || {}; return tcOutstanding(s) * 1e12 + (s.media || 0); }
  const m = /([\d.]+)\s*(KB|MB|GB)/i.exec(e.detail || "");
  if (!m) return 0;
  const mult = { kb: 1024, mb: 1048576, gb: 1073741824 }[m[2].toLowerCase()] || 1;
  return parseFloat(m[1]) * mult;
}

function tcSortEntries(entries) {
  const rank = e => (e.type === "drive" ? 0 : e.type === "folder" ? 1 : 2);
  const name = e => (e.name || "").toLowerCase();
  return entries.slice().sort((a, b) => {
    const g = rank(a) - rank(b);
    if (g) return g;                        // drives, then folders, then files
    let c = 0;
    if (tcSort.key === "status") c = tcStatusRank(a) - tcStatusRank(b);
    else if (tcSort.key === "count") c = tcSortNumber(b) - tcSortNumber(a);   // most first
    if (!c) c = name(a).localeCompare(name(b), undefined, { numeric: true });
    else c *= tcSort.dir;
    return c;
  });
}

function tcSetSort(key) {
  tcSort = (tcSort.key === key) ? { key, dir: -tcSort.dir } : { key, dir: 1 };
  try { localStorage.setItem(TC_SORT_KEY, JSON.stringify(tcSort)); } catch { }
  if (tcState.lastEntries) tcRender(tcState.lastEntries);
}

function tcSyncSortButtons() {
  for (const b of document.querySelectorAll(".tc-sortbtn"))
    b.classList.toggle("on", b.dataset.sort === tcSort.key);
  const note = $("tc-sort-note");
  if (note) {
    note.textContent = tcSort.key === "name" ? (tcSort.dir > 0 ? "A–Z" : "Z–A")
      : tcSort.key === "status" ? (tcSort.dir > 0 ? "needs converting first" : "settled first")
      : (tcSort.dir > 0 ? "most first" : "fewest first");
  }
}
function tcRender(entries) {
  const list = $("tc-list");
  tcState.converting = false;
  tcState.lastEntries = entries;      // kept so Select all can re-render checkboxes
  tcState.selectable = [];            // full paths of the rows that have a checkbox
  tcSyncSortButtons();
  if (!entries.length) { list.innerHTML = '<div class="tc-row" style="color:var(--muted)">nothing here</div>'; tcSyncGo(); return; }
  list.innerHTML = "";
  // the chosen order; lastEntries above keeps the server's own list intact
  // so re-sorting never has to go back to the server
  for (const e of tcSortEntries(entries)) {
    const isDrive = e.type === "drive";
    const isFolder = e.type === "folder";
    const openable = (isDrive || isFolder) && e.ready !== false;
    const row = document.createElement("div");
    row.className = "tc-row" + (openable ? " folder" : "");
    // a drive's path is its own name (e.g. "C:\"); folders join onto the
    // current directory. Search results carry their own full path (they can be
    // anywhere under the folder), so use that when present.
    const full = e.path || (isDrive ? e.name : joinPath(tcState.path, e.name));
    if (!isDrive) tcState.selectable.push(full);

    // Drives are navigate-only — open one and tick the files inside, the way
    // the picker works — so a whole drive can't be queued by one checkbox.
    if (!isDrive) {
      const cb = document.createElement("input");
      cb.type = "checkbox";
      cb.checked = tcState.selected.has(full);
      cb.onchange = () => { cb.checked ? tcState.selected.add(full) : tcState.selected.delete(full); tcSyncGo(); };
      row.appendChild(cb);
    } else {
      const spacer = document.createElement("span");
      spacer.style.cssText = "flex:none;width:15px";   // keep names aligned with checkboxed rows
      row.appendChild(spacer);
    }

    const icon = document.createElement("span");
    icon.textContent = isDrive ? "💾" : isFolder ? "📁" : "🎞";
    icon.style.flex = "none";
    row.appendChild(icon);

    const nm = document.createElement("span");
    nm.className = "tc-name";
    // Files show their REAL filename — so a search for "(2)" visibly matches
    // "…(H.264) (2).mp4", and the .avi / (H.264).mp4 / (H.264) (2).mp4 copies
    // are told apart instead of all reading as the same prettified title.
    nm.textContent = isDrive ? (e.name + (e.label ? " (" + e.label + ")" : "")) : e.name;
    if (openable) { nm.title = "Open"; nm.onclick = () => tcReload(full); }
    else if (e.title && e.title !== e.name) nm.title = e.title;   // pretty name on hover
    row.appendChild(nm);

    if (isDrive) {
      const det = document.createElement("span");
      det.className = "tc-det";
      det.textContent = e.ready === false ? "not ready" : (e.detail || "");
      row.appendChild(det);
    }

    if (isFolder) {
      const pills = document.createElement("span");
      pills.style.cssText = "flex:none;display:flex;gap:4px;flex-wrap:wrap;justify-content:flex-end";
      pills.innerHTML = tcFolderPills(e.summary);
      row.appendChild(pills);
    }

    if (e.type === "file") {
      const det = document.createElement("span");
      det.className = "tc-det";
      det.textContent = e.detail || "";
      row.appendChild(det);

      const badge = document.createElement("span");
      badge.className = "tc-badge ";
      if (e.state === "done") { badge.className += "b-done"; badge.textContent = "converted"; }
      else if (e.state === "converting") { badge.className += "b-conv"; badge.textContent = "converting" + (e.percent != null ? " " + e.percent + "%" : "…"); tcState.converting = true; }
      else if (e.needs) { badge.className += "b-need"; badge.textContent = "needs converting"; }
      else { badge.className += "b-ok"; badge.textContent = "plays as-is"; }
      row.appendChild(badge);
    }
    list.appendChild(row);
  }
  tcSyncGo();

  /* "checking..." means the server has not read those files' codecs yet. It
     is doing so in the background, but this listing was fetched once and
     would otherwise sit with the same stale pills until somebody navigated
     away and back - which is what "they never refresh" was. Come back for a
     fresh listing while any remain, quietly and with a ceiling, so a folder
     of unreadable files cannot poll forever. */
  clearTimeout(tcCheckTimer);
  const stillChecking = entries.some(e => e.summary && e.summary.unknown > 0);
  if (!stillChecking) { tcCheckTries = 0; return; }
  if (tcCheckTries >= 40) return;                 // ~4 minutes, then leave it alone
  tcCheckTries++;
  const at = tcState.path;
  tcCheckTimer = setTimeout(() => {
    // only if the panel is still showing the same folder
    if (tcState.path === at) tcReload(at, true);
  }, 6000);
}

/* Select all / clear — ticks or unticks every checkbox currently listed
   (files and folders; drives are navigate-only). Toggles based on whether all
   are already selected. */
function tcSelectAll() {
  const all = tcState.selectable || [];
  if (!all.length) return;
  const allSelected = all.every(p => tcState.selected.has(p));
  if (allSelected) all.forEach(p => tcState.selected.delete(p));
  else all.forEach(p => tcState.selected.add(p));
  if (tcState.lastEntries) tcRender(tcState.lastEntries);   // re-render to reflect the checkboxes
}

/* Pills beside a folder — the same idea as the media conversion tool: how
   much media is inside and how much of it a TV needs converted. The counts
   come from the server, walked recursively and read from the codec cache
   only, so opening a folder never launches a probe. Red "to convert" leads;
   the settled facts trail. "checking…" means files under here haven't been
   probed yet — browse into them (or play them) and the counts fill in. */
/* What is still outstanding here — the number the headline shows.

   It is needs + unknown, not needs, and that is the whole fix for a pill that
   counted upwards. "needs" is only the files already probed AND known to be
   unplayable; anything not yet read by ffprobe sits in "unknown". Reading one
   moves it unknown → needs or unknown → ready, and the first of those made the
   headline GROW. That is what "110 to convert" turning into "123 to convert"
   was: not 13 new files, 13 files that had been there all along and had just
   been read. Probing happens constantly — the background sweep, opening this
   panel, and pressing Convert all do it — so the number climbed whatever the
   user did, including while conversions were finishing.

   needs + unknown cannot climb. Reading a file moves it unknown → needs (the
   sum is unchanged) or unknown → ready (the sum drops by one), and finishing a
   conversion moves it needs → done (drops by one). Every transition is level
   or downward, so the figure only ever falls — and it is an honest ceiling on
   what pressing Convert will actually queue, rather than a floor presented as
   a total. */
function tcOutstanding(s) {
  return ((s && s.needs) || 0) + ((s && s.unknown) || 0);
}

function tcFolderPills(s) {
  if (!s || !s.media)
    return '<span class="tc-badge b-ok" title="Nothing in here a TV could play or convert">no media</span>';
  let out = "";
  // The headline is whether there is anything TO convert. A file counts as
  // "needs converting" only if a TV can't play it AND no converted copy exists
  // yet — so a folder holding an original plus its H.264 copy reads as done,
  // not as "1/3", because the copy already plays and nothing is outstanding.
  //
  // While anything here is unread the figure is a ceiling, and it says so in
  // words — "up to 123" — rather than with the bare "+" this used to hang off
  // the end of the label. That "+" landed after the word convert ("110 to
  // convert+"), where it read as a stray character rather than as "at least",
  // and the tooltip beside it flatly asserted the number was final. Between
  // them there was nothing to warn anybody that the figure was still settling.
  const outstanding = tcOutstanding(s);
  if (outstanding > 0)
    out += '<span class="tc-badge b-need" title="'
         + (s.unknown
             ? outstanding + ' file(s) here may need converting — ' + s.needs
               + ' confirmed so far, ' + s.unknown + ' still being read. '
               + 'The number only falls as they are read.'
             : outstanding + ' file(s) here still need converting for a TV')
         + '">' + (s.unknown ? 'up to ' : '') + outstanding + ' to convert</span>';
  else
    out += '<span class="tc-badge b-done" title="Everything here plays on a TV — either converted, or a format a TV already handles">TV-ready</span>';
  // Neutral detail — facts, not work, so they never read as "incomplete".
  if (s.done > 0)
    out += '<span class="tc-badge b-ok" title="' + s.done + ' file(s) already have a converted copy">' + s.done + ' converted</span>';
  if (s.ready > 0)
    out += '<span class="tc-badge b-ok" title="' + s.ready + ' file(s) a TV plays as they are, no conversion needed">' + s.ready + ' play as-is</span>';
  if (s.capped) out += '<span class="tc-badge b-ok" title="Stopped counting at 4000 files">4000+</span>';
  return out;
}

/* View options for the conversion list — the media conversion tool's "Active
   first" toggle, adapted to this list (running + waiting). Remembered per
   browser. */
const TC_ORDER_KEY = "j0kers-tc-conv-order";
const TC_ORDERS = ["active", "waiting", "az"];
const TC_ORDER_LABEL = { active: "⇅ Active first", waiting: "⇅ Waiting first", az: "⇅ A → Z" };
const TC_ORDER_WHY = {
  active: "Running first, then what's waiting in the order it will run.",
  waiting: "Waiting first — handy for pruning the queue — then what's running.",
  az: "Everything by title, running and waiting together.",
};
let tcConvOrder = (() => {
  try { const s = localStorage.getItem(TC_ORDER_KEY); return TC_ORDERS.includes(s) ? s : "active"; }
  catch { return "active"; }
})();

function paintTcConvOrder() {
  const b = $("tc-conv-order");
  if (!b) return;
  const next = TC_ORDERS[(TC_ORDERS.indexOf(tcConvOrder) + 1) % TC_ORDERS.length];
  b.textContent = TC_ORDER_LABEL[tcConvOrder];
  b.title = TC_ORDER_WHY[tcConvOrder] + "\n\nPress for " + TC_ORDER_LABEL[next].replace("⇅ ", "").toLowerCase() + ".";
}

function tcToggleConvOrder() {
  tcConvOrder = TC_ORDERS[(TC_ORDERS.indexOf(tcConvOrder) + 1) % TC_ORDERS.length];
  try { localStorage.setItem(TC_ORDER_KEY, tcConvOrder); } catch { /* private mode */ }
  paintTcConvOrder();
  renderConversions(lastRunning, lastQueued);   // re-order without waiting for the next poll
}

/* The conversion list — like the media conversion tool's: what's running now
   (with progress, no remove — it's already going) and what's still waiting
   (each with a ✕ to drop it before it starts). */
let lastRunning = [], lastQueued = [];
function renderConversions(running, queued) {
  const wrap = $("tc-conv-wrap"), list = $("tc-conv-list"), count = $("tc-conv-count"), clear = $("tc-conv-clear");
  if (!wrap) return;
  running = lastRunning = running || []; queued = lastQueued = queued || [];
  paintTcConvOrder();
  // Always visible, so it's findable and obviously shows nothing rather than
  // disappearing. Empty state when idle.
  if (!running.length && !queued.length) {
    count.textContent = "";
    if (clear) clear.style.display = "none";
    list.innerHTML = '<div class="tc-row" style="color:var(--muted);cursor:default">Nothing converting right now.</div>';
    return;
  }
  count.textContent = "· " + running.length + " running, " + queued.length + " waiting";
  if (clear) clear.style.display = queued.length ? "" : "none";
  list.innerHTML = "";

  // one flat list of items, tagged by kind, then ordered by the chosen view
  const items = running.map(r => ({ kind: "running", title: r.title || r.stream, percent: r.percent, stream: r.stream, eta: r.etaSeconds }))
    .concat(queued.map(q => ({ kind: "queued", title: q.title || q.path, path: q.path })));
  if (tcConvOrder === "az")
    items.sort((a, b) => (a.title || "").localeCompare(b.title || "", undefined, { sensitivity: "base" }));
  else if (tcConvOrder === "waiting")
    items.sort((a, b) => (a.kind === "queued" ? 0 : 1) - (b.kind === "queued" ? 0 : 1));
  // "active" keeps the natural running-then-waiting order (no sort needed)

  for (const it of items) {
    const r = document.createElement("div");
    r.className = "tc-row";
    const icon = document.createElement("span"); icon.textContent = "🎞"; icon.style.flex = "none";
    const nm = document.createElement("span"); nm.className = "tc-name"; nm.textContent = it.title;
    r.appendChild(icon); r.appendChild(nm);

    if (it.kind === "running") {
      const badge = document.createElement("span");
      badge.className = "tc-badge b-conv";
      badge.textContent = "converting " + (it.percent != null ? it.percent + "%" : "…");
      r.appendChild(badge);
      /* How much longer, at the rate this job is actually managing — the
         number the percentage never answered. Absent for the first few
         seconds, and for a source whose length could not be probed. */
      if (it.eta != null) {
        const eta = document.createElement("span");
        eta.className = "tc-badge b-ok";
        eta.title = "Estimated from how fast this conversion is actually going";
        eta.textContent = etaText(it.eta) + " left";
        r.appendChild(eta);
      }
      const cancel = document.createElement("button");
      cancel.className = "danger"; cancel.style.flex = "none"; cancel.textContent = "✕";
      cancel.title = "Cancel this conversion (the partial copy is discarded)";
      cancel.onclick = () => tcCancelRunning(it.stream, it.title);
      r.appendChild(cancel);
    } else {
      const badge = document.createElement("span");
      badge.className = "tc-badge b-ok"; badge.textContent = "waiting";
      r.appendChild(badge);
      const rm = document.createElement("button");
      rm.className = "danger"; rm.style.flex = "none"; rm.textContent = "✕";
      rm.title = "Remove from the queue";
      rm.onclick = () => tcRemoveQueued(it.path);   // path via closure — no escaping pitfalls
      r.appendChild(rm);
    }
    list.appendChild(r);
  }
}

/* Every one of these threw the response away. A refusal came back and was
   swallowed in silence, so the button simply appeared to do nothing - press
   it again, still nothing, with no way to find out why. This endpoint needs
   server-admin rights, so 403 is the likeliest refusal and the one worth
   naming. Say what happened instead of nothing. */
async function tcReport(r, what) {
  if (r && r.ok) return true;
  let why = "";
  try { why = (await r.json()).error || ""; } catch { /* not JSON */ }
  if (!why && r && r.status === 403) why = "this account does not have server-admin rights";
  alert("Could not " + what + (why ? ": " + why : "."));
  return false;
}

async function tcRemoveQueued(path) {
  try {
    const r = await fetch("/api/transcode/remove", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers() },
      body: JSON.stringify({ path }),
    });
    await tcReport(r, "remove that file from the queue");
  } catch { alert("Could not reach the server to change the queue."); }
  tick();   // pull a fresh status so the list updates at once
}

async function tcCancelRunning(stream, title) {
  if (!confirm("Cancel converting “" + (title || stream) + "”? The half-finished copy is discarded.")) return;
  try {
    await fetch("/api/transcode/remove", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers() },
      body: JSON.stringify({ stream }),
    }).then(r => tcReport(r, "cancel that conversion"));
  } catch { alert("Could not reach the server to cancel that conversion."); }
  tick();
}

async function tcClearQueue() {
  if (!confirm("Remove everything still waiting to convert? (Running conversions keep going.)")) return;
  try {
    const r = await fetch("/api/transcode/remove", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers() },
      body: JSON.stringify({ clear: true }),
    });
    await tcReport(r, "clear the queue");
  } catch { alert("Could not reach the server to clear the queue."); }
  tick();
}

function tcSyncGo() {
  const go = $("tc-go");
  go.disabled = tcState.selected.size === 0;
  go.textContent = tcState.selected.size ? "Transcode selected (" + tcState.selected.size + ")" : "Transcode selected";
  const del = $("tc-del");
  if (del) {
    del.disabled = tcState.selected.size === 0;
    del.textContent = tcState.selected.size ? "🗑 Delete checked (" + tcState.selected.size + ")" : "🗑 Delete checked";
  }
  const all = $("tc-all");
  if (all) {
    const list = tcState.selectable || [];
    all.disabled = list.length === 0;
    const allSelected = list.length > 0 && list.every(p => tcState.selected.has(p));
    all.textContent = allSelected ? "☐ Clear all" : "☑ Select all";
  }
}

function tcUp() { if (tcState.parent !== null) tcReload(tcState.parent); }

/* Refresh button: re-scan the folder (picks up files added/converted since it
   was opened) and pull a fresh status so the conversion list and free space
   update at once instead of on the next poll. */
function tcRefresh() {
  /* Unconditional: with no folder open this shows the "add a folder" prompt,
     which is the right answer and a visible one. Guarding it on tcState.path
     meant the button did nothing at all at the drive root - no movement, no
     message - which is indistinguishable from a broken button. */
  tcReload(tcState.path);
  tick();
}

async function tcTranscode() {
  const paths = [...tcState.selected];
  if (!paths.length) return;
  const msg = $("tc-msg");
  $("tc-go").disabled = true;
  msg.textContent = "queuing…";
  let result = "";
  try {
    const r = await fetch("/api/transcode", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers() },
      body: JSON.stringify({ paths }),
    });
    const d = await r.json();
    if (!r.ok) throw new Error(d.error || r.status);
    /* What was actually taken on, against what was found.

       The server settles only the first dozen files before answering — the
       rest are read and queued on a background task, and it reports that
       honestly as "pending". This used to drop `pending` on the floor and
       report `queued` on its own, so acting on a folder pill that said 110
       answered "9 file(s) queued for conversion": a number computed from
       twelve files, contradicting the pill that prompted the click and making
       it look as though most of the job had been refused. Say the whole
       shape of it instead — taken on, still being read, and found. */
    const gap = parseInt(($("tc-stagger") || {}).value || "0", 10);
    const pending = d.pending || 0;
    const skips = [];
    if (d.alreadyGood > 0) skips.push(d.alreadyGood + " already play on a TV");
    const already = d.needs != null ? d.needs - d.queued : d.found - d.queued;
    if (already > 0) skips.push(already + " already converted or in progress");
    if (pending > 0) skips.push(pending + " still being read and queued behind these");
    result = (d.queued > 0 || pending > 0)
      ? d.queued + " of " + d.found + " file(s) queued for conversion"
        + (skips.length ? " (" + skips.join(", ") + ")" : "")
        + (gap > 0 ? " — starting one every " + gap + "s once one is running" : "")
      : (skips.length ? "nothing to queue — " + skips.join(", ") : "no video files found");
  } catch (e) { result = "failed: " + e.message; }
  // refresh the listing first (it clears the message), then show the result so
  // it doesn't vanish the instant it appears
  await tcReload(tcState.path);
  msg.textContent = result;
}

