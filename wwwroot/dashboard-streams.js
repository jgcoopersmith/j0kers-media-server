/* The RTSP mounts and the HLS streams: the mount list with its live audio
   preview, the stream cards, and the drag-and-drop that puts them in the
   order you want. Split out of dashboard.html; see dashboard-core.js for why
   every function here stays global. */
"use strict";
let mountsLoaded = 0;
async function refreshMounts() {
  if (Date.now() - mountsLoaded < 15000) return;
  let data;
  try { data = await api("/api/mounts"); }
  catch { return; } // don't stamp the cache on failure — retry next tick
  mountsLoaded = Date.now();
  lastMounts = data;
  renderMounts();
}

let lastMounts = null;

/* Kept apart from the fetch so switching view doesn't re-hit the API. */
function renderMounts() {
  if (!lastMounts) return;
  // a poll landing mid-drag would rebuild the rows and drop what is in the
  // air; the drag's end spends this and redraws once it is safe
  if (draggingHls) { hlsRenderDeferred = true; return; }
  const view = cardView("mounts");
  const uriOf = m => "rtsp://" + location.hostname + ":" + rtspPort + m.path;
  let h = "";

  for (const m of inChosenOrder(REORDER.mnt, lastMounts.mounts, m => m.path)) {
    const uri = uriOf(m);
    if (view === "condensed") {
      // the path is the mount's identity; the full URI is a click away on Copy
      h += '<div class="tv-tile mnt-item" draggable="true" data-mnt-name="' + esc(m.path) + '">'
        + '<span class="mono" style="flex:1;min-width:0;color:var(--ink);font-size:12.5px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">'
        + esc(m.path) + '</span>'
        + '<button data-preview="' + esc(m.path) + '" data-act="preview" title="Play">▶</button>'
        + '<button data-act="copy" data-arg="' + esc(uri) + '" title="Copy the rtsp:// URI">⧉</button>'
        + '</div>';
      continue;
    }

    const detail = view === "info"
      ? '<div style="color:var(--muted);font-size:12px;margin-top:2px">source: ' + esc(m.source) + '</div>'
        + (m.description ? '<div style="color:var(--muted);font-size:12px">' + esc(m.description) + '</div>' : "")
        + '<div style="color:var(--muted);font-size:12px">' + (m.dynamic ? "added via dashboard" : "from server.json") + '</div>'
      : '<div style="color:var(--muted);font-size:12px">' + esc(m.source)
        + (m.description ? " · " + esc(m.description) : "") + (m.dynamic ? " · added via dashboard" : "") + '</div>';

    h += '<div class="mnt-item" data-mnt-name="' + esc(m.path) + '" style="display:flex;align-items:'
      + (view === "info" ? "flex-start" : "center")
      + ';gap:8px;padding:' + (view === "info" ? "10px" : "7px") + ' 0;border-bottom:1px solid var(--grid)">'
      + '<span class="mnt-grip" title="Drag to reorder">⠿</span>'
      + '<div style="flex:1;min-width:0"><div class="mono" style="color:var(--ink)">' + esc(uri) + '</div>'
      + detail + '</div>'
      + '<button data-preview="' + esc(m.path) + '" data-act="preview">▶ Play</button>'
      + '<button data-act="mount-stop" data-arg="' + esc(m.path) + '">■ Stop</button>'
      + '<button data-act="copy" data-arg="' + esc(uri) + '">Copy</button>'
      + '<button class="danger" title="Remove mount" data-act="mount-remove" data-arg="' + esc(m.path) + '">✕</button>'
      + '</div>';
  }

  const box = $("mounts");
  box.className = view === "condensed" ? "tv-grid" : "";
  box.innerHTML = h || '<div class="empty">No mounts configured.</div>';
  wireHlsDrag(box, REORDER.mnt);

  // outside the grid, so it doesn't become a tile
  if (lastMounts.announcementService && view !== "condensed") {
    const uri = lastMounts.announcementService.replace("<host>", location.hostname);
    box.insertAdjacentHTML("beforeend",
      '<div style="padding:8px 0 0;color:var(--muted);font-size:12px">Announcements: <span class="mono">'
      + esc(uri) + '</span></div>');
  }
  syncPreviewButtons();
}

/* ---- RTSP mount audio preview ----
   The server streams a live WAV (PCM16, 8 kHz); we skip the 44-byte header
   and schedule the samples gaplessly through Web Audio. A <audio> element
   can't do this: Chrome buffers low-bitrate live WAV for tens of seconds
   before starting. The VU meter proves audio is flowing even when muted. */
let preview = { ctrl: null, ctx: null, gain: null, path: null };
let previewPath = null; // kept for button sync

function syncPreviewButtons() {
  document.querySelectorAll("[data-preview]").forEach(b => {
    b.classList.toggle("playing", previewPath === b.dataset.preview);
  });
}

function stopPreview() {
  if (preview.ctrl) preview.ctrl.abort();
  if (preview.ctx) preview.ctx.close().catch(() => {});
  preview = { ctrl: null, ctx: null, gain: null, path: null };
  previewPath = null;
  $("mountplayer").style.display = "none";
  $("vu").style.width = "0%";
  syncPreviewButtons();
}

function togglePreview(btn) { startPreview(btn.dataset.preview); }

async function startPreview(path) {
  if (previewPath === path) return; // already playing; the Stop button ends it
  stopPreview();

  const msg = $("mountmsg");
  const ctrl = new AbortController();
  const ctx = new AudioContext();
  const gain = ctx.createGain();
  gain.gain.value = parseFloat($("vol").value);
  gain.connect(ctx.destination);
  preview = { ctrl, ctx, gain, path };
  previewPath = path;
  syncPreviewButtons();
  $("mountplayer").style.display = "block";

  // Autoplay policies can hand out a suspended context even from a click.
  try { await ctx.resume(); } catch {}
  msg.textContent = ctx.state === "running"
    ? "playing " + path + " — live stream, 8 kHz mono"
    : "audio context is '" + ctx.state + "' — click Play again or check the tab's sound permission";

  let nextTime = 0, leftover = null, skipped = 0;
  try {
    const r = await fetch("/api/preview?mount=" + encodeURIComponent(path), { headers: headers(), signal: ctrl.signal });
    if (!r.ok) throw new Error("preview " + r.status);
    const reader = r.body.getReader();
    while (true) {
      const { done, value } = await reader.read();
      if (done || preview.ctrl !== ctrl) break;

      // strip the 44-byte WAV header, then carry odd bytes between chunks
      let bytes = value;
      if (skipped < 44) {
        const cut = Math.min(44 - skipped, bytes.length);
        bytes = bytes.subarray(cut);
        skipped += cut;
        if (!bytes.length) continue;
      }
      if (leftover) {
        const merged = new Uint8Array(leftover.length + bytes.length);
        merged.set(leftover); merged.set(bytes, leftover.length);
        bytes = merged; leftover = null;
      }
      if (bytes.length & 1) {
        leftover = bytes.slice(bytes.length - 1);
        bytes = bytes.subarray(0, bytes.length - 1);
      }
      if (!bytes.length) continue;

      const samples = new Int16Array(bytes.buffer, bytes.byteOffset, bytes.length >> 1);
      const f32 = new Float32Array(samples.length);
      let peak = 0;
      for (let i = 0; i < samples.length; i++) {
        f32[i] = samples[i] / 32768;
        const a = Math.abs(f32[i]);
        if (a > peak) peak = a;
      }
      $("vu").style.width = Math.min(100, Math.round(peak * 130)) + "%";

      const buf = ctx.createBuffer(1, f32.length, 8000);
      buf.copyToChannel(f32, 0);
      const src = ctx.createBufferSource();
      src.buffer = buf;
      src.connect(gain);
      if (nextTime < ctx.currentTime + 0.15) nextTime = ctx.currentTime + 0.15; // jitter cushion
      src.start(nextTime);
      nextTime += buf.duration;
    }
  } catch (e) {
    if (preview.ctrl === ctrl && e.name !== "AbortError")
      msg.textContent = "stream error: " + e.message;
  }
  if (preview.ctrl === ctrl) stopPreview();
}

document.addEventListener("input", e => {
  if (e.target.id === "vol" && preview.gain) preview.gain.gain.value = parseFloat(e.target.value);
});

/* One watch link per connected network, for a stream.
   Only drawn when there is more than one: with a single address the link on
   the title already points at it, and repeating it would be noise. The
   address the dashboard itself is open on is marked, since that is the one
   already known to work from here — the others are for handing to a device
   on that network. */
function interfaceLinks(streamName) {
  if (hlsAddresses.length < 2) return "";
  const page = "/watch/" + encodeURIComponent(streamName);
  const raw = "/" + encodeURIComponent(streamName) + "/index.m3u8";
  return '<div class="if-links">'
    + hlsAddresses.map(i => {
        const url = mediaUrlOn(i.address, page);
        const here = i.address === location.hostname;
        const label = (i.kind === "wi-fi" ? "📶" : "🔌") + " " + i.address;
        const tip = (i.name || i.address) + (i.primary ? " · default route" : "")
          + (here ? " · the network you're on" : "");
        // One copy per address: the stream URL, which plays in VLC and in
        // any browser with HLS. The watch page (subtitle menu, hls.js for
        // browsers without HLS) stays reachable through the address link
        // itself and the stream's title.
        return '<a href="' + esc(url) + '" target="_blank" rel="noopener"'
          + (here ? ' class="here"' : '') + ' title="' + esc(tip) + '">' + esc(label) + '</a>'
          + '<button data-act="copy" data-arg="' + esc(mediaUrlOn(i.address, raw))
          + '" title="' + esc("Copy the stream URL on " + i.address)
          + '">🎬 Link</button>';
      }).join("")
    + "</div>";
}

let lastHls = null;
let lastTranscodeSig = null;   // set of active transcodes at the last poll; the list is re-fetched only when this changes

/* Conversions running right now, from the last status poll: {stream, title,
   percent, doneSeconds, durationSeconds}. Kept so the HLS card can show a
   file that is being converted but has no playlist on disk yet. */
let transcodingNow = [];
let hadTranscodes = false;

async function refreshHls() {
  try {
    const r = await fetch(mediaUrl("/"));
    const data = await r.json();
    $("t-hls").textContent = data.streams.length;
    $("t-hls-d").textContent = "on port " + hlsPort;
    lastHls = data;
    renderHls();
  } catch {
    // A poll that didn't come back is not news. It says nothing the user
    // can act on, it fires on an empty card for reasons that have nothing
    // to do with reachability, and the next poll is a second away — so
    // whatever the card is showing stays showing, including the ordinary
    // "no HLS streams" line. A server that is genuinely down is already
    // reported by the connection light in the header.
    lastHls = null;
  }
}

/* One row for a file still being converted: no playlist to play yet, so it
   reports how far along it is instead. A source whose length couldn't be
   probed has no percentage to give, and says how much it has produced
   rather than inventing one. */
function convertingRow(t) {
  const pct = typeof t.percent === "number" ? t.percent : null;
  const label = pct !== null ? pct + "%" : fmtClock(t.doneSeconds || 0) + " done";
  return '<div style="display:flex;align-items:center;gap:8px;padding:7px 0;border-bottom:1px solid var(--grid)">'
    + '<span class="hls-thumb hls-thumb-fallback" style="display:flex">⏳</span>'
    + '<div style="flex:1;min-width:0">'
    + '<div style="color:var(--ink);overflow:hidden;text-overflow:ellipsis;white-space:nowrap">'
    + esc(t.title || t.stream) + '</div>'
    + '<div style="color:var(--muted);font-size:11.5px">Converting · ' + esc(label)
    + ' — playable as soon as the first part is ready</div>'
    + '<div class="conv-bar"><div style="width:' + (pct !== null ? pct : 0) + '%"></div></div>'
    + '</div></div>';
}

/* Kept apart from the fetch so switching view doesn't re-hit the media port. */
function renderHls() {
  if (!lastHls) return;
  // A poll landing mid-drag would replace the row being held: 'drop' would
  // never reach a live node, and the dragend that follows would save the
  // order off the freshly drawn list — silently throwing the move away.
  // Redraw when the drag is over instead.
  if (draggingHls) { hlsRenderDeferred = true; return; }
  // The same is true of a plain text selection. The card polls every 2s
  // and used to rebuild unconditionally — replace the nodes a Range points
  // into and the browser collapses the selection, the instant it happens.
  // That is what made a stream's title look copyable for a moment and then
  // not: the highlight was real, and the very next poll erased it, often
  // before there was time to press Ctrl+C. Deferring here is not enough on
  // its own — nothing currently wakes a deferred redraw back up once the
  // selection clears, the way dragend does for a drag — so a listener does
  // that below.
  if (hlsSelectionActive()) { hlsRenderDeferred = true; return; }
  hlsRenderDeferred = false;
  const box = $("hls");

  /* Files being converted that have not reached the disk yet.
     Clicking a video scrolls this card into view, and until ffmpeg writes
     the first playlist there is nothing here to see — while the Transcodes
     tile that does know about it is thousands of pixels back up the page.
     So the conversion is shown here as well, where the click left you. */
  const listed = new Set(lastHls.streams.map(s => s.name.toLowerCase()));
  const pending = transcodingNow.filter(t => t && t.stream && !listed.has(t.stream.toLowerCase()));

  if (!lastHls.streams.length && !pending.length) {
    box.className = "";
    box.innerHTML = '<div class="empty">No HLS streams. Drop segment files in a subfolder of the media root.</div>';
    return;
  }

  const view = cardView("hls");
  let h = pending.map(convertingRow).join("");

  for (const s of hlsInChosenOrder(lastHls.streams)) {
    const url = mediaUrl(s.playlist);
    // the watch page plays in any browser (phones included); the raw
    // m3u8 is linked from that page for VLC users
    const watchUrl = mediaUrl("/watch/" + encodeURIComponent(s.name));
    // A conversion in progress: the hourglass alone said something was
    // happening but not how much, which after being scrolled down here from
    // the tiles is the whole question. Carry the figure the Transcodes tile
    // has, so it is answered where the click actually left you.
    const job = transcodingNow.find(t => t.stream === s.name);
    const converting = activeTranscodes.includes(s.name);
    const pct = job && typeof job.percent === "number" ? job.percent : null;
    const transcoding = converting
      ? '<span title="Converting — playable now, growing as it goes">⏳</span> ' : "";
    const convNote = converting
      ? '<div style="color:var(--muted);font-size:11.5px">Converting · '
        + (pct !== null ? pct + "%" : (job ? esc(fmtClock(job.doneSeconds || 0)) + " done" : "starting"))
        + '</div><div class="conv-bar"><div style="width:' + (pct !== null ? pct : 0) + '%"></div></div>'
      : "";
    // small poster frame taken from the stream's own media; click to play.
    // Falls back to an icon if a frame can't be grabbed.
    const posterUrl = mediaUrl("/" + encodeURIComponent(s.name) + "/thumb.jpg");
    // draggable="false" as well as the CSS: Firefox has no -webkit-user-drag,
    // and the attribute is what it honours. A tile still drags, because the
    // tile itself carries draggable="true" and this only speaks for the image.
    const thumb = cls => '<img class="' + cls + '" draggable="false" src="' + esc(posterUrl) + '" alt=""'
      + ' data-act="hls-play" data-arg="' + esc(url) + '" title="' + esc(s.source || s.name) + '"'
      + ' onerror="this.replaceWith(Object.assign(document.createElement(\'span\'),'
      + '{className:\'' + cls + ' hls-thumb-fallback\',textContent:\'🎬\'}))">';

    if (view === "condensed") {
      // poster and title only — the whole tile plays, as in the TV grid
      // the whole tile plays; it is also the drag handle, since a tile has
      // no spare room for a separate grip
      h += '<div class="tv-tile hls-item" draggable="true" data-hls-name="' + esc(s.name) + '"'
        + ' data-act="hls-play" data-arg="' + esc(url) + '" title="' + esc(s.name) + '">'
        + thumb("hls-thumb-sm")
        + '<span style="flex:1;min-width:0;color:var(--ink);font-size:12.5px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">'
        + transcoding + esc(s.title || s.name) + '</span></div>';
      continue;
    }

    // readable title on top, the stream id (what the URLs use) underneath
    h += '<div class="hls-item" data-hls-name="' + esc(s.name) + '"'
      + ' style="display:flex;align-items:' + (view === "info" ? "flex-start" : "center")
      + ';gap:8px;padding:' + (view === "info" ? "10px" : "7px") + ' 0;border-bottom:1px solid var(--grid)">'
      // a row is mostly buttons and links, so the drag needs a place of its
      // own rather than the whole row
      + '<span class="hls-grip" title="Drag to reorder">⠿</span>'
      + thumb("hls-thumb")
      + '<div style="flex:1;min-width:0">' + transcoding
      + '<a href="' + esc(watchUrl) + '" target="_blank" rel="noopener" draggable="false" title="Open the watch page">'
      + esc(s.title || s.name) + '</a>'
      + '<div class="mono" style="color:var(--muted);font-size:11px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">'
      + esc(s.name) + '</div>'
      + convNote
      + (view === "info"
          ? '<div class="mono" style="color:var(--muted);font-size:11px;margin-top:2px;word-break:break-all">'
            + esc(url) + '</div>'
            + (s.source ? '<div style="color:var(--muted);font-size:12px">' + esc(s.source) + '</div>' : "")
          : "")
      + interfaceLinks(s.name) + '</div>'
      + '<button data-act="hls-play" data-arg="' + esc(url) + '">▶ Play</button>'
      + '<button data-act="hls-stop" data-arg="' + esc(url) + '">■ Stop</button>'
      // Copy gives the playlist itself — what VLC, Kodi and every other
      // player wants, and what this button always gave until the watch
      // pages briefly took it over. The watch page hasn't lost its way in:
      // the stream's title links to it, and the per-network row carries a
      // page button per address. shareUrl keeps localhost out of it.
      + '<button data-act="copy" data-arg="' + esc(shareUrl(s.playlist))
      + '" title="' + esc("Copy the stream URL for VLC and other players — for a browser page, click the stream's name" + shareHostNote()) + '">Copy</button>'
      + '<button class="edit-only" title="Convert this media again from scratch — for a conversion made before the codec settings changed, or one that came out wrong" data-act="hls-retrans" data-arg="' + esc(s.name) + '">↻</button>'
      + '<button class="danger" title="Remove this from the list. The conversion stays on disk, so playing this media again brings it straight back." data-act="hls-remove" data-arg="' + esc(s.name) + '">✕</button></div>';
  }

  box.className = view === "condensed" ? "tv-grid" : "";
  box.innerHTML = h;
  wireHlsDrag(box);
}

/* ---- the HLS streams in whatever order you put them in ----
   The server lists streams as it finds them on disk, which is alphabetical
   and rarely the order you care about. Dragging one sets your own; it is
   remembered per browser, and applies to every view of the card.

   The stored list is names, not positions, so a stream that appears or
   disappears doesn't shuffle everything else. Anything not in it — a new
   stream — sorts after what is, in the server's order. */
/* Two cards reorder this way now — HLS streams and Live channels — so the
   machinery is described once and told which card it is working on. Each
   keeps its own remembered order; a drag in one is not a drag in the other. */
const REORDER = {
  hls: { key: "j0kers-hls-order", item: ".hls-item", grip: ".hls-grip",
         attr: "hlsName", nameSel: "[data-hls-name]", redraw: () => renderHls() },
  ch:  { key: "j0kers-channel-order", item: ".ch-item", grip: ".ch-grip",
         attr: "chName", nameSel: "[data-ch-name]", redraw: () => refreshChannels(true) },
  mnt: { key: "j0kers-mount-order", item: ".mnt-item", grip: ".mnt-grip",
         attr: "mntName", nameSel: "[data-mnt-name]", redraw: () => renderMounts() },
};

const HLS_ORDER_KEY = REORDER.hls.key;

function savedOrder(cfg) {
  try { return JSON.parse(localStorage.getItem(cfg.key) || "[]"); } catch { return []; }
}

/* Sorts by the remembered order. The stored list is names, not positions, so
   an item appearing or disappearing doesn't shuffle everything else, and a
   stable sort leaves anything unknown — something new — in the order the
   server gave it, after what is known. */
function inChosenOrder(cfg, items, nameOf) {
  const order = savedOrder(cfg);
  if (!order.length) return items;
  const rank = new Map(order.map((n, i) => [String(n).toLowerCase(), i]));
  const at = x => rank.has(nameOf(x).toLowerCase()) ? rank.get(nameOf(x).toLowerCase()) : Infinity;
  return items.map((s, i) => ({ s, i }))
    .sort((a, b) => { const ra = at(a.s), rb = at(b.s); return ra === rb ? a.i - b.i : ra - rb; })
    .map(x => x.s);
}

function hlsInChosenOrder(streams) {
  return inChosenOrder(REORDER.hls, streams, s => s.name);
}

function saveOrder(cfg, box) {
  const names = [...box.querySelectorAll(cfg.nameSel)].map(e => e.dataset[cfg.attr]);
  try { localStorage.setItem(cfg.key, JSON.stringify(names)); } catch {}
}

// only one drag is ever in the air, so one variable serves both cards
let draggingHls = null;
// set when a poll wanted to redraw during a drag; spent when the drag ends
let hlsRenderDeferred = false;

/* True while the window's selection has any part of itself inside #hls.
   collapsed excludes a plain click, which leaves a zero-length selection
   sitting wherever the caret landed — that is not a copy in progress and
   should not hold the card's redraw hostage forever. */
function hlsSelectionActive() {
  const sel = window.getSelection();
  if (!sel || sel.isCollapsed || sel.rangeCount === 0) return false;
  const box = document.getElementById("hls");
  return !!box && box.contains(sel.anchorNode);
}

// Catches a deferred redraw back up once the selection it was waiting on
// goes away — a Ctrl+C, a click elsewhere, pressing Escape. Without this,
// a poll that deferred stays deferred forever: nothing else asks renderHls
// to run again. selectionchange fires for every kind of selection on the
// page, most of which have nothing to do with this card, so the cheap
// checks (deferred? still active?) run before the more expensive contains().
document.addEventListener("selectionchange", () => {
  if (hlsRenderDeferred && !hlsSelectionActive()) renderHls();
});

/* Nothing in a list is dragged except by its grip.
   Reported precisely: selecting a stream's title alone does nothing, but a
   selection begun on the id line underneath and dragged up over the title
   works. What decides it is where the press lands. Press inside the title
   and the browser reads press-and-move on a link as picking the link up —
   a drag of the URL, so no selection ever starts. Press on the plain line
   below and it is an ordinary sweep, which then extends over the title
   without complaint.

   draggable="false" and -webkit-user-drag: none were meant to settle that
   and did not, so this stops asking politely: any dragstart that did not
   come from a row armed by its ⠿ grip is cancelled. Capture phase, so it
   runs before the row's own dragstart handler. Reordering is untouched —
   the grip sets item.draggable, and a condensed tile carries the attribute
   itself, and both are let through. The listener goes on the box, which
   survives re-rendering; the rows inside it do not. */
function blockStrayDrags(box) {
  if (box.dataset.dragGuard) return;
  box.dataset.dragGuard = "1";
  box.addEventListener("dragstart", e => {
    const item = e.target.closest && e.target.closest(".hls-item, .ch-item, .mnt-item");
    if (!item || !item.draggable) e.preventDefault();
  }, true);
}

function wireHlsDrag(box, cfg) {
  cfg = cfg || REORDER.hls;
  blockStrayDrags(box);
  for (const item of box.querySelectorAll(cfg.item)) {
    const grip = item.querySelector(cfg.grip);
    // rows are armed by their grip; a tile is its own handle
    if (grip) {
      grip.addEventListener("mousedown", () => { item.draggable = true; });
      grip.addEventListener("mouseup", () => { item.draggable = false; });
    }

    item.addEventListener("dragstart", e => {
      draggingHls = item;
      item.classList.add("dragging");
      if (e.dataTransfer) {
        e.dataTransfer.effectAllowed = "move";
        try { e.dataTransfer.setData("text/plain", item.dataset[cfg.attr]); } catch {}
      }
      e.stopPropagation();   // reordering a stream is not moving the card
    });

    item.addEventListener("dragend", () => {
      item.classList.remove("dragging");
      if (grip) item.draggable = false;
      draggingHls = null;
      clearHlsMarks(box, cfg);
      saveOrder(cfg, box);
      // a poll asked for a redraw while this was in the air
      if (hlsRenderDeferred) { hlsRenderDeferred = false; cfg.redraw(); }
    });

    item.addEventListener("dragover", e => {
      if (!draggingHls || draggingHls === item) return;
      e.preventDefault();
      e.stopPropagation();
      if (e.dataTransfer) e.dataTransfer.dropEffect = "move";
      clearHlsMarks(box, cfg);
      item.classList.add(beforeHls(item, e) ? "drop-above" : "drop-below");
    });

    item.addEventListener("dragleave", () => item.classList.remove("drop-above", "drop-below"));

    item.addEventListener("drop", e => {
      if (!draggingHls || draggingHls === item) return;
      e.preventDefault();
      e.stopPropagation();
      box.insertBefore(draggingHls, beforeHls(item, e) ? item : item.nextSibling);
      clearHlsMarks(box, cfg);
      saveOrder(cfg, box);
    });
  }
}

/* Tiles sit side by side, rows stack — so which half of the target counts
   as "before" depends on which way the list runs. */
function beforeHls(item, e) {
  const box = item.getBoundingClientRect();
  return item.closest(".tv-grid")
    ? e.clientX < box.left + box.width / 2
    : e.clientY < box.top + box.height / 2;
}

function clearHlsMarks(box, cfg) {
  for (const i of box.querySelectorAll((cfg || REORDER.hls).item))
    i.classList.remove("drop-above", "drop-below");
}


