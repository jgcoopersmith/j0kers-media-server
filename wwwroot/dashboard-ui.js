/* The pieces the rest of the page borrows, and the start-up that sets it
   running: pickPath, the popconfirm, the clipboard fallback for plain http,
   the delegated data-act handler, card folding and card ordering.

   It is listed last because the bottom of it is the boot sequence, which
   calls into nearly every other file and so needs all of them loaded. Split
   out of dashboard.html; see dashboard-core.js for why every function here
   stays global. */
"use strict";
/* ==== pickPath(): reusable drive / folder / file picker =================
   Usage:
     const p = await pickPath();                                  // anything
     const f = await pickPath({ mode: "file",   title: "Pick a clip" });
     const d = await pickPath({ mode: "folder", title: "Media root" });
   Resolves with the absolute path, or null if cancelled.
   mode: "any" (default) | "file" | "folder" — what the Select button accepts
   (drives count as folders). Navigation: double-click or single-click a
   drive/folder to enter it, single-click selects, Esc cancels.
========================================================================= */
const picker = { resolve: null, mode: "any", path: "", parentPath: null, selected: null, selectedType: null };

function pickPath(opts = {}) {
  if (picker.resolve) pickerCancel(); // a second picker cancels the first
  return new Promise(resolve => {
    picker.resolve = resolve;
    picker.mode = opts.mode || "any";
    picker.selected = null;
    picker.selectedType = null;
    $("picker-title").textContent = opts.title ||
      (picker.mode === "file" ? "Select a file" : picker.mode === "folder" ? "Select a folder" : "Select a file or folder");
    $("picker-overlay").style.display = "flex";
    pickerLoad(opts.startPath || "");
  });
}

async function pickerLoad(path) {
  const list = $("picker-list"), msg = $("picker-msg");
  msg.textContent = "";
  list.innerHTML = '<div class="pick-row dim">loading…</div>';
  picker.selected = null;
  picker.selectedType = null;
  // clear the folder path while loading, so Select can't return the previous
  // folder if clicked mid-load
  picker.path = "";
  picker.parentPath = null;
  pickerSync();

  let data;
  try {
    const url = "/api/browse" + (path ? "?path=" + encodeURIComponent(path) : "");
    const r = await fetch(url, { headers: headers() });
    data = await r.json();
    if (!r.ok) throw new Error(data.error || r.status);
  } catch (e) {
    list.innerHTML = "";
    msg.textContent = "cannot open: " + e.message;
    return;
  }

  picker.path = data.path;
  picker.parentPath = data.parent; // null at drive root → back to drive list
  $("picker-path").textContent = data.path || "Drives";
  $("picker-up").disabled = !data.path;

  list.innerHTML = "";
  if (!data.entries.length)
    list.innerHTML = '<div class="pick-row dim">empty folder</div>';

  for (const e of data.entries) {
    const row = document.createElement("div");
    row.className = "pick-row" + (e.ready === false ? " dim" : "");
    const icon = e.type === "drive" ? "💾" : e.type === "folder" ? "📁" : "📄";
    const label = e.type === "drive" && e.label ? " (" + e.label + ")" : "";
    row.innerHTML = '<span class="icon">' + icon + '</span><span class="nm">' + esc(e.name + label) + '</span><span class="det">' + esc(e.detail || "") + '</span>';
    const full = e.type === "drive" ? e.name
      : joinPath(picker.path, e.name);

    if (e.ready !== false) {
      row.onclick = () => {
        list.querySelectorAll(".pick-row").forEach(r2 => r2.classList.remove("selected"));
        row.classList.add("selected");
        picker.selected = full;
        picker.selectedType = e.type;
        pickerSync();
      };
      if (e.type !== "file")
        row.ondblclick = () => pickerLoad(full);
    }
    list.appendChild(row);
  }
}

function pickerSync() {
  const ok = $("picker-ok");
  const t = picker.selectedType;
  let valid = false;
  if (picker.selected) {
    valid = picker.mode === "any"
      || (picker.mode === "file" && t === "file")
      || (picker.mode === "folder" && t !== "file");
  } else if (picker.mode !== "file" && picker.path) {
    valid = true; // no selection → the folder being viewed is selectable
  }
  ok.disabled = !valid;
  ok.textContent = picker.selected || !picker.path ? "Select" : "Select this folder";
}

function pickerUp() {
  if (picker.parentPath) pickerLoad(picker.parentPath);
  else pickerLoad(""); // drive root → drive list
}

function pickerFinish(value) {
  $("picker-overlay").style.display = "none";
  const resolve = picker.resolve;
  picker.resolve = null;
  if (resolve) resolve(value);
}

function pickerOk() {
  pickerFinish(picker.selected || picker.path || null);
}

function pickerCancel() { pickerFinish(null); }

document.addEventListener("keydown", e => {
  if (e.key === "Escape" && picker.resolve) pickerCancel();
});
$("picker-overlay").addEventListener("mousedown", e => {
  if (e.target.id === "picker-overlay") pickerCancel();
});

/* Clipboard that works over plain http too: navigator.clipboard only
   exists on secure contexts (https / localhost), so LAN access at
   http://<ip>:9090 needs the legacy execCommand path. */
/* Asks the question next to the button that raised it.
   Resolves true or false, so it drops in where confirm() was. With no
   anchor — called from somewhere without a button in hand — it falls back
   to confirm(), which is centred but still asks. */
let openPopconfirm = null;

/* The same popover with more than one way to say yes.
   Some questions are not yes-or-no: removing a conversion can keep the
   files or delete them, and those are different decisions rather than a
   confirmation and an afterthought. Resolves the chosen value, or null. */
function choiceAt(anchor, text, options, remember) {
  return confirmAt(anchor, text, null, options, remember);
}

function confirmAt(anchor, text, okLabel, options, remember) {
  if (!anchor || !anchor.getBoundingClientRect) {
    // no anchor: fall back to confirm(), taking the first option as "yes"
    if (options) return Promise.resolve(confirm(text) ? options[0].value : null);
    return Promise.resolve(confirm(text));
  }
  if (openPopconfirm) openPopconfirm();          // never two at once

  return new Promise(resolve => {
    const pop = document.createElement("div");
    pop.className = "popconfirm";
    const body = document.createElement("div");
    body.className = "pc-text";
    body.textContent = text;                      // textContent: a file name is not markup
    const row = document.createElement("div");
    row.className = "pc-row";
    const no = document.createElement("button");
    no.textContent = "Cancel";
    row.append(no);

    // one button per option, or the single yes/no pair. Cancel is always
    // first and always focused, so the safe answer is the one already
    // under the finger whichever shape this takes.
    const answers = options || [{ label: okLabel || "Remove", value: true, danger: true }];
    const yes = [];
    for (const opt of answers) {
      const b = document.createElement("button");
      b.textContent = opt.label;
      if (opt.danger) b.className = "pc-go";
      b.addEventListener("click", () => close(opt.value));
      row.append(b);
      yes.push(b);
    }
    let rememberEl = null;
    // An optional "always do this" tick. The caller passes an object and
    // reads its .checked afterwards, so adding this changed no existing
    // call site or return value.
    if (remember) {
      const wrap = document.createElement("label");
      wrap.className = "pc-remember";
      wrap.style.cssText = "display:flex;align-items:center;gap:6px;margin:8px 0 0;font-size:12px;cursor:pointer";
      const box = document.createElement("input");
      box.type = "checkbox";
      const cap = document.createElement("span");
      cap.textContent = remember.label || "Always do this";
      wrap.append(box, cap);
      remember.box = box;
      rememberEl = wrap;
    }
    pop.append(body);
    if (rememberEl) pop.append(rememberEl);
    pop.append(row);
    document.body.appendChild(pop);

    // a flag, not an identity check against openPopconfirm: that holds the
    // cancel wrapper rather than this function, so comparing them was always
    // unequal and nothing ever closed
    let settled = false;
    const close = answer => {
      if (settled) return;
      settled = true;
      // read the tick while the element still exists
      if (remember && remember.box) remember.checked = remember.box.checked;
      if (openPopconfirm === cancel) openPopconfirm = null;
      removeEventListener("keydown", onKey, true);
      removeEventListener("mousedown", onOutside, true);
      removeEventListener("scroll", onScroll, true);
      removeEventListener("resize", onScroll);
      pop.remove();
      resolve(answer);
    };
    const cancel = () => close(options ? null : false);
    openPopconfirm = cancel;

    // Anchored under the button, pulled back inside the window when it
    // would hang off an edge, and flipped above when there is no room below.
    const place = () => {
      const a = anchor.getBoundingClientRect();
      const p = pop.getBoundingClientRect();
      let left = Math.min(a.right - p.width, innerWidth - p.width - 8);
      left = Math.max(8, left);
      let top = a.bottom + 6;
      if (top + p.height > innerHeight - 8) top = a.top - p.height - 6;   // flip above
      // and clamp regardless: flipping above an anchor that is itself near
      // the bottom edge can still land off-screen, and a question you cannot
      // read is worse than one in the wrong place
      top = Math.min(Math.max(8, top), Math.max(8, innerHeight - p.height - 8));
      pop.style.left = left + "px";
      pop.style.top = top + "px";
    };
    place();

    const onKey = e => {
      if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); cancel(); }
      if (e.key === "Enter" && yes.includes(document.activeElement)) { e.preventDefault(); document.activeElement.click(); }
    };
    const onOutside = e => { if (!pop.contains(e.target)) cancel(); };
    // a popover pinned to a button that has scrolled away is worse than none
    const onScroll = () => cancel();

    no.addEventListener("click", cancel);
    addEventListener("keydown", onKey, true);
    addEventListener("mousedown", onOutside, true);
    addEventListener("scroll", onScroll, true);
    addEventListener("resize", onScroll);
    no.focus();                                   // Cancel is the safe default
  });
}

async function copyToClipboard(text) {
  if (navigator.clipboard && window.isSecureContext) {
    try { await navigator.clipboard.writeText(text); return true; } catch { /* fall through */ }
  }
  const ta = document.createElement("textarea");
  ta.value = text;
  ta.setAttribute("readonly", "");
  ta.style.position = "fixed";
  ta.style.opacity = "0";
  document.body.appendChild(ta);
  ta.select();
  ta.setSelectionRange(0, text.length);
  let ok = false;
  try { ok = document.execCommand("copy"); } catch { }
  ta.remove();
  return ok;
}


async function copyText(btn, text) {
  const ok = await copyToClipboard(text);
  if (!ok) { window.prompt("Copy the URL:", text); return; }
  // restore whatever the button said, not the word "Copy" — some of them
  // name which of a stream's two URLs they carry, and a row that ends up
  // with two buttons both reading "Copy" is worse than no label at all
  const was = btn.dataset.label || btn.textContent;
  btn.dataset.label = was;
  btn.classList.add("copied");
  btn.textContent = "Copied";
  setTimeout(() => { btn.classList.remove("copied"); btn.textContent = was; }, 1200);
}

/* Delegated clicks for all rendered rows. Arguments travel as data-arg
   attributes rather than inline JS strings, so file names and mount paths
   containing quotes or backslashes work. */
document.addEventListener("click", e => {
  const el = e.target.closest("[data-act]");
  if (!el) return;
  const arg = el.dataset.arg;
  switch (el.dataset.act) {
    case "preview":     togglePreview(el); break;
    case "copy":        copyText(el, arg); break;
    case "mount-stop":  if (previewPath === arg) stopPreview(); break;
    case "mount-remove": removeMount(arg); break;
    case "hls-play":    clearQueueState(); playHls(arg); break;
    case "hls-stop":    stopHlsVideo(); break;
    case "session-kill": killSession(arg); break;
    case "hls-remove":  removeHlsStream(arg, el); break;
    case "hls-retrans": retranscodeStream(arg, el); break;
    case "ch-restart":  restartChannel(arg); break;
    case "ch-start":    startChannel(arg); break;
    case "ch-stop":     stopChannel(arg); break;
    case "ch-remove":   removeChannel(arg, el); break;
    case "tv-play":     clearQueueState(); playHls(arg); break;
    case "tv-pin":      pinTvChannel(arg, el.dataset.name, el); break;
    case "lib-open":    loadLibrary(arg); break;
    case "lib-img":     viewImage(arg); break;
    case "lib-play":    prepareMedia(arg); break;
    case "lib-playfolder": playFolder(arg); break;
    case "lib-root-remove": removeLibraryRoot(arg); break;
    case "fav-open":    openFavorite(arg); break;
    case "fav-add":     pinMedia(arg); break;
    case "fav-remove":  unpinMedia(arg); break;
    case "pl-play":     playFolder(arg, el.dataset.name); break;
    case "pl-remove":   removePlaylist(arg); break;
    case "usr-edit":    toggleUserEdit(arg); break;
    case "usr-signout": signOutUser(arg); break;
    case "usr-save":    saveUser(arg); break;
    case "usr-remove":  removeUser(arg); break;
    case "usr-keys":    toggleUserKeys(arg); break;
    case "usr-key-new": mintUserKey(arg); break;
    case "key-revoke":  revokeKey(arg, el.dataset.user || null); break;
  }
});

for (const id of ["acct-overlay", "users-overlay"])
  $(id).addEventListener("mousedown", e => { if (e.target.id === id) closeOverlay(id); });

/* ---- the live link: how the server knows this page is open ----

   Closing the dashboard shuts the server down (unless it is set to minimize
   to the tray, which makes it a background service instead).

   This used to be announced with a pagehide beacon and nothing else, and the
   beacon does not arrive: closing the tab reached the server as no request at
   all, so the process hung about until a thirty-second silence timer noticed
   — and on a machine that was converting something, not even then.

   So the page holds a connection open instead. Nothing has to be announced:
   when the browser goes, the socket goes with it, and the server's next
   write down it fails. That is the operating system reporting the close
   rather than the page being asked to report its own, which is why it works
   when the beacon does not. It also keeps a locked screen or a backgrounded
   tab — where the polling stops but the connection does not — from looking
   like a close, which is what the thirty seconds were really guarding
   against.

   EventSource reconnects on its own after a drop, so a refresh, a blip or a
   navigation reopens this without anything here noticing; the server allows
   for that with a grace period before it acts. */
let liveLink = null;
function openLiveLink() {
  if (liveLink) return;
  try {
    // EventSource cannot set an Authorization header. A cookie rides along
    // by itself on a same-origin request; a key/token sign-in goes in the
    // query string, which this server already accepts (?key=/?token=) and
    // never writes to the log.
    liveLink = new EventSource("/api/server/session"
      + (token ? "?token=" + encodeURIComponent(token) : ""));
  } catch {
    liveLink = null;   // no EventSource: the beacon and the silence watch stand
  }
}

/* Still sent, because it arrives a beat before the socket drops on the
   browsers where it does arrive, and a beat is worth having.

   The body is not decoration. sendBeacon with nothing to send posts without
   a Content-Length, and the Windows HTTP stack answers that with 411 Length
   Required before the server ever sees it. A few bytes give the request a
   length and it arrives. */
window.addEventListener("pagehide", () => {
  navigator.sendBeacon("/api/server/closing", "bye");
});

/* Establish who we are before the first poll, so the page never briefly
   renders administration controls to someone who isn't an administrator. */
decoratePasswords();   // the account and add-user fields that ship in the markup

/* ---- fold a card down to its title bar ----
   A dashboard this tall is mostly scrolling past the parts you are not
   using today. Each card gets a tab at the left of its title, and what is
   folded is remembered per browser — the point is a layout that stays the
   way you left it, which it wouldn't if every reload reopened everything.
   The cards keep running while folded: the log still collects, the poll
   still polls. Only the view is put away. */
/* The title's own span, not the whole h2 — that would sweep in the tab's
   arrow and every control in the bar, and a title split across lines came
   out empty. Falls back to position, so a card with no title still gets a
   key of its own rather than sharing one. */
function foldKey(card) {
  const span = card.querySelector("h2 span");
  const title = (span?.textContent || card.id || "").trim();
  const slug = title.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
  return "fold:" + (slug || "card" + [...document.querySelectorAll(".card")].indexOf(card));
}

function setFolded(card, folded, remember = true) {
  card.classList.toggle("folded", folded);
  const btn = card.querySelector(".card-fold");
  if (btn) {
    btn.textContent = folded ? "▸" : "▾";
    btn.title = folded ? "Expand" : "Collapse";
    btn.setAttribute("aria-expanded", folded ? "false" : "true");
  }
  if (remember) {
    try { prefSet(foldKey(card), folded ? "1" : "0"); } catch {}
  }
}

function initCardFolding() {
  for (const card of document.querySelectorAll(".card")) {
    const h2 = card.querySelector("h2");
    if (!h2 || h2.querySelector(".card-fold")) continue;

    // a plain (non-flex) title bar has to become one, or the tab and the
    // text stack instead of sitting side by side
    if (getComputedStyle(h2).display !== "flex") {
      h2.style.display = "flex";
      h2.style.alignItems = "center";
      h2.style.gap = "8px";
    }

    const btn = document.createElement("button");
    btn.className = "card-fold";
    btn.type = "button";
    btn.onclick = () => setFolded(card, !card.classList.contains("folded"));
    h2.insertBefore(btn, h2.firstChild);

    let saved = null;
    try { saved = prefGet(foldKey(card)); } catch {}
    setFolded(card, saved === "1", false);

    makeCardDraggable(card);
  }
}

/* ---- dragging a card to a new place in the column ----
   The fold tab doubles as the handle: one control, two gestures. The card
   is only marked draggable while the pointer is on that tab, so selecting
   text in a card doesn't turn into a drag. */
const ORDER_KEY = "j0kers-card-order";
let draggingCard = null;

/* Anything that would rather have the mouse than move the card: controls,
   clickable rows, media tiles, and the places you scroll or select text.
   Everything else — the title bar, the card's own background, plain
   headings and labels — is fair game as a handle. */
const NO_DRAG_FROM = "button, a, input, select, textarea, label, video, audio, img," +
                     " [data-act], [contenteditable], table, pre, code, #log, .tiles," +
                     // The stream list is text you want to get out of it — the
                     // stream id the URLs are built from, the playlist address in
                     // Info view. Dragging across it to select was arming the card
                     // instead, so the selection never happened and the card slid
                     // away under the pointer. Rows keep their own reordering:
                     // that is armed by the ⠿ grip, which is not affected by this.
                     " #hls";

function canDragFrom(target) {
  if (!(target instanceof Element)) return true;
  if (target.closest(".card-fold")) return true;      // the tab is a handle, not a control
  if (target.closest(NO_DRAG_FROM)) return false;
  // mid-selection: finish the selection rather than snatching the card away
  const sel = window.getSelection?.();
  return !sel || sel.isCollapsed;
}

function makeCardDraggable(card) {
  // The whole card is the handle, minus the parts that need the mouse
  // themselves. Set on mousedown rather than left on: a permanently
  // draggable card cannot have text selected inside it at all.
  card.addEventListener("mousedown", e => { card.draggable = canDragFrom(e.target); });
  card.addEventListener("mouseup", () => { card.draggable = false; });

  card.addEventListener("dragstart", e => {
    draggingCard = card;
    card.classList.add("dragging");
    if (e.dataTransfer) {
      e.dataTransfer.effectAllowed = "move";
      // Firefox won't start a drag without payload
      try { e.dataTransfer.setData("text/plain", foldKey(card)); } catch {}
    }
  });

  card.addEventListener("dragend", () => {
    card.classList.remove("dragging");
    card.draggable = false;
    draggingCard = null;
    clearDropMarks();
    saveCardOrder();
  });

  card.addEventListener("dragover", e => {
    if (!draggingCard || draggingCard === card) return;
    e.preventDefault();
    // drawing where it would land matters more than the cursor shape, so
    // the effect hint must never be able to skip it
    if (e.dataTransfer) e.dataTransfer.dropEffect = "move";
    const box = card.getBoundingClientRect();
    const above = e.clientY < box.top + box.height / 2;
    clearDropMarks();
    card.classList.add(above ? "drop-above" : "drop-below");
  });

  card.addEventListener("dragleave", () => card.classList.remove("drop-above", "drop-below"));

  card.addEventListener("drop", e => {
    if (!draggingCard || draggingCard === card) return;
    e.preventDefault();
    const box = card.getBoundingClientRect();
    const above = e.clientY < box.top + box.height / 2;
    card.parentNode.insertBefore(draggingCard, above ? card : card.nextSibling);
    clearDropMarks();
    saveCardOrder();
  });
}

function clearDropMarks() {
  for (const c of document.querySelectorAll(".card"))
    c.classList.remove("drop-above", "drop-below");
}

function saveCardOrder() {
  try {
    prefSet(ORDER_KEY,
      JSON.stringify([...document.querySelectorAll(".card")].map(foldKey)));
  } catch {}
}

/* Applied before the tabs are added, so a reload comes up in the order it
   was left. A card missing from the saved list — one added by a later
   version — keeps its place at the end rather than disappearing. */
function applyCardOrder() {
  let order;
  try { order = JSON.parse(prefGet(ORDER_KEY) || "null"); } catch { return; }
  if (!Array.isArray(order) || !order.length) return;

  const cards = [...document.querySelectorAll(".card")];
  if (!cards.length) return;
  const parent = cards[0].parentNode;
  const anchor = cards[cards.length - 1].nextSibling;  // put them all back before this

  const byKey = new Map(cards.map(c => [foldKey(c), c]));
  for (const key of order) {
    const card = byKey.get(key);
    if (card) { parent.insertBefore(card, anchor); byKey.delete(key); }
  }
  for (const card of byKey.values()) parent.insertBefore(card, anchor);
}

applyCardOrder();
initCardFolding();

refreshAuth().then(async ok => {
  if (!ok) return;
  openLiveLink();              // from here on, closing this page closes the server
  await refreshMediaToken();   // before the first render, so no media URL goes out unsigned
  tick();
});
setInterval(tick, POLL_MS);
