/* The power button and the Config dialog behind it - bind address, ports,
   HTTPS, DLNA sharing, and where the log and media folders live. Split out
   of dashboard.html; see dashboard-core.js for why every function here
   stays global. */
"use strict";
/* ---- power button: start/stop the streaming services ---- */
async function togglePower() {
  const running = $("powerbtn").classList.contains("on");
  if (running && !confirm("Stop the RTSP and HLS services? Active sessions will be disconnected. The dashboard stays available to start them again.")) return;
  try {
    await fetch("/api/server/" + (running ? "stop" : "start"), { method: "POST", headers: headers() });
  } catch {}
  tick();
}

/* ---- config dialog: hostname (bind address) and ports ---- */
let cfgLoaded = null; // values as loaded, so Save only sends what changed
let streamRemoveAction = "ask";   // keep|delete once settled; "ask" prompts each time

let cfgInterfaces = [];

async function openConfig() {
  $("cfg-msg").textContent = "";
  try {
    const s = await api("/api/settings");
    cfgLoaded = s;
    $("cfg-bind").value = s.bindAddress;
    $("cfg-rtsp").value = s.rtspPort;
    $("cfg-hls").value = s.hlsPort;
    $("cfg-ctl").value = s.controlPort;
    $("cfg-link").value = s.linkLifetimeHours;
    showLinkLifetime();
    cfgInterfaces = s.interfaces || [];
    renderCfgInterfaces();

    $("cfg-tray").checked = !!s.minimizeToTray;
    $("cfg-open").checked = s.openDashboardOnStart !== false;
    $("cfg-tray").disabled = !s.traySupported;
    $("cfg-tray-note").textContent = s.traySupported
      ? "Applies immediately. The icon starts in the taskbar's hidden ^ area — drag it out to pin it."
      : "Windows only — on macOS/Linux run the server under systemd, launchd, or nohup.";

    $("cfg-announce").checked = !!s.discoveryEnabled;
    $("cfg-announce-note").textContent = s.discoveryHostName
      ? "Applies immediately. Lets devices find the server by name (" + location.protocol + "//"
        + s.discoveryHostName + ".local:" + s.controlPort + "/) instead of by IP, and lists it "
        + "in Windows' Network folder. Announcing says only that the server exists — signing in is still required."
      : "Applies immediately. Announcing says only that the server exists — signing in is still required.";

    $("cfg-https").checked = !!s.httpsEnabled;
    httpsActive = !!s.httpsActive;
    httpsOwnCert = s.httpsOwnCertificate !== false;
    renderHttpsNote();

    $("cfg-dlna").checked = !!s.dlnaEnabled;
    cfgDlnaFolders = s.dlnaFolders || 0;
    try {
      const d = await api("/api/dlna");
      cfgDlnaShare = d.folders || [];
      cfgDlnaPort = d.port || 0;
      cfgDlnaPlain = !!d.plainPort;
    } catch { cfgDlnaShare = []; cfgDlnaPort = 0; cfgDlnaPlain = false; }
    renderDlnaNote();

    $("cfg-loglevel").value = (s.logLevel || "info").toLowerCase();
    $("cfg-logfile").checked = !!s.logToFile;
    // the full path, not "logs" — it's what Browse opens at, and it answers
    // "where are they actually going?" without a second trip
    $("cfg-logdir").value = s.logDirectoryResolved || s.logDirectory || "logs";
    $("cfg-logperiod").value = (s.logRotatePeriod || "daily").toLowerCase();
    $("cfg-logsize").value = s.logRotateSizeMb ?? 0;
    $("cfg-logkeep").value = s.logMaxFiles ?? 7;
    cfgLogResolved = s.logDirectoryResolved || "";
    // the box shows the resolved path, so the "what changed?" baseline has to
    // be that too — otherwise every save would look like a folder change
    s.logDirectory = $("cfg-logdir").value;
    cfgLogFiles = s.logFiles || [];
    renderLogging();

    // transcodes directory: show the resolved path (what Browse opens at), and
    // make the "what changed?" baseline that same path so an untouched save
    // doesn't look like a directory change
    $("cfg-mediaroot").value = s.mediaRootResolved || s.mediaRoot || "media";
    var rmAct = (s.streamRemoveAction || "ask").toLowerCase();
    $("cfg-rm-keep").checked = rmAct === "keep";
    $("cfg-rm-delete").checked = rmAct === "delete";
    cfgMediaResolved = s.mediaRootResolved || "";
    s.mediaRoot = $("cfg-mediaroot").value;
  } catch (e) {
    $("cfg-msg").textContent = "could not load settings: " + e.message;
  }
  $("cfg-overlay").style.display = "flex";
}

/* With 0.0.0.0 the server answers on every connected network — show which
   ones those are, since a phone has to use the address on its own subnet.
   Click one to bind to just that interface instead. */
function renderCfgInterfaces() {
  const box = $("cfg-ifaces");
  const bind = $("cfg-bind").value.trim();
  if (bind !== "0.0.0.0") {
    box.innerHTML = bind && bind !== "127.0.0.1" && bind !== "localhost"
      ? '<div style="color:var(--muted);font-size:11.5px">bound to this address only — use 0.0.0.0 for every network</div>'
      : '<div style="color:var(--muted);font-size:11.5px">local machine only — use 0.0.0.0 to reach it from phones and other PCs</div>';
    return;
  }
  if (!cfgInterfaces.length) {
    box.innerHTML = '<div style="color:var(--muted);font-size:11.5px">no connected network interfaces found</div>';
    return;
  }
  const port = parseInt($("cfg-ctl").value, 10) || 9090;
  let h = '<div style="color:var(--muted);font-size:11.5px;margin-bottom:3px">'
        + 'listening on all interfaces — reachable at:</div>';
  for (const i of cfgInterfaces) {
    h += '<div style="display:flex;align-items:center;gap:6px;padding:2px 0;font-size:12px">'
      + '<span>' + (i.kind === "wi-fi" ? "📶" : "🔌") + '</span>'
      + '<a class="mono" href="' + location.protocol + '//' + esc(i.address) + ':' + port + '/" target="_blank" rel="noopener">'
      + esc(i.address) + ':' + port + '</a>'
      + '<span style="color:var(--muted)">' + esc(i.name) + (i.primary ? " · default route" : "") + '</span>'
      + '<span style="flex:1"></span>'
      + '<button data-cfg-bind="' + esc(i.address) + '" style="font-size:11px;padding:1px 6px">bind only here</button>'
      + '</div>';
  }
  box.innerHTML = h;
}

/* The server can restart itself, so a setting that only applies at startup
   should not end in "now go and restart it" — on a tray-mode server that
   means hunting for an icon. Declining is fine: the setting is saved either
   way and takes effect whenever the server next starts. */
async function offerRestart(nowHttps) {
  const what = nowHttps
    ? "HTTPS starts when the server restarts.\n\n"
      + "Restart it now?\n\n"
      + "Windows may ask for administrator once to bind the certificate. You will come back "
      + "on https:// — your browser will warn about the self-signed certificate the first "
      + "time — everyone signs in again, and DLNA clients will most likely stop seeing the server."
    : "HTTPS stops when the server restarts.\n\nRestart it now? You will come back on http://.";
  if (!confirm(what)) return;

  let where = null;
  try {
    const r = await fetch("/api/server/restart", { method: "POST", headers: headers() });
    const data = await r.json();
    if (!r.ok) { alert(data.error || "could not restart — start the server again yourself"); return; }
    where = data.url;
  } catch {
    // the connection dropping *is* the restart beginning, so this is not
    // necessarily a failure — carry on to the waiting screen
  }
  waitForRestart(where);
}

/* Nothing on this page survives the restart, so replace it with something
   that says what is happening and sends them to the right address. The new
   scheme is a different origin, so this cannot poll it — it counts down and
   then goes, leaving a link for when it takes longer than expected. */
function waitForRestart(url) {
  const target = url || location.href;
  document.body.innerHTML =
    '<div style="display:grid;place-items:center;height:100vh;font-family:system-ui;text-align:center;gap:14px">'
    + '<div><div style="font-size:15px;margin-bottom:6px">Restarting the server…</div>'
    + '<div id="rs-count" style="color:#888;font-size:13px">taking you to ' + esc(target) + ' shortly</div>'
    + '<div style="margin-top:14px"><a id="rs-go" href="' + esc(target) + '">go there now</a></div></div></div>';
  let left = 8;
  const tick = setInterval(() => {
    left--;
    const c = document.getElementById("rs-count");
    if (c) c.textContent = left > 0
      ? "taking you to " + target + " in " + left + "s"
      : "taking you to " + target + "…";
    if (left <= 0) { clearInterval(tick); location.href = target; }
  }, 1000);
}

/* HTTPS is the one switch that cannot apply itself: the listeners are bound
   already, and binding the certificate to the ports needs the elevation
   prompt that only startup asks for. So the note is mostly about what
   happens next, and what the switch will cost. */
let httpsActive = false, httpsOwnCert = true;

function renderHttpsNote() {
  const want = $("cfg-https").checked;
  const note = $("cfg-https-note");
  if (want === httpsActive) {
    note.innerHTML = want
      ? "On. The dashboard and the media port are served over TLS."
      : "Off. The dashboard and the media port are served over plain HTTP — fine on a "
        + "trusted network, readable by anything on the wire beyond one.";
    return;
  }
  note.innerHTML = want
    ? "<b>Takes effect when the server restarts</b>, and Windows will ask for administrator "
      + "once to bind the certificate to the ports."
      + (httpsOwnCert ? " The server will use a certificate it made itself, so browsers warn "
                      + "until you trust it." : "")
      + " Everyone signs in again — <code>https://…</code> is a different origin from "
      + "<code>http://…</code>. DLNA keeps working: it moves to a plain-HTTP port of its own, "
      + "since TVs cannot do TLS and DLNA has no sign-in to protect anyway."
    : "<b>Takes effect when the server restarts.</b> Everyone signs in again, and links "
      + "you have saved with <code>https://</code> will need changing back.";
}

/* DLNA is the one switch here that gives something away, so the note says
   so plainly rather than describing a feature — and names the folders that
   would actually go out, since that is the decision being made. */
let cfgDlnaFolders = 0, cfgDlnaShare = [], cfgDlnaPort = 0, cfgDlnaPlain = false;

function renderDlnaNote() {
  const on = $("cfg-dlna").checked;
  const box = $("cfg-dlna-folders");
  const picked = cfgDlnaShare.filter(f => f.shared).length;

  $("cfg-dlna-note").innerHTML = on
    ? "Applies immediately. <b>DLNA has no sign-in</b> — every device on this network can browse and play "
      + "whatever is ticked below, with no account. Requests from outside the local network are refused."
      + (cfgDlnaPlain
          ? " While the server is on HTTPS this runs on plain HTTP at port <b>" + cfgDlnaPort
            + "</b>, because televisions cannot do TLS. Nothing else is served there."
          : "")
    : "Lets a TV browse the library from its own <i>Media Server</i> input, for devices with no browser "
      + "and no VLC. Files are served whole, so what plays is whatever the device can decode itself.";

  if (!on) { box.innerHTML = ""; return; }

  let h = '<div style="display:flex;align-items:center;gap:8px;margin-bottom:3px">'
    + '<span style="color:var(--muted);font-size:11.5px;flex:1">'
    + (!cfgDlnaShare.length
        ? "No folders yet — add one and a TV will find it here."
        : "Shared over DLNA — " + (picked ? picked + " of " + cfgDlnaShare.length : "<b>nothing yet</b>") + ":")
    + "</span>"
    + '<button onclick="addDlnaFolder()">+ Add folder</button></div>';

  if (cfgDlnaShare.length) {
    h += '<div style="max-height:150px;overflow-y:auto;border:1px solid var(--grid);border-radius:8px;padding:4px 2px">';
    for (const i in cfgDlnaShare) {
      const f = cfgDlnaShare[i];
      h += '<div style="display:flex;align-items:center;gap:8px;padding:3px 8px;font-size:12.5px">'
        + '<label style="display:flex;align-items:center;gap:8px;flex:1;min-width:0;cursor:pointer;'
        + 'text-transform:none;letter-spacing:0;color:var(--ink);margin:0">'
        + '<input type="checkbox" data-dlna-folder="' + i + '"' + (f.shared ? " checked" : "")
        + ' style="width:15px;height:15px">'
        + "<span>" + esc(f.name) + "</span>"
        + '<span class="mono" style="color:var(--muted);font-size:11px;overflow:hidden;text-overflow:ellipsis;'
        + 'white-space:nowrap;flex:1" title="' + esc(f.path) + '">' + esc(f.path) + "</span>"
        + "</label>"
        + (f.missing ? '<span style="color:var(--muted);font-size:11px">missing</span>' : "")
        + '<button data-dlna-remove="' + esc(f.path) + '" title="Remove this folder from the media library">✕</button>'
        + "</div>";
    }
    h += "</div>";
  }
  box.innerHTML = h;
}

/* These are the media library's folders, not a list of DLNA's own — so
   adding one here adds it to the library everywhere, and removing one takes
   it out of the dashboard too. Said plainly on the way out rather than
   discovered afterwards. */
async function addDlnaFolder() {
  const p = await pickPath({ mode: "folder", title: "Add a folder to the media library" });
  if (!p) return;
  const r = await fetch("/api/library", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers() },
    body: JSON.stringify({ folder: p }),
  });
  const data = await r.json().catch(() => ({}));
  if (!r.ok) { $("cfg-msg").textContent = data.error || ("could not add the folder: " + r.status); return; }
  // a folder added here is meant to be shared — that is why it was added
  cfgDlnaShare.push({ path: data.added || p, name: (data.added || p).split(/[\\/]/).filter(Boolean).pop(), shared: true });
  refreshLibraryRoots(true);
  renderDlnaNote();
}

async function removeDlnaFolder(path) {
  if (!confirm("Remove this folder from the media library?\n\n" + path
      + "\n\nIt leaves the dashboard's library as well, not just DLNA. Nothing on disk is touched."))
    return;
  await fetch("/api/library?folder=" + encodeURIComponent(path), { method: "DELETE", headers: headers() });
  cfgDlnaShare = cfgDlnaShare.filter(f => f.path !== path);
  refreshLibraryRoots(true);
  renderDlnaNote();
}

/* Ticking a folder only marks it; Save & apply is what shares it — the
   dialog's one Save has to mean this too, or a stray click would publish a
   folder the moment it was made. */
$("cfg-dlna-folders").addEventListener("change", e => {
  const box = e.target.closest("[data-dlna-folder]");
  if (!box) return;
  cfgDlnaShare[box.dataset.dlnaFolder].shared = box.checked;
  renderDlnaNote();
});
$("cfg-dlna-folders").addEventListener("click", e => {
  const btn = e.target.closest("[data-dlna-remove]");
  if (btn) removeDlnaFolder(btn.dataset.dlnaRemove);
});

async function saveDlnaShare() {
  await fetch("/api/dlna", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers() },
    body: JSON.stringify({ folders: cfgDlnaShare.filter(f => f.shared).map(f => f.path) }),
  });
}

/* ---- logging section of the config dialog ---- */
let cfgLogResolved = "", cfgLogFiles = [];
let cfgMediaResolved = "";

function renderLogging() {
  const on = $("cfg-logfile").checked;
  $("cfg-log-box").style.opacity = on ? "1" : ".45";
  for (const id of ["cfg-logdir", "cfg-logdir-browse", "cfg-logperiod", "cfg-logsize", "cfg-logkeep"])
    $(id).disabled = !on;

  $("cfg-logdir-note").textContent =
    "Browse to any folder, or type a relative one to keep it inside the server's own directory.";

  // spell the two rules back as one sentence: they combine, and either can be off
  const period = $("cfg-logperiod").value;
  const size = parseInt($("cfg-logsize").value, 10) || 0;
  const keep = parseInt($("cfg-logkeep").value, 10) || 0;
  const every = { hourly: "every hour", daily: "every day", weekly: "every week", monthly: "every month" }[period];
  let when;
  if (every && size) when = "A new file starts " + every + ", or sooner if it passes " + size + " MB.";
  else if (every) when = "A new file starts " + every + ", whatever its size.";
  else if (size) when = "A new file starts once the current one passes " + size + " MB.";
  else when = "One file that grows forever — set a period or a size to rotate it.";
  $("cfg-log-note").textContent = when + " " + (keep
    ? "The newest " + keep + " older file" + (keep === 1 ? " is" : "s are") + " kept; the rest are deleted."
    : "Older files are deleted straight away — only the current one is kept.");

  if (!on || !cfgLogFiles.length) { $("cfg-logfiles").textContent = ""; return; }
  const total = cfgLogFiles.reduce((a, f) => a + f.bytes, 0);
  $("cfg-logfiles").textContent = cfgLogFiles.length + " file"
    + (cfgLogFiles.length === 1 ? "" : "s") + " on disk, " + fmtBytes(total) + " in total.";
}
for (const id of ["cfg-logfile", "cfg-logperiod", "cfg-logsize", "cfg-logkeep"])
  $(id).addEventListener("input", renderLogging);

/* Opens at the folder in the box, so Browse continues from where the logs
   are now rather than from the drive list. */
async function browseLogDir() {
  const start = $("cfg-logdir").value.trim() || cfgLogResolved;
  const p = await pickPath({ mode: "folder", title: "Folder for the log files", startPath: start });
  if (p) { $("cfg-logdir").value = p; renderLogging(); }
}


/* The two "always" boxes are one setting with three states, shown as a pair
   because that is how it was asked for: ticking one clears the other, and
   neither ticked means ask each time. */
function syncRemoveAction(which) {
  if (which === "keep" && $("cfg-rm-keep").checked) $("cfg-rm-delete").checked = false;
  if (which === "delete" && $("cfg-rm-delete").checked) $("cfg-rm-keep").checked = false;
}

function removeActionFromBoxes() {
  if ($("cfg-rm-keep").checked) return "keep";
  if ($("cfg-rm-delete").checked) return "delete";
  return "ask";
}
async function browseMediaDir() {
  const start = $("cfg-mediaroot").value.trim() || cfgMediaResolved;
  const p = await pickPath({ mode: "folder", title: "Choose the transcodes directory", startPath: start });
  if (p) $("cfg-mediaroot").value = p;
}

/* 168 means nothing at a glance; "7 days" does */
function showLinkLifetime() {
  const h = parseInt($("cfg-link").value, 10);
  $("cfg-link-h").textContent = !h || h < 1 ? ""
    : h % 24 === 0 ? "· " + (h / 24) + (h === 24 ? " day" : " days")
    : h === 1 ? "· one hour" : "";
}
$("cfg-link").addEventListener("input", showLinkLifetime);

// keep the list in step with what's typed in the bind field
$("cfg-bind").addEventListener("input", renderCfgInterfaces);
$("cfg-ifaces").addEventListener("click", e => {
  const btn = e.target.closest("[data-cfg-bind]");
  if (!btn) return;
  $("cfg-bind").value = btn.dataset.cfgBind;
  renderCfgInterfaces();
});

function closeConfig() { $("cfg-overlay").style.display = "none"; }

async function saveConfig() {
  const msg = $("cfg-msg");
  msg.textContent = "";
  const current = {
    bindAddress: $("cfg-bind").value.trim(),
    rtspPort: parseInt($("cfg-rtsp").value, 10),
    hlsPort: parseInt($("cfg-hls").value, 10),
    controlPort: parseInt($("cfg-ctl").value, 10),
    linkLifetimeHours: parseInt($("cfg-link").value, 10),
    minimizeToTray: $("cfg-tray").checked,
    openDashboardOnStart: $("cfg-open").checked,
    discoveryEnabled: $("cfg-announce").checked,
    dlnaEnabled: $("cfg-dlna").checked,
    httpsEnabled: $("cfg-https").checked,
    logLevel: $("cfg-loglevel").value,
    logToFile: $("cfg-logfile").checked,
    logDirectory: $("cfg-logdir").value.trim() || "logs",
    logRotateSizeMb: parseInt($("cfg-logsize").value, 10) || 0,
    logRotatePeriod: $("cfg-logperiod").value,
    logMaxFiles: parseInt($("cfg-logkeep").value, 10) || 0,
    mediaRoot: $("cfg-mediaroot").value.trim() || "media",
    streamRemoveAction: removeActionFromBoxes(),
  };
  // only send fields the user actually changed, so untouched settings
  // keep following server.json instead of being frozen in settings.json
  const body = {};
  for (const k in current)
    if (!cfgLoaded || current[k] !== cfgLoaded[k]) body[k] = current[k];

  // the folder ticks live in their own endpoint, and are saved whether or
  // not anything else on the dialog changed
  try { await saveDlnaShare(); }
  catch (e) { msg.textContent = "could not save the DLNA folders: " + e.message; return; }

  if (!Object.keys(body).length) { closeConfig(); return; }
  try {
    const r = await fetch("/api/settings", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers() },
      body: JSON.stringify(body),
    });
    const data = await r.json();
    if (!r.ok) { msg.textContent = data.error || ("failed: " + r.status); return; }
    closeConfig();
    if (data.controlPortChanged)
      alert("Saved. RTSP/HLS are already on the new settings; the control port (this dashboard) moves to :" + body.controlPort + " the next time you restart the server.");
    // This setting does nothing until the server restarts, so offer to do
    // it rather than leaving someone to hunt for the tray icon — and say
    // where they will end up, since the address changes scheme.
    if (data.httpsChanged) await offerRestart(data.httpsEnabled);
    else if (data.mediaRootChanged)
      alert("Transcodes directory saved. New transcodes and live-channel streams write there after the server restarts.");
    mountsLoaded = 0; channelsLoaded = 0;
    // a new lifetime only reaches URLs via a freshly minted token
    if (body.linkLifetimeHours !== undefined) await refreshMediaToken();
    tick();
  } catch (e) {
    msg.textContent = "request failed: " + e.message;
  }
}
$("cfg-overlay").addEventListener("mousedown", e => { if (e.target.id === "cfg-overlay") closeConfig(); });

