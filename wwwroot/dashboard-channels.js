/* Live TV: the channels this server restreams, importing a lineup from an
   HDHomeRun, the free-TV providers, and the per-card view modes those
   listings use. Split out of dashboard.html; see dashboard-core.js for why
   every function here stays global. */
"use strict";
/* ---- live channels (HDHomeRun / IPTV / cameras → HLS) ---- */
function toggleAddChannel() {
  const f = $("addchannel");
  const show = f.style.display === "none";
  f.style.display = show ? "block" : "none";
  $("ch-toggle").textContent = show ? "− Close" : "+ Add channel";
  $("ch-msg").textContent = "";
  if (show) $("ch-name").focus();
}

/* ---- import a lineup from an HDHomeRun ----
   The tuner keeps its own scan; this reads the result and saves the picked
   channels idle, which is what pinning a free-TV channel does too. The
   address is remembered per browser — it is the one thing you would
   otherwise retype every time. */
let tunerChannels = [];

function toggleImportTuner() {
  const f = $("importtuner");
  const show = f.style.display === "none";
  f.style.display = show ? "block" : "none";
  $("tuner-toggle").textContent = show ? "− Close" : "📡 Import from tuner";
  $("tuner-msg").textContent = "";
  if (show) {
    $("tuner-host").value = $("tuner-host").value || localStorage.getItem("tunerHost") || "";
    $("tuner-host").focus();
  }
}

async function fetchTunerLineup() {
  const msg = $("tuner-msg"), out = $("tuner-result");
  const host = $("tuner-host").value.trim();
  msg.textContent = "";
  if (!host) { msg.textContent = "enter the tuner's address"; return; }
  out.innerHTML = '<div style="color:var(--muted);font-size:12px">reading ' + esc(host) + "…</div>";
  let data;
  try {
    data = await api("/api/tuner?host=" + encodeURIComponent(host));
  } catch (e) {
    out.innerHTML = "";
    msg.textContent = e.message;
    return;
  }
  localStorage.setItem("tunerHost", host);
  tunerChannels = data.channels || [];
  renderTunerLineup(data.device);
}

function renderTunerLineup(device) {
  const out = $("tuner-result");
  if (!tunerChannels.length) {
    out.innerHTML = '<div class="empty">That tuner has no channels — run a channel scan from its own app first.</div>';
    return;
  }
  const fresh = tunerChannels.filter(c => !c.alreadyAdded && !c.drm).length;
  let h = '<div style="color:var(--muted);font-size:12px;margin-bottom:6px">'
    + esc(device.name) + (device.model ? " · " + esc(device.model) : "")
    + (device.tuners ? " · " + device.tuners + " tuners" : "")
    + " · " + tunerChannels.length + " channels</div>";
  h += '<div style="display:flex;gap:8px;align-items:center;margin-bottom:6px">'
    + '<button onclick="tunerSelectAll(true)">Select all</button>'
    + '<button onclick="tunerSelectAll(false)">Select none</button>'
    + '<span style="flex:1"></span>'
    + '<button id="tuner-import" onclick="importTunerChannels()" style="color:var(--good);border-color:color-mix(in srgb,var(--good) 45%,transparent)">'
    + "Import selected</button></div>";
  h += '<div style="max-height:320px;overflow-y:auto;border:1px solid var(--grid);border-radius:8px">';
  for (const i in tunerChannels) {
    const c = tunerChannels[i];
    // already here, or copy-protected: shown so the lineup is the whole
    // lineup, but not offered — importing either produces a dead row
    const off = c.alreadyAdded || c.drm;
    const note = c.alreadyAdded ? "already added" : c.drm ? "copy-protected — cannot be restreamed" : "";
    h += '<label style="display:flex;align-items:center;gap:8px;padding:5px 10px;font-size:13px;'
      + 'cursor:' + (off ? "default" : "pointer") + ";opacity:" + (off ? ".5" : "1") + '">'
      + '<input type="checkbox" data-tuner-ch="' + i + '"' + (off ? " disabled" : " checked")
      + ' style="width:15px;height:15px">'
      + '<span class="mono" style="min-width:52px;color:var(--muted)">' + esc(c.number) + "</span>"
      + "<span>" + esc(c.name) + "</span>"
      + (c.hd ? '<span class="badge">HD</span>' : "")
      + '<span style="flex:1"></span>'
      + '<span style="color:var(--muted);font-size:11.5px">' + esc(note) + "</span>"
      + "</label>";
  }
  out.innerHTML = h + "</div>";
  if (!fresh) $("tuner-import").disabled = true;
}

function tunerSelectAll(on) {
  for (const box of document.querySelectorAll("[data-tuner-ch]"))
    if (!box.disabled) box.checked = on;
}

async function importTunerChannels() {
  const msg = $("tuner-msg");
  msg.textContent = "";
  const picked = [];
  for (const box of document.querySelectorAll("[data-tuner-ch]")) {
    if (!box.checked || box.disabled) continue;
    const c = tunerChannels[box.dataset.tunerCh];
    if (c) picked.push({ name: c.channelName, url: c.url });
  }
  if (!picked.length) { msg.textContent = "nothing selected"; return; }
  try {
    const r = await fetch("/api/channels/import", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers() },
      body: JSON.stringify({ channels: picked }),
    });
    const data = await r.json();
    if (!r.ok) { msg.textContent = data.error || ("failed: " + r.status); return; }
    // a partial import is the normal outcome on a second run — say which
    // ones didn't make it rather than reporting a flat success
    if (data.skipped && data.skipped.length)
      msg.textContent = "added " + data.added + ", skipped " + data.skipped.length
        + " (" + data.skipped.slice(0, 3).map(s => s.name + ": " + s.reason).join("; ") + ")";
    else
      toggleImportTuner();
    refreshChannels(true);
    fetchTunerLineup(); // re-mark what is now already added
  } catch (e) {
    msg.textContent = "request failed: " + e.message;
  }
}

async function submitAddChannel() {
  const msg = $("ch-msg");
  msg.textContent = "";
  const body = { name: $("ch-name").value.trim(), url: $("ch-url").value.trim() };
  if (!body.name || !body.url) { msg.textContent = "both a name and a stream URL are required"; return; }
  try {
    const r = await fetch("/api/channels", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers() },
      body: JSON.stringify(body),
    });
    const data = await r.json();
    if (!r.ok) { msg.textContent = data.error || ("failed: " + r.status); return; }
    toggleAddChannel();
    $("ch-name").value = ""; $("ch-url").value = "";
    refreshChannels(true);
  } catch (e) {
    msg.textContent = "request failed: " + e.message;
  }
}

let channelsLoaded = 0;
async function refreshChannels(force) {
  if (!force && Date.now() - channelsLoaded < 10000) return;
  let data;
  try {
    data = await api("/api/channels");
  } catch { return; }
  // a poll landing mid-drag would rebuild the rows and drop what is in the
  // air; the drag's end spends this and redraws once it is safe
  if (draggingHls) { hlsRenderDeferred = true; return; }
  channelsLoaded = Date.now();
  ffmpegOk = data.ffmpegAvailable;
  $("ffmpeg-warn").style.display = ffmpegOk ? "none" : "block";
  if (!data.channels.length) {
    $("channels").innerHTML = '<div class="empty">No channels. Add an HDHomeRun tuner URL, IPTV stream, or camera to restream it as HLS.</div>';
    return;
  }
  const view = cardView("ch");
  let h = "";
  for (const c of inChosenOrder(REORDER.ch, data.channels, c => c.name)) {
    // master.m3u8 when the restream carries subtitles — it names both the
    // video and the subtitle rendition, and index.m3u8 alone cannot. The
    // server reports which streams have one; the flag is per channel, so a
    // source without subtitles is unaffected.
    const leaf = (c.subtitles ? "/master.m3u8" : "/index.m3u8");
    const url = mediaUrl("/" + c.stream + leaf);
    // Play is for this page, so it may say localhost; Copy is for somewhere
    // else — VLC, a phone, another PC — and a localhost URL pasted there
    // points at that device instead. shareUrl picks an address the server
    // actually answers on. Same reasoning, same helper, as HLS Streams.
    const copyUrl = shareUrl("/" + c.stream + leaf);
    // What Play actually opens: the channel's own source, not the re-encode.
    //
    // A pinned channel stores the proxy URL it came from, and that proxy passes
    // the provider's stream through untouched - every rendition of its adaptive
    // ladder included. The ch-* stream beside it is that same source put through
    // ffmpeg so a television can play it over DLNA, and that costs a second
    // lossy encode of already-compressed video, an upscale to 1080p the source
    // never had, and the ability to drop to a lower rendition when the network
    // dips. That is what made it look softer and stall where the provider's own
    // site does not. A browser needs none of it, so it gets the source.
    // Falls back to the re-encode for a channel added by hand, which has no
    // proxy URL behind it.
    const viaProxy = (c.url || "").match(/\/api\/tv\/(?:watch|r)\?.*/);
    const playUrl = viaProxy ? viaProxy[0] : url;
    const live = c.status === "running";
    // idle = saved but never started (a fresh pin). It has no playlist on
    // disk yet, so offering Play would just fail — offer Start instead.
    const idle = c.status === "idle";

    if (view === "condensed") {
      // name and state only, packed into the same grid the lineup uses. The
      // whole tile acts: Play on a running channel, Start on an idle one,
      // which is the same choice the row's first button makes. It is its own
      // drag handle too — a tile has no spare room for a grip.
      h += '<div class="tv-tile ch-item" draggable="true" data-ch-name="' + esc(c.name) + '"'
        + ' data-act="' + (idle ? "ch-start" : "hls-play") + '" data-arg="' + esc(idle ? c.name : playUrl) + '"'
        + ' title="' + esc(c.name + " · " + c.status) + '">'
        + '<span class="state ' + (live ? "playing" : "ready") + '"><span class="dot"></span></span>'
        + '<span style="flex:1;min-width:0;color:var(--ink);font-size:12.5px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">'
        + esc(c.name) + '</span></div>';
      continue;
    }

    h += '<div class="ch-item" data-ch-name="' + esc(c.name) + '" style="display:flex;align-items:'
      + (view === "info" ? "flex-start" : "center") + ';gap:8px;padding:'
      + (view === "info" ? "10px" : "7px") + ' 0;border-bottom:1px solid var(--grid)">'
      // a row is mostly buttons, so the drag needs a handle of its own
      + '<span class="ch-grip" title="Drag to reorder">⠿</span>'
      + '<span class="state ' + (live ? "playing" : "ready") + '"><span class="dot"></span></span>'
      + '<div style="flex:1;min-width:0"><div style="color:var(--ink)">' + esc(c.name) + '</div>'
      + '<div class="mono" style="color:var(--muted);font-size:11.5px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">' + esc(c.url) + ' · ' + esc(c.status) + '</div>'
      // Info adds what the row has to truncate: the source URL in full, and
      // the stream directory the restream writes to — the name that appears
      // in a share link and in the log.
      + (view === "info"
          ? '<div class="mono" style="color:var(--muted);font-size:11px;margin-top:3px;word-break:break-all">'
            + esc(c.url) + '</div>'
            + '<div class="mono" style="color:var(--muted);font-size:11px">' + esc(c.stream) + '</div>'
          : "")
      + '</div>'
      + (idle
          ? '<button title="Start restreaming this channel from this server"'
            + ' data-act="ch-start" data-arg="' + esc(c.name) + '">▶ Start</button>'
          : '<button data-act="hls-play" data-arg="' + esc(playUrl) + '">▶ Play</button>'
            + '<button title="Stop restreaming (keeps the channel)" data-act="ch-stop" data-arg="' + esc(c.name) + '">■ Stop</button>'
            + '<button title="Restart channel" data-act="ch-restart" data-arg="' + esc(c.name) + '">↻</button>')
      // Same place in the row as on an HLS stream — Play, Stop, Copy, ✕ — so
      // the button is where it already is on the card above. An idle channel
      // has no playlist on disk yet, which is why it offers Start rather than
      // Play; the URL is still its permanent address, so the button stays and
      // says plainly that it answers once the channel is running.
      // Two copies, because a channel has two URLs and they are not
      // interchangeable. "Tab" is the watch page — the one that plays in a
      // browser, and the one to paste into another Chrome tab. "VLC" is the
      // bare playlist, which a browser cannot play and a real player wants.
      // They were one button labelled "Copy", handing out the playlist, and
      // pasting that into a browser plays nothing.
      + '<button data-act="copy" data-arg="' + esc(watchPageUrl(copyUrl, c.name))
      + '" title="' + esc("Copy a link that plays in a browser tab — paste it into Chrome"
                          + (idle ? ". It plays once the channel is started" : "")) + '">⧉ Tab</button>'
      + '<button data-act="copy" data-arg="' + esc(copyUrl) + '" title="'
      // shareHostNote() already opens with an em dash, so the idle half says
      // its piece as a sentence rather than putting two dashes in one line
      + esc("Copy the raw stream URL for VLC and other players — a browser cannot play this one"
            + (idle ? ". It answers once the channel is started" : "")
            + shareHostNote())
      + '">⧉ VLC</button>'
      + '<button class="danger" title="Remove channel" data-act="ch-remove" data-arg="' + esc(c.name) + '">✕</button>'
      + '</div>';
  }
  $("channels").className = view === "condensed" ? "tv-grid" : "";
  $("channels").innerHTML = h;
  wireHlsDrag($("channels"), REORDER.ch);
}

async function restartChannel(name) {
  await fetch("/api/channels/restart?name=" + encodeURIComponent(name), { method: "POST", headers: headers() });
  refreshChannels(true);
}

async function startChannel(name) {
  await fetch("/api/channels/start?name=" + encodeURIComponent(name), { method: "POST", headers: headers() });
  // ffmpeg needs a moment to write the first segments before the row is
  // worth redrawing as running
  refreshChannels(true);
  setTimeout(() => refreshChannels(true), 3000);
}

async function stopChannel(name) {
  await fetch("/api/channels/stop?name=" + encodeURIComponent(name), { method: "POST", headers: headers() });
  refreshChannels(true);
}

async function removeChannel(name, at) {
  if (!await confirmAt(at, "Remove channel " + name + "?"
      + "\n\nIts restream stops and its files are deleted. A pinned free-TV"
      + "\nchannel has to be found in the lineup and pinned again.", "Remove")) return;
  await fetch("/api/channels?name=" + encodeURIComponent(name), { method: "DELETE", headers: headers() });
  refreshChannels(true);
}

/* Remove every channel. Worth more warning than the HLS equivalent: a
   stream is a converted copy that can be made again from the file it came
   from, while a channel is the URL itself — delete a pinned free-TV channel
   and it has to be found in the lineup and pinned again. Same one-at-a-time
   reasoning: each removal stops a restream and deletes a directory. */
async function removeAllChannels(at) {
  let names = [];
  try {
    const data = await api("/api/channels");
    names = (data.channels || []).map(c => c.name);
  } catch { alert("Could not read the channel list."); return; }
  if (!names.length) { alert("There are no channels to delete."); return; }

  const shown = names.slice(0, 12).join("\n  ");
  const more = names.length > 12 ? "\n  …and " + (names.length - 12) + " more" : "";
  if (!await confirmAt(at, "Remove ALL " + names.length + " channel" + (names.length > 1 ? "s" : "") + "?\n\n  "
      + shown + more
      + "\n\nEach restream stops and its files are deleted. Pinned free-TV channels"
      + "\nhave to be found in the lineup and pinned again. This cannot be undone.", "Remove all")) return;

  // same reasoning as the HLS card: report what the server actually said
  const failed = [];
  for (const name of names) {
    try {
      const r = await fetch("/api/channels?name=" + encodeURIComponent(name), { method: "DELETE", headers: headers() });
      if (!r.ok) {
        const d = await r.json().catch(() => ({}));
        failed.push(name + " — " + (d.error || ("HTTP " + r.status)));
      }
    } catch (e) { failed.push(name + " — " + e.message); }
  }
  refreshChannels(true);
  if (failed.length) alert("Removed " + (names.length - failed.length) + " of " + names.length
    + ".\n\nThese could not be removed:\n  " + failed.join("\n  "));
}

/* ---- free TV (Pluto TV and playlist providers) ----
   The lineup is a few hundred channels, so it is fetched once per provider
   and filtered in the page: typing in the search box shouldn't cost a round
   trip. Playback goes through the server's proxy on this same origin, which
   is what keeps the provider's session token fresh — see Media/Providers. */
let lineup = [];            // the current provider's channels, unfiltered
let lineupProvider = "";
let lineupLoading = false;

async function loadProviders() {
  let data;
  try { data = await api("/api/tv/providers"); } catch { return; }
  const sel = $("tv-provider");
  if (!data.providers.length) {
    $("tv-lineup").innerHTML = '<div class="empty">No channel providers are configured.</div>';
    return;
  }
  sel.innerHTML = data.providers.map(p =>
    '<option value="' + esc(p.id) + '">' + esc(p.name) + '</option>').join("");
  loadLineup(true);
}

async function loadLineup(force) {
  const id = $("tv-provider").value || "pluto";
  if (!force && id === lineupProvider) return;
  if (lineupLoading) return;
  lineupLoading = true;
  $("tv-lineup").innerHTML = '<div class="empty">Loading the lineup…</div>';
  try {
    const data = await api("/api/tv/lineup?provider=" + encodeURIComponent(id));
    lineup = data.channels || [];
    lineupProvider = id;
    // Most playlist providers carry no group-title at all, and a filter whose
    // only option is "all" is furniture. Show it only where it does something.
    const groups = data.groups || [];
    const groupSel = $("tv-group");
    groupSel.innerHTML = '<option value="">all groups</option>'
      + groups.map(g => '<option value="' + esc(g) + '">' + esc(g) + '</option>').join("");
    groupSel.style.display = groups.length ? "" : "none";
    renderLineup();
  } catch (e) {
    $("tv-lineup").innerHTML = '<div class="empty">Could not load the lineup: ' + esc(e.message) + '</div>';
  } finally {
    lineupLoading = false;
  }
}

/* Debounced so holding a key down doesn't re-render the list per keystroke. */
let searchTimer = 0;
function lineupSearch() {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(renderLineup, 120);
}

/* ---- per-card view modes ----
   Condensed / Default / Info mean the same thing everywhere — less, the
   usual, more — but what that is belongs to each card. Remembered per card
   so choosing a dense channel grid doesn't also strip the mount list. */
const VIEW_NAMES = ["condensed", "default", "info"];
const cardViews = {};

function cardView(key) { return cardViews[key] || "default"; }

function setCardView(key, v, rerender) {
  cardViews[key] = VIEW_NAMES.includes(v) ? v : "default";
  try { localStorage.setItem("j0kers-view-" + key, cardViews[key]); } catch { /* private mode */ }
  const sel = $(key + "-view");
  if (sel && sel.value !== cardViews[key]) sel.value = cardViews[key];
  if (rerender) rerender();
}

/* Restores the remembered choice and points the control at it. */
function loadCardView(key) {
  try {
    const saved = localStorage.getItem("j0kers-view-" + key);
    if (VIEW_NAMES.includes(saved)) cardViews[key] = saved;
  } catch { /* private mode */ }
  const sel = $(key + "-view");
  if (sel) sel.value = cardView(key);
}

/* Every remembered view, restored before the first fetch rather than as each
   card wakes up. Doing it per-card meant the choice rode on that card's boot
   path: the lineup's was read inside the providers fetch, so a provider list
   that was slow, empty or unreachable left the control sitting on whatever
   the markup happened to list first while the code used something else. This
   runs off nothing but localStorage, so it cannot be missed. */
function restoreCardViews() {
  for (const key of ["hls", "mounts", "tv", "ch"]) loadCardView(key);
}
restoreCardViews();

/* The three ways to look at a lineup. Each sets its own row cap, because
   what makes a list unwieldy is its height, not its length: a condensed row
   is a fraction of an info row, so the same cap would either cripple one or
   bog down the other. */
const LINEUP_VIEWS = {
  condensed: { cap: 240, logo: 26, row: condensedTile, container: "tv-grid" },
  default:   { cap: 60,  logo: 38, row: defaultRow,    container: "" },
  info:      { cap: 25,  logo: 72, row: infoRow,       container: "" },
};
function setLineupView(v) { setCardView("tv", v, renderLineup); }

const tvButtons = c =>
  '<button data-act="tv-play" data-arg="' + esc(c.watch) + '">▶ Watch</button>'
  + '<button class="edit-only" title="Restream this channel from this server"'
  + ' data-act="tv-pin" data-arg="' + esc(c.id) + '" data-name="' + esc(c.name) + '">📌</button>';

const tvLogo = (c, px) => !px ? ""
  : c.logo
    ? '<img src="' + esc(c.logo) + '" alt="" loading="lazy" style="width:' + px + 'px;height:'
      + Math.round(px * 0.58) + 'px;object-fit:contain;flex:none">'
    : '<span style="width:' + px + 'px;flex:none"></span>';

const ellipsis = "overflow:hidden;text-overflow:ellipsis;white-space:nowrap";

/* One tile in the condensed grid: logo and name, and the tile itself is the
   Watch button — at this size a separate one would be most of the tile. The
   pin sits inside it, and the click dispatcher resolves to the nearest
   [data-act], so pressing 📌 pins rather than playing. Number and category
   move to the tooltip; the name is what anyone scans a grid for. */
function condensedTile(c) {
  const title = esc(String(c.number)) + (c.group ? " · " + esc(c.group) : "") + " — " + esc(c.name);
  return '<div class="tv-tile" title="' + title + '" data-act="tv-play" data-arg="' + esc(c.watch) + '">'
    + tvLogo(c, LINEUP_VIEWS.condensed.logo)
    + '<span style="flex:1;min-width:0;color:var(--ink);font-size:12.5px;' + ellipsis + '">' + esc(c.name) + '</span>'
    + '<button class="edit-only" title="Restream this channel from this server"'
    + ' data-act="tv-pin" data-arg="' + esc(c.id) + '" data-name="' + esc(c.name) + '">📌</button>'
    + '</div>';
}

function defaultRow(c) {
  return '<div style="display:flex;align-items:center;gap:8px;padding:7px 0;border-bottom:1px solid var(--grid)">'
    + tvLogo(c, LINEUP_VIEWS.default.logo)
    + '<div style="flex:1;min-width:0">'
    + '<div style="color:var(--ink);' + ellipsis + '">' + esc(c.name) + '</div>'
    + '<div class="mono" style="color:var(--muted);font-size:11.5px;' + ellipsis + '">'
    + esc(String(c.number)) + (c.group ? " · " + esc(c.group) : "") + '</div></div>'
    + tvButtons(c) + '</div>';
}

/* Adds what the channel actually shows. The summary wraps rather than
   being clipped — a description cut off mid-sentence is no use, and it is
   the whole reason for picking this view. */
function infoRow(c) {
  return '<div style="display:flex;align-items:flex-start;gap:10px;padding:10px 0;border-bottom:1px solid var(--grid)">'
    + tvLogo(c, LINEUP_VIEWS.info.logo)
    + '<div style="flex:1;min-width:0">'
    + '<div style="color:var(--ink);' + ellipsis + '">' + esc(c.name) + '</div>'
    + '<div class="mono" style="color:var(--muted);font-size:11.5px;margin-bottom:3px;' + ellipsis + '">'
    + esc(String(c.number)) + (c.group ? " · " + esc(c.group) : "") + '</div>'
    + (c.summary
        ? '<div style="color:var(--muted);font-size:12px;line-height:1.45">' + esc(c.summary) + '</div>'
        : '<div style="color:var(--muted);font-size:12px;font-style:italic">No description.</div>')
    + '</div>'
    + '<div style="display:flex;gap:6px;flex:none">' + tvButtons(c) + '</div></div>';
}

function renderLineup() {
  const q = ($("tv-search").value || "").trim().toLowerCase();
  const group = $("tv-group").value || "";
  const shown = lineup.filter(c =>
    (!group || c.group === group) &&
    (!q || c.name.toLowerCase().includes(q) || (c.summary || "").toLowerCase().includes(q)));

  const box = $("tv-lineup");
  if (!shown.length) {
    box.className = "";
    box.innerHTML = '<div class="empty">Nothing matches.</div>';
    return;
  }

  /* Capped because the whole lineup is several hundred rows and the browser
     is noticeably slower to lay them all out than anyone is to narrow the
     search. The count says what is hidden rather than pretending. */
  const view = LINEUP_VIEWS[cardView("tv")] || LINEUP_VIEWS.default;
  let h = shown.slice(0, view.cap).map(view.row).join("");
  if (shown.length > view.cap)
    h += '<div class="empty">' + (shown.length - view.cap) + ' more — narrow the search to see them.</div>';
  // the 📌 button carries .edit-only; CSS hides it for read-only accounts
  box.className = view.container;
  box.innerHTML = h;
}

async function pinTvChannel(id, name, at) {
  // Anchored to the 📌 that was pressed. The lineup runs to several hundred
  // channels, so a centred dialog asks about one of them from nowhere near
  // it — and in the condensed grid, where the tiles are small and alike,
  // there is nothing left on screen to say which one was aimed at.
  if (!await confirmAt(at, "Save \"" + name + "\" as a local channel?"
    + "\n\nIt is added to Live channels without starting anything. Press ▶ Start"
    + "\nthere when you want it restreaming for phones, TVs and VLC.", "Pin")) return;
  try {
    const r = await fetch("/api/tv/pin", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers() },
      body: JSON.stringify({ provider: lineupProvider, id, name }),
    });
    const data = await r.json();
    if (!r.ok) { alert(data.error || ("failed: " + r.status)); return; }
    refreshChannels(true);
  } catch (e) {
    alert("request failed: " + e.message);
  }
}

