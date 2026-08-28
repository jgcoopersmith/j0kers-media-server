/* The media library: pinned media, library roots, browsing and searching,
   the queue that plays a whole folder, saved playlists, and starting one
   file playing. Split out of dashboard.html; see dashboard-core.js for why
   every function here stays global. */
"use strict";
/* ---- media library: browse a folder; play movies/music, view pictures ---- */
const EXT = {
  video: ["mp4","m4v","mkv","avi","mov","webm","ts","m2ts","mts","wmv","flv","f4v","mpg","mpeg","mpe","m1v","m2v",
          "vob","3gp","3g2","ogv","ogm","mxf","asf","rm","rmvb","divx","dv","y4m","hevc","h264","264","265","av1","ivf","nut"],
  audio: ["mp3","flac","wav","m4a","m4b","ogg","oga","aac","wma","opus","aiff","aif","ape","wv","mka","ac3","eac3",
          "dts","amr","au","caf","mp2","mpa","mpga","ra","spx","tta","mid","ulaw","alaw","gsm"],
  image: ["jpg","jpeg","png","gif","webp","bmp","svg","avif","tif","tiff","ico","heic","heif","jxl","tga","dds","exr"],
};
function mediaKind(name) {
  const e = name.split(".").pop().toLowerCase();
  for (const k in EXT) if (EXT[k].includes(e)) return k;
  return null;
}
let ffmpegOk = false;

/* ---- pinned media: quick buttons at the top of the library ---- */
let favoritePaths = new Set();
let favoritesLoaded = 0;

async function refreshFavorites(force) {
  if (!force && Date.now() - favoritesLoaded < 15000) return;
  let data;
  try { data = await api("/api/favorites"); } catch { return; }
  favoritesLoaded = Date.now();
  favoritePaths = new Set(data.favorites.map(f => f.path.toLowerCase()));
  if (!data.favorites.length) { $("favorites").innerHTML = ""; return; }
  let h = '<div style="display:flex;flex-wrap:wrap;gap:6px">';
  for (const f of data.favorites) {
    h += '<span class="root-chip">'
      + '<span class="open" data-act="fav-open" data-arg="' + esc(f.path) + '" title="' + esc(f.path) + '">⭐ ' + esc(f.name) + '</span>'
      + '<button data-act="fav-remove" data-arg="' + esc(f.path) + '" title="Unpin (file is not deleted)" style="color:var(--critical)">✕</button>'
      + '</span>';
  }
  $("favorites").innerHTML = h + '</div>';
}

async function pinMedia(path, name) {
  const r = await fetch("/api/favorites", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers() },
    body: JSON.stringify({ path, name }),
  });
  if (!r.ok) { const d = await r.json().catch(() => ({})); alert(d.error || "pin failed"); return; }
  await refreshFavorites(true); // update favoritePaths BEFORE re-rendering the grid
  if (currentLibPath) loadLibrary(currentLibPath); // refresh star states
}

async function pinMediaViaPicker() {
  const p = await pickPath({ mode: "file", title: "Pick a media file to pin as a quick button" });
  if (!p) return;
  pinMedia(p);
}

async function unpinMedia(path) {
  await fetch("/api/favorites?path=" + encodeURIComponent(path), { method: "DELETE", headers: headers() });
  await refreshFavorites(true);
  if (currentLibPath) loadLibrary(currentLibPath);
}

/* Shared ☆/⭐ pin toggle used on folder rows, music rows, and tiles. */
function pinButton(path) {
  const pinned = favoritePaths.has(path.toLowerCase());
  return '<button data-act="' + (pinned ? "fav-remove" : "fav-add") + '" data-arg="' + esc(path) + '" title="'
    + (pinned ? "Unpin quick button" : "Pin as quick button") + '">' + (pinned ? "⭐" : "☆") + '</button>';
}

function openFavorite(path) {
  const kind = mediaKind(path);
  if (kind === "image") viewImage(path);
  else if (kind === "video" || kind === "audio") {
    if (!ffmpegOk) { alert("ffmpeg is not available for playback"); return; }
    queue.items = []; queue.order = []; queue.pos = -1; $("np").style.display = "none";
    playMedia(path);
  }
  else loadLibrary(path); // pinned folder → jump straight into it
}

/* ---- library roots: server-persisted source folders ---- */
let libraryRoots = [];
let currentLibPath = null;

async function refreshLibraryRoots(force) {
  try {
    const data = await api("/api/library");
    libraryRoots = data.folders;
  } catch { return; }

  // one-time migration from the old localStorage single-folder library
  const legacy = localStorage.getItem("j0kers-library");
  if (legacy && !libraryRoots.length) {
    localStorage.removeItem("j0kers-library");
    await fetch("/api/library", { method: "POST", headers: { "Content-Type": "application/json", ...headers() }, body: JSON.stringify({ folder: legacy }) }).catch(() => {});
    return refreshLibraryRoots(true);
  }

  let h = "";
  for (const f of libraryRoots) {
    const name = f.split(/[\\/]/).filter(Boolean).pop();
    const active = currentLibPath && (currentLibPath === f || currentLibPath.toLowerCase().startsWith(f.toLowerCase() + "\\"));
    h += '<span class="root-chip' + (active ? " active" : "") + '">'
      + '<span class="open" data-act="lib-open" data-arg="' + esc(f) + '" title="' + esc(f) + '">📂 ' + esc(name) + '</span>'
      + '<button class="danger" data-act="lib-root-remove" data-arg="' + esc(f) + '" title="Remove from library (files are not deleted)" style="color:var(--critical)">✕</button>'
      + '</span>';
  }
  $("lib-roots").innerHTML = h;
  renderSearchScope(); // a folder added or removed changes what can be searched
}

async function addLibraryRoot() {
  const p = await pickPath({ mode: "folder", title: "Add a folder to the media library" });
  if (!p) return;
  const r = await fetch("/api/library", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers() },
    body: JSON.stringify({ folder: p }),
  });
  if (!r.ok) { const d = await r.json().catch(() => ({})); alert(d.error || "add failed"); return; }
  await refreshLibraryRoots(true);
  loadLibrary(p);
}

async function removeLibraryRoot(folder) {
  if (!confirm("Remove this folder from the library? (No files on disk are deleted.)")) return;
  await fetch("/api/library?folder=" + encodeURIComponent(folder), { method: "DELETE", headers: headers() });
  if (currentLibPath && currentLibPath.toLowerCase().startsWith(folder.toLowerCase())) {
    currentLibPath = null;
    $("lib-path").textContent = "";
    $("library").innerHTML = '<div class="empty">Add a library folder — movies, music, and pictures inside it become playable here.</div>';
    $("lib-playall").style.display = "none";
    $("lib-save").style.display = "none";
  }
  refreshLibraryRoots(true);
}

/* ---- library browsing: grouped sections with thumbnails ---- */
/* Same-origin, so the session cookie rides along on its own — putting a
   long-lived key in an <img> URL would only leak it into history and logs. */
/* Bumped by an explicit Refresh. Poster frames are served with a day's
   cache, so a file replaced at the same path keeps showing the old frame in
   the browser even though the server regenerates it — its cache key carries
   the file's size and timestamp. Changing the URL is what makes the browser
   ask again. */
let thumbVersion = 0;

function thumbUrl(path) {
  return "/api/thumb?path=" + encodeURIComponent(path)
    + (thumbVersion ? "&v=" + thumbVersion : "");
}

/* Re-reads what is on screen from disk: new files appear, deleted ones go,
   replaced ones get a fresh poster. Nothing is cached server-side — the
   listing is read per request — so this is a genuine rescan rather than a
   redraw of what the page already had. */
async function rescanLibrary() {
  const btn = $("lib-refresh");
  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Rescanning…";
  thumbVersion = Date.now();
  try {
    // the chips first: a root may have been added or removed elsewhere
    await refreshLibraryRoots(true);
    refreshFavorites(true);
    refreshPlaylists(true);
    if (currentLibPath) await loadLibrary(currentLibPath);
  } catch (e) {
    $("lib-path").textContent = "rescan failed: " + e.message;
  } finally {
    btn.disabled = false;
    btn.textContent = label;
  }
}

/* ---- library search: the whole library, not the folder on screen ----
   Browsing answers "what is in here"; this answers "where is that film",
   which is the question you have once a library is more than one folder
   deep. Results replace the listing until the box is cleared, and the
   folder you were in is remembered so clearing puts you back. */
let libSearchTimer = null, libSearchGen = 0, libBrowsePath = null;

function librarySearchTyped() {
  clearTimeout(libSearchTimer);
  const q = $("lib-search").value.trim();
  $("lib-search-clear").style.display = q ? "" : "none";
  if (!q) { clearLibrarySearch(); return; }
  if (q.length < 2) return;   // the server refuses these anyway
  // typing is faster than a disk walk; only search once it pauses
  libSearchTimer = setTimeout(runLibrarySearch, 350);
}

/* Enter runs it now, and runs it again on text that hasn't changed — after
   files have moved or been added, re-asking the same question is the whole
   point, and typing doesn't fire when nothing was typed. */
function librarySearchNow() {
  clearTimeout(libSearchTimer);
  runLibrarySearch();
}

/* The scope box lists every library folder, not just the one on screen —
   narrowing to a folder should not mean navigating there first. The folder
   currently open is offered as well when it is deeper than a library root,
   and "Choose a folder…" reaches anything else. Rebuilt as you browse, and
   the current choice is kept whenever it still exists. */
const folderName = p => (p || "").split(/[\\/]/).filter(Boolean).pop() || p;

function renderSearchScope() {
  const sel = $("lib-scope");
  const here = currentLibPath;
  const roots = libraryRoots || [];
  const known = new Set(roots.map(r => r.toLowerCase()));

  let h = '<option value="">Everywhere</option>';
  for (const r of roots) h += '<option value="' + esc(r) + '">In ' + esc(folderName(r)) + "</option>";
  // the open folder, when it is somewhere below a root
  if (here && !known.has(here.toLowerCase()))
    h += '<option value="' + esc(here) + '">In ' + esc(folderName(here)) + " (open)</option>";
  // a folder chosen through the picker, which may be neither a root nor open
  if (scopeChoice && !known.has(scopeChoice.toLowerCase()) && scopeChoice !== here)
    h += '<option value="' + esc(scopeChoice) + '">In ' + esc(folderName(scopeChoice)) + "</option>";
  h += '<option value="__pick__">Choose a folder…</option>';
  sel.innerHTML = h;

  // Opening a library or a folder IS choosing where to search — searching
  // the whole library from inside one folder is not what the click meant.
  // An explicit pick (including Everywhere) overrides that until the next
  // time a folder is opened.
  const want = scopeChoice === null ? (here || "") : scopeChoice;
  sel.value = [...sel.options].some(o => o.value === want) ? want : "";
}

/* null = follow whatever folder is open; "" = Everywhere, chosen by hand;
   a path = that folder, chosen by hand. Kept apart from the box's value
   because the box is rebuilt on every poll, and a rebuild must not be able
   to undo a choice — nor to freeze one that navigation should have moved. */
let scopeChoice = null;

/* "Choose a folder…" is an action, not a scope — pick one, then scope to
   it. Cancelling has to put the box back where it was rather than leaving
   it on an option that means nothing. */
async function librarySearchScopeChanged() {
  const sel = $("lib-scope");
  if (sel.value !== "__pick__") {
    scopeChoice = sel.value;   // including "" for Everywhere, chosen by hand
    librarySearchNow();
    return;
  }
  sel.value = scopeChoice ?? "";
  const p = await pickPath({ mode: "folder", title: "Search inside which folder?", startPath: currentLibPath || "" });
  if (!p) return;
  scopeChoice = p;
  renderSearchScope();
  librarySearchNow();
}

async function runLibrarySearch() {
  const q = $("lib-search").value.trim();
  if (q.length < 2) return;
  const box = $("library");
  const gen = ++libSearchGen;
  if (libBrowsePath === null) libBrowsePath = currentLibPath;
  box.innerHTML = '<div class="empty">searching…</div>';

  const scope = $("lib-scope").value;
  let data;
  try {
    data = await api("/api/library/search?q=" + encodeURIComponent(q)
                     + (scope ? "&folder=" + encodeURIComponent(scope) : ""));
  } catch (e) {
    if (gen !== libSearchGen) return;
    box.innerHTML = '<div class="empty">search failed: ' + esc(e.message) + "</div>";
    return;
  }
  if (gen !== libSearchGen) return; // a later search overtook this one

  $("lib-path").textContent = "";
  $("lib-playall").style.display = "none";
  $("lib-save").style.display = "none";

  const videos = data.files.filter(f => f.kind === "video");
  const music = data.files.filter(f => f.kind === "audio");
  const pictures = data.files.filter(f => f.kind === "image");
  const total = data.files.length + data.folders.length;

  let h = '<div style="color:var(--muted);font-size:12px;margin-bottom:6px">'
    + (total ? total + " match" + (total === 1 ? "" : "es") : "nothing matched")
    + " for “" + esc(data.query) + "”"
    + (data.folder
        ? " in " + esc(data.folder.split(/[\\/]/).filter(Boolean).pop())
        : " across the whole library")
    + (data.truncated ? " · stopped at the first 300 — narrow the search"
       : data.timedOut ? " · gave up after 5 seconds, so there may be more" : "")
    + "</div>";

  // the folder each hit came from is half the answer when the same episode
  // name appears in three seasons
  const withFolder = (e, act, icon) =>
    libRow(icon, e.title || e.name, shortFolder(e.folder), ffmpegOk || act === "lib-img" ? act : "",
           e.path, pinButton(e.path), e.path);

  if (videos.length) {
    h += '<div class="lib-h">🎬 Videos <span class="cnt">' + videos.length + "</span></div>";
    for (const e of videos) h += withFolder(e, "lib-play", "🎬");
  }
  if (music.length) {
    h += '<div class="lib-h">🎵 Music <span class="cnt">' + music.length + "</span></div>";
    for (const e of music) h += withFolder(e, "lib-play", "🎵");
  }
  if (pictures.length) {
    h += '<div class="lib-h">🖼 Pictures <span class="cnt">' + pictures.length + "</span></div>";
    for (const e of pictures) h += withFolder(e, "lib-img", "🖼");
  }
  if (data.folders.length) {
    h += '<div class="lib-h">📁 Folders <span class="cnt">' + data.folders.length + "</span></div>";
    for (const e of data.folders)
      h += libRow("📁", e.name, shortFolder(e.folder), "lib-open", e.path, pinButton(e.path), e.path);
  }

  box.innerHTML = total ? h : h + '<div class="empty">no playable file matched that</div>';
}

/* A full path in the detail column pushes the name out of sight; the last
   two segments are what actually tells them apart. */
function shortFolder(folder) {
  const parts = (folder || "").split(/[\\/]/).filter(Boolean);
  return parts.length <= 2 ? folder : "…\\" + parts.slice(-2).join("\\");
}

function clearLibrarySearch() {
  clearTimeout(libSearchTimer);
  libSearchGen++;
  $("lib-search").value = "";
  $("lib-search-clear").style.display = "none";
  const back = libBrowsePath;
  libBrowsePath = null;
  if (back) loadLibrary(back);
  else if (libraryRoots.length) loadLibrary(libraryRoots[0]);
  else $("library").innerHTML = '<div class="empty">Add a library folder — movies, music, and pictures inside it become playable here.</div>';
}

async function loadLibrary(path) {
  // opening a folder from a result is the end of that search
  if (libBrowsePath !== null && $("lib-search").value.trim() === "") libBrowsePath = null;
  const box = $("library");
  // Opening a folder from halfway down a long listing left the page at that
  // scroll position, which in the new folder is somewhere near its end —
  // reading as "it jumped to the bottom". Remember whether this is a move
  // to a different folder, and put its start back under the eye below.
  const movedFolder = currentLibPath !== null && currentLibPath !== path;
  $("lib-path").textContent = path;
  box.innerHTML = '<div class="empty">loading…</div>';
  let data;
  try {
    const r = await fetch("/api/browse?path=" + encodeURIComponent(path), { headers: headers() });
    data = await r.json();
    if (!r.ok) throw new Error(data.error || r.status);
  } catch (e) {
    box.innerHTML = '<div class="empty">cannot open: ' + esc(e.message) + '</div>';
    return;
  }

  currentLibPath = data.path;
  scopeChoice = null;      // opening a folder is a fresh answer to "where?"
  renderSearchScope();
  $("lib-playall").style.display = ffmpegOk ? "" : "none";
  $("lib-save").style.display = "";
  refreshLibraryRoots(true); // update active chip highlight

  const join = n => joinPath(data.path, n);
  const folders = [], videos = [], music = [], pictures = [];
  for (const e of data.entries) {
    if (e.type === "folder") { folders.push(e); continue; }
    const kind = mediaKind(e.name);
    if (kind === "video") videos.push(e);
    else if (kind === "audio") music.push(e);
    else if (kind === "image") pictures.push(e);
  }

  let h = "";
  const isRoot = libraryRoots.some(f => f.toLowerCase() === data.path.toLowerCase());
  if (data.parent && !isRoot)
    // Bigger than the file and folder icons beside it, deliberately: this is
    // the one row in the list that navigates rather than opens, and it is
    // the one being aimed at most often.
    h += libRow('<span class="up-icon">⬆️</span>', ".. up", "", "lib-open", data.parent);

  // Playable things first, folders after. A folder holding a handful of
  // films beside seventy-odd subfolders used to put those films below about
  // two thousand pixels of folder rows, which reads as "it didn't pick up
  // the files" — they were listed, just past the fold. Folders are
  // navigation; in a media library the media is the point.
  if (videos.length) {
    h += '<div class="lib-h">🎬 Videos <span class="cnt">' + videos.length + '</span></div>';
    h += tileGrid(videos, join, "lib-play", "🎬");
  }

  if (music.length) {
    h += '<div class="lib-h">🎵 Music <span class="cnt">' + music.length + '</span></div>';
    for (const e of music) {
      const full = join(e.name);
      // show the readable title; the path still comes from e.name
      h += libRow("🎵", e.title || e.name, e.detail, ffmpegOk ? "lib-play" : "", full, pinButton(full), e.name);
    }
  }

  if (pictures.length) {
    h += '<div class="lib-h">🖼 Pictures <span class="cnt">' + pictures.length + '</span></div>';
    h += tileGrid(pictures, join, "lib-img", "🖼");
  }

  if (folders.length) {
    h += '<div class="lib-h">📁 Folders <span class="cnt">' + folders.length + '</span></div>';
    for (const e of folders) {
      const full = join(e.name);
      let extra = pinButton(full);
      if (ffmpegOk)
        extra = '<button data-act="lib-playfolder" data-arg="' + esc(full) + '" title="Play everything in this folder">▶ All</button>' + extra;
      h += libRow("📁", e.name, "", "lib-open", full, extra);
    }
  }

  box.innerHTML = h || '<div class="empty">nothing playable in this folder</div>';

  // Only on a move, and only when the start of the listing is off the top of
  // the window: someone who opened a folder already in view should not have
  // the page yanked around underneath them. The card's own header is the
  // target rather than the listing, so the path and the search box come with
  // it. No smooth scroll — this is a destination, not a journey.
  if (movedFolder) {
    const card = box.closest(".card");
    const top = card ? card.getBoundingClientRect().top : 0;
    // scrollTo rather than scrollIntoView: the latter does nothing here, and
    // silently — this page's cards are in a plain static column, and it
    // decided there was nothing to scroll
    if (card && top < 0) window.scrollTo(0, top + window.scrollY);
  }
}

function tileGrid(entries, join, act, fallbackIcon) {
  let h = '<div class="tiles-grid">';
  for (const e of entries) {
    const full = join(e.name);
    const clickable = act === "lib-img" || ffmpegOk;
    const pinned = favoritePaths.has(full.toLowerCase());
    h += '<div class="tile-item" style="position:relative;' + (clickable ? '' : 'opacity:.55;cursor:default') + '"'
      + (clickable ? ' data-act="' + act + '" data-arg="' + esc(full) + '"' : '') + ' title="' + esc(e.name) + '">'
      + '<button class="tile-pin" data-act="' + (pinned ? "fav-remove" : "fav-add") + '" data-arg="' + esc(full) + '" title="'
      + (pinned ? "Unpin quick button" : "Pin as quick button") + '">' + (pinned ? "⭐" : "☆") + '</button>'
      + '<img class="tile-thumb" loading="lazy" src="' + esc(thumbUrl(full)) + '" alt="" '
      + 'onerror="this.style.display=\'none\';this.nextElementSibling.style.display=\'flex\'">'
      + '<div class="tile-fallback">' + fallbackIcon + '</div>'
      // readable title on the tile; the real file name is the tooltip above
      + '<div class="tile-name">' + esc(e.title || e.name) + '</div>'
      + '</div>';
  }
  return h + '</div>';
}

function libRow(icon, name, detail, act, arg, extra, tooltip) {
  const clickable = act
    ? ' data-act="' + act + '" data-arg="' + esc(arg) + '" style="cursor:pointer"'
    : ' style="opacity:.55"';
  // tooltip carries the real file name when the label is a cleaned-up title
  const tip = tooltip ? ' title="' + esc(tooltip) + '"' : '';
  return '<div class="pick-row"' + clickable + tip + '><span class="icon">' + icon + '</span><span class="nm">'
    + esc(name) + '</span>' + (extra || "") + '<span class="det">' + esc(detail || "") + '</span></div>';
}

/* ---- folder playlists: queue every media file, auto-advance ----
   items is the folder's files in name order; order is the play order
   (identity, or shuffled); pos indexes into order. */
const queue = {
  items: [], order: [], pos: -1, label: null,
  shuffle: localStorage.getItem("j0kers-shuffle") === "1",
  loop: localStorage.getItem("j0kers-loop") === "1",
};

function buildOrder(firstItemIndex) {
  const order = queue.items.map((_, i) => i);
  if (queue.shuffle) {
    for (let i = order.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [order[i], order[j]] = [order[j], order[i]];
    }
    if (firstItemIndex !== undefined) {
      // keep the current item first so toggling shuffle doesn't skip it
      const at = order.indexOf(firstItemIndex);
      [order[0], order[at]] = [order[at], order[0]];
    }
  }
  queue.order = order;
}

function syncQueueButtons() {
  $("np-shuf").classList.toggle("playing", queue.shuffle);
  $("np-loop").classList.toggle("playing", queue.loop);
  const atStart = queue.pos <= 0, atEnd = queue.pos >= queue.order.length - 1;
  $("np-prev").disabled = atStart && !queue.loop;
  $("np-next").disabled = atEnd && !queue.loop;
}

async function listFolderMedia(folder) {
  const r = await fetch("/api/browse?path=" + encodeURIComponent(folder), { headers: headers() });
  const data = await r.json();
  if (!r.ok) throw new Error(data.error || r.status);
  return data.entries
    .filter(e => e.type === "file" && ["video", "audio"].includes(mediaKind(e.name)))
    .map(e => joinPath(folder, e.name));
}

async function playFolder(folder, label) {
  let items;
  try { items = await listFolderMedia(folder); }
  catch (e) { alert("cannot open folder: " + e.message); return; }
  if (!items.length) { alert("no playable media files in that folder"); return; }
  queue.items = items;
  queue.label = label || folder.split(/[\\/]/).filter(Boolean).pop();
  buildOrder();
  playQueuePos(0);
}

async function playQueuePos(pos) {
  if (queue.loop && queue.order.length) {
    // wrap in both directions; a forward wrap under shuffle gets a fresh order
    if (pos >= queue.order.length) { buildOrder(); pos = 0; }
    else if (pos < 0) pos = queue.order.length - 1;
  }
  if (pos < 0 || pos >= queue.order.length) { stopQueue(); return; }
  queue.pos = pos;
  const path = queue.items[queue.order[pos]];
  $("np").style.display = "flex";
  $("np-title").textContent = queue.label + " — " + path.split(/[\\/]/).pop();
  $("np-pos").textContent = "(" + (pos + 1) + "/" + queue.order.length + ")";
  syncQueueButtons();
  await playMedia(path);
}

// clear only the playlist state (leave the video alone — a caller about to
// start a single stream doesn't want the player torn down and rebuilt)
function clearQueueState() {
  queue.items = [];
  queue.order = [];
  queue.pos = -1;
  $("np").style.display = "none";
}

function stopQueue() {
  clearQueueState();
  const v = $("hlsvideo");
  v.pause();
  if (window._hls) { window._hls.destroy(); window._hls = null; }
  v.removeAttribute("src");
  v.load();
}

$("np-prev").addEventListener("click", () => playQueuePos(queue.pos - 1));
$("np-next").addEventListener("click", () => playQueuePos(queue.pos + 1));
$("np-stop").addEventListener("click", stopQueue);
$("np-shuf").addEventListener("click", () => {
  queue.shuffle = !queue.shuffle;
  localStorage.setItem("j0kers-shuffle", queue.shuffle ? "1" : "0");
  if (queue.items.length) {
    // rebuild the order around the item currently playing
    const currentItem = queue.order[queue.pos];
    buildOrder(currentItem);
    queue.pos = queue.order.indexOf(currentItem);
    $("np-pos").textContent = "(" + (queue.pos + 1) + "/" + queue.order.length + ")";
  }
  syncQueueButtons();
});
$("np-loop").addEventListener("click", () => {
  queue.loop = !queue.loop;
  localStorage.setItem("j0kers-loop", queue.loop ? "1" : "0");
  syncQueueButtons();
});
$("hlsvideo").addEventListener("ended", () => {
  if (queue.order.length) playQueuePos(queue.pos + 1); // wraps or stops per loop mode
});

$("lib-playall").addEventListener("click", () => { if (currentLibPath) playFolder(currentLibPath); });
$("lib-save").addEventListener("click", async () => {
  if (!currentLibPath) return;
  const suggested = currentLibPath.split(/[\\/]/).filter(Boolean).pop();
  const name = prompt("Playlist name:", suggested);
  if (!name) return;
  const r = await fetch("/api/playlists", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers() },
    body: JSON.stringify({ name, folder: currentLibPath }),
  });
  if (!r.ok) { const d = await r.json().catch(() => ({})); alert(d.error || "save failed"); return; }
  refreshPlaylists(true);
});

/* ---- remembered playlists strip ---- */
let playlistsLoaded = 0;
async function refreshPlaylists(force) {
  if (!force && Date.now() - playlistsLoaded < 15000) return;
  let data;
  try { data = await api("/api/playlists"); } catch { return; }
  playlistsLoaded = Date.now();
  if (!data.playlists.length) { $("playlists").innerHTML = ""; return; }
  let h = '<div style="display:flex;flex-wrap:wrap;gap:6px">';
  for (const p of data.playlists) {
    h += '<span style="display:inline-flex;align-items:center;gap:4px;border:1px solid var(--border);border-radius:999px;padding:2px 4px 2px 10px;font-size:12.5px;background:var(--surface-2)">'
      + '<span title="' + esc(p.folder) + '">🎞 ' + esc(p.name) + '</span>'
      + '<button data-act="pl-play" data-arg="' + esc(p.folder) + '" data-name="' + esc(p.name) + '" title="Play playlist" style="border:none;background:none;padding:2px 4px">▶</button>'
      + '<button data-act="pl-remove" data-arg="' + esc(p.name) + '" title="Forget playlist" style="border:none;background:none;padding:2px 4px;color:var(--critical)">✕</button>'
      + '</span>';
  }
  $("playlists").innerHTML = h + '</div>';
}

async function removePlaylist(name) {
  if (!confirm("Forget playlist '" + name + "'? (No media files are deleted.)")) return;
  await fetch("/api/playlists?name=" + encodeURIComponent(name), { method: "DELETE", headers: headers() });
  refreshPlaylists(true);
}

function viewImage(path) {
  $("lightbox-img").src = "/api/image?path=" + encodeURIComponent(path);
  $("lightbox").style.display = "flex";
}

/* movies & music: ask the server to transcode to HLS, then play inline */
let currentMediaPath = null; // file behind the inline player (for quality switches)

/* Library click: create the HLS stream (transcode starts in the background
   and the stream appears in the HLS Streams list) WITHOUT playing it. */
async function prepareMedia(path) {
  const height = mediaKind(path) === "video" ? parseInt($("pc-res").value, 10) || 0 : 0;
  // Say something before the request, not after it. Preparing a file it has
  // not seen before can hold this call for a second or more — sizing the
  // conversion cache, probing the source — and until now the page did
  // nothing at all in that time, which reads as a click that missed.
  const started = path.split(/[\\/]/).pop();
  flashPlayerMsg("Preparing " + started + "…");
  try {
    const r = await fetch("/api/play", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers() },
      body: JSON.stringify({ file: path, height }),
    });
    const data = await r.json();
    if (!r.ok) { alert(data.error || "could not prepare stream"); return; }
    // Show it as converting straight away. The status poll is two seconds
    // apart, and this card is where the scroll below is about to put them —
    // an empty card in the meantime is what reads as nothing happening.
    if (data.stream && !data.ready
        && !transcodingNow.some(t => t.stream === data.stream)) {
      transcodingNow = transcodingNow.concat([{
        stream: data.stream,
        title: path.split(/[\\/]/).pop(),
        percent: 0, doneSeconds: 0, durationSeconds: 0,
      }]);
      renderHls();   // draw it now; refreshHls below only resolves later
    }
    refreshHls();
    noteWatched();
    document.getElementById("hls").scrollIntoView({ behavior: "smooth", block: "center" });
  } catch (e) {
    alert("request failed: " + e.message);
  }
}

let playGeneration = 0; // bumped on every playMedia; stale polls bail out

async function playMedia(path, startAt) {
  const name = path.split(/[\\/]/).pop();
  currentMediaPath = path;
  const gen = ++playGeneration;
  // Claim the tab now, while the click that asked for it still counts as a
  // gesture: preparing a file takes seconds, and a tab opened after that
  // wait is a popup as far as the browser is concerned.
  const tab = openPlayerTab();
  if (tab) {
    try {
      tab.document.write("<title>Preparing…</title><body style=\"margin:0;background:#000;color:#999;"
        + "font:14px system-ui;display:grid;place-items:center;height:100vh\">preparing…</body>");
    } catch { /* about:blank in another tab can be picky; harmless */ }
  }
  // quality applies to video only; audio has nothing to scale
  const height = mediaKind(path) === "video" ? parseInt($("pc-res").value, 10) || 0 : 0;
  try {
    const r = await fetch("/api/play", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers() },
      body: JSON.stringify({ file: path, height }),
    });
    const data = await r.json();
    if (!r.ok) { alert(data.error || "playback failed"); return; }
    // the server has just recorded this play — don't leave a stale list
    noteWatched();

    const url = mediaUrl(data.playlist);
    $("hlsmsg") && ($("hlsmsg").textContent = "preparing " + name + "…");
    // wait for the transcoder to produce the playlist (usually 1–3 s)
    for (let i = 0; i < 40; i++) {
      if (data.ready) break;
      const s = await (await fetch("/api/play?stream=" + encodeURIComponent(data.stream), { headers: headers() })).json();
      if (s.ready) break;
      await new Promise(res2 => setTimeout(res2, 500));
    }
    if (gen !== playGeneration) {          // a newer play (rapid ⏭/⏮) superseded this one
      if (tab && !tab.closed) tab.close();  // and its tab is now pointless
      return;
    }
    playHls(url, startAt, tab);
  } catch (e) {
    alert("playback failed: " + e.message);
  }
}

