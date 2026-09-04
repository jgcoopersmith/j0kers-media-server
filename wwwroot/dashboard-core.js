/* The ground floor of the dashboard's JavaScript. Everything the other
   dashboard-*.js files lean on lives here: the signed media URLs, the fetch
   helpers that carry the credential, the small formatting and escaping
   helpers, the header clock, the theme, and the sign-in gate.

   The page's script used to be one block of nearly five thousand lines
   inside dashboard.html. Cutting it into these files is a file split and
   nothing more: every function is still a plain global, because the markup
   calls them by name from inline onclick handlers and the files call each
   other freely. Turning them into modules, or wrapping them in an IIFE,
   would silently break both.

   Load order is therefore load-bearing. The browser runs these scripts in
   the order dashboard.html lists them, so anything read while a later file
   has not loaded yet - the top-level let and const values above all - has to
   be declared in a file that comes earlier. This one comes first for exactly
   that reason. */
"use strict";
const POLL_MS = 2000;
// previous reading of the server's cumulative byte counter, for the rate
let lastBytes = null, lastTime = null;
let hlsPort = null, rtspPort = null;
/* Signed-URL token for the media port. Players can't send a cookie or a
   header, so every HLS URL this page builds carries one instead. Refreshed
   well before it expires so a long film never stalls on a dead link. */
let mediaToken = "";
async function refreshMediaToken() {
  try {
    const t = await api("/api/media/token");
    mediaToken = t.token || "";
    // re-mint at the two-thirds mark rather than waiting for expiry
    const ms = Math.max(60000, (new Date(t.expiresUtc) - Date.now()) * 2 / 3);
    setTimeout(refreshMediaToken, Math.min(ms, 2147483647));
  } catch { mediaToken = ""; }
}
/* Every media URL goes through here, so there is exactly one place the
   token can be forgotten. */
/* The page's own scheme, not a hard-coded one: with TLS on, the media port
   is https too, and a https dashboard fetching http video is mixed content —
   which the browser blocks outright rather than warning about. */
function mediaScheme() { return location.protocol === "https:" ? "https://" : "http://"; }

function mediaUrlOn(host, pathAndQuery) {
  const base = mediaScheme() + host + ":" + hlsPort + pathAndQuery;
  if (!mediaToken) return base;
  return base + (base.includes("?") ? "&" : "?") + mediaToken;
}
function mediaUrl(pathAndQuery) { return mediaUrlOn(location.hostname, pathAndQuery); }

/// Hosts that only ever mean "this machine". A link built from one is fine
/// for the page that built it and useless to anything else.
const LOOPBACK = new Set(["localhost", "127.0.0.1", "[::1]", "::1"]);

/**
 * A URL meant to leave this browser — copied to a clipboard, typed into a
 * phone. Browsing the dashboard at localhost makes every link this page
 * builds say localhost, and pasted into VLC on a phone that resolves to the
 * phone itself. So a copied link falls back to an address the server
 * actually answers on: the default route where one is known.
 *
 * That fallback is a guess, and on a machine with more than one network it
 * is often the wrong one — the default route is where this PC reaches the
 * internet, not necessarily the network a phone is on. Nothing here can
 * know the other device's network, so where there is a choice the page
 * shows it (see interfaceLinks) rather than deciding silently, and
 * shareHost() names whatever this picked so the button isn't a mystery.
 */
function shareHost() {
  if (!LOOPBACK.has(location.hostname)) return location.hostname;
  const routable = hlsAddresses.find(a => a.primary) || hlsAddresses[0];
  return routable ? routable.address : location.hostname;
}

function shareUrl(pathAndQuery) { return mediaUrlOn(shareHost(), pathAndQuery); }

/** "(10.0.0.191)", plus a nudge to the per-network buttons when there are more. */
function shareHostNote() {
  const host = shareHost();
  return hlsAddresses.length > 1
    ? " — uses " + host + ". On another network, use the 🎬 Link button beside that address instead."
    : " — uses " + host + ".";
}

/* Addresses the media port answers on, from /api/status. More than one
   means the server is bound to every interface and has several connected
   networks — a link built from the host this page is open on is then only
   right for whoever is already on that network. */
let hlsAddresses = [];
/* The bearer credential: a device key from "remember this device", or a
   legacy control.authToken. Keys live in localStorage so a phone stays
   signed in across restarts; passwords are never stored anywhere — they
   are exchanged for an HttpOnly session cookie the page cannot read. */
let token = localStorage.getItem("j0kers-key") || sessionStorage.getItem("j0kers-token") || "";
let me = null;              // the signed-in account, from /api/auth/state
let authState = null;       // {authRequired, setupRequired, secure}

// bootstrap from a ?token=/?key=… link, then scrub it from the URL so it
// doesn't linger in history, bookmarks, or a Referer header
(function () {
  const m = location.search.match(/[?&](?:token|key)=([^&]+)/);
  if (m) {
    token = decodeURIComponent(m[1]);
    localStorage.setItem("j0kers-key", token);
    history.replaceState(null, "", location.pathname);
  }
})();


/* ---- preferences belong to the account, not to the browser ----

   Every option this page remembers - the theme, which view each card is in,
   the card order, what is folded, the last transcode folder, shuffle and loop,
   playback speed and subtitle language - lived in localStorage under a bare
   key. localStorage is per browser, so on a machine where more than one
   account signs in they were one shared set: a guest picking the light theme
   changed it for the owner, and the owner's card layout arrived for the guest.

   So every key is suffixed with the account it belongs to. The token is
   deliberately left out of this - it is a credential, not a preference, and
   sign-out already removes it.

   The account is not known at first paint. The theme is applied by an inline
   script in <head> to avoid a flash of the wrong one, and the card order is
   applied before refreshAuth has answered. So the name is also kept under a
   plain key, as the answer to "who was here last" - which is right on every
   load but the first after a switch, and that one is corrected the moment the
   server says who this is. */
const PREF_USER_KEY = "j0kers-prefs-for";
let prefUser = "";
try { prefUser = localStorage.getItem(PREF_USER_KEY) || ""; } catch { /* private mode */ }

function prefKey(key) { return prefUser ? key + "@" + prefUser : key; }
function prefGet(key) { try { return localStorage.getItem(prefKey(key)); } catch { return null; } }
function prefSet(key, value) { try { localStorage.setItem(prefKey(key), value); } catch { /* private mode */ } }
function prefRemove(key) { try { localStorage.removeItem(prefKey(key)); } catch { /* private mode */ } }

/* The keys that existed before any of this, so a first sign-in keeps the
   layout and theme somebody already has rather than starting them over. */
/* Read off the code rather than remembered: the first version of this list
   guessed "j0kers-cards" for the card order, which is really
   "j0kers-card-order", and guessed a "j0kers-fold-" prefix for the folded
   state, which is really "fold:". Neither matched anything, so neither was
   ever adopted. */
const PREF_KEYS = [
  "j0kers-theme",         // dashboard-core.js  THEME_KEY
  "j0kers-card-order",    // dashboard-ui.js    ORDER_KEY
  "j0kers-library",       // dashboard-library.js
  "j0kers-shuffle",       // dashboard-library.js
  "j0kers-loop",          // dashboard-library.js
  "j0kers-speed",         // dashboard-player.js
  "j0kers-res",           // dashboard-player.js
  "j0kers-sub-lang",      // dashboard-player.js
  "j0kers-tc-folder",     // dashboard-transcode.js  TC_FOLDER_KEY
  "j0kers-tc-sort",       // dashboard-transcode.js  TC_SORT_KEY
  "j0kers-tc-conv-order", // dashboard-transcode.js  TC_ORDER_KEY
  "j0kers-hls-order",     // dashboard-streams.js    REORDER.hls.key
  "tunerHost",            // dashboard-channels.js
];

/* The two families with a key per item rather than a fixed name: one view
   mode per card, and one folded flag per card. */
const PREF_PREFIXES = ["j0kers-view-", "fold:"];

/* Called once the server has said who this is. Adopts whatever was already
   stored bare, then re-applies the things that were painted before the answer
   arrived. */
function usePreferencesFor(username) {
  const who = username || "";
  if (who === prefUser) return;
  prefUser = who;
  try { localStorage.setItem(PREF_USER_KEY, who); } catch { /* private mode */ }
  if (who) adoptBarePreferences();
  reapplyPreferences();
}

/* A one-time move of the old shared keys into this account's own, and only
   where the account has nothing of its own yet - so it can never overwrite a
   preference somebody has already set. */
function adoptBarePreferences() {
  let keys = [];
  try { keys = Object.keys(localStorage); } catch { return; }
  for (const key of keys) {
    // bare keys only: anything already carrying "@" belongs to someone
    if (key.includes("@")) continue;
    const isPref = PREF_KEYS.includes(key) || PREF_PREFIXES.some(p => key.startsWith(p));
    if (!isPref) continue;
    try {
      // Raw on both sides on purpose: `mine` is already the suffixed
      // key, and `key` is the old shared one that has no suffix. Going
      // through prefGet/prefSet here would suffix them a second time.
      const mine = prefKey(key);
      if (localStorage.getItem(mine) === null)
        localStorage.setItem(mine, localStorage.getItem(key));
    } catch { /* private mode */ }
  }
}

/* The theme and the card layout are put on the page before anybody knows whose
   they should be. If the answer turns out to be a different account, they are
   put right here rather than waiting for a reload. */
function reapplyPreferences() {
  try {
    // Always set, never only-when-found. Acting only on a saved value left
    // the previous account's theme on screen for an account that had none of
    // its own - which is not "no preference", it is somebody else's. The
    // fallback is the same one the inline <head> script uses.
    const t = prefGet("j0kers-theme");
    document.documentElement.setAttribute("data-theme",
      THEMES.includes(t) ? t
        : matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark");
    paintThemeButton();
  } catch { /* private mode */ }
  try { if (typeof applyCardOrder === "function") applyCardOrder(); } catch { }
  try { if (typeof restoreCardViews === "function") restoreCardViews(); } catch { }
  // Folded state is put back too. It is applied while the fold buttons are
  // built, which is long before anyone knows whose it should be.
  try { if (typeof applySavedFolding === "function") applySavedFolding(); } catch { }
}

const $ = id => document.getElementById(id);
/* join a browse path and entry name with the OS's separator (the server
   returns native paths — backslashes on Windows, slashes on mac/Linux) */
function joinPath(base, name) {
  if (base.endsWith("\\") || base.endsWith("/")) return base + name;
  return base + (base.includes("\\") ? "\\" : "/") + name;
}
const esc = s => String(s).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));

/* X-J0kers-CSRF is what tells the server this request came from our own
   JavaScript: a hostile page can't set a custom header on a cross-origin
   request without a preflight the browser will refuse, so the session
   cookie alone can never be ridden from somewhere else. */
function headers() {
  const h = { "X-J0kers-CSRF": "1" };
  if (token) h["Authorization"] = "Bearer " + token;
  return h;
}

/* Every read goes through here, and every read gets a deadline.

   Without one, a request the server accepts and then never answers leaves
   this promise pending for ever — and a pending promise is silent. The whole
   dashboard sat at "connecting…" with every tile on a dash, no error in the
   console and nothing in the log, because the first poll never came back and
   never failed either. It looked exactly like a server that was not there,
   on a server that was running fine.

   Fifteen seconds is deliberately far longer than any answer should take;
   this is here to turn "for ever" into "an error you can read", not to be a
   tight budget. */
const API_TIMEOUT_MS = 15000;

async function api(path) {
  const stop = new AbortController();
  const timer = setTimeout(() => stop.abort(), API_TIMEOUT_MS);
  let r;
  try {
    r = await fetch(path, { headers: headers(), signal: stop.signal });
  } catch (e) {
    if (e.name === "AbortError")
      throw new Error(path + " → no answer in " + (API_TIMEOUT_MS / 1000) + "s");
    throw e;
  } finally {
    clearTimeout(timer);
  }
  if (r.status === 401) { showLogin(); throw new Error("unauthorized"); }
  if (!r.ok) throw new Error(path + " → " + r.status);
  return r.json();
}

/* POST/PUT/DELETE with a JSON body; returns [ok, data]. */
async function send(method, path, body) {
  const opts = { method, headers: headers() };
  if (body !== undefined) {
    opts.headers = { "Content-Type": "application/json", ...opts.headers };
    opts.body = JSON.stringify(body);
  }
  const r = await fetch(path, opts);
  let data = {};
  try { data = await r.json(); } catch {}
  if (r.status === 401) showLogin();
  return [r.ok, data];
}

/* ---- accounts: sign-in gate, session state, role-driven UI ---- */

function closeOverlay(id) { $(id).style.display = "none"; }

/* ---- password fields: 👁 to check what you typed ----
   Applied by walking the DOM rather than by hand-writing a button next to
   each field, so the ones built at runtime (the per-user password box in
   the Users dialog) get one too, and so does anything added later. */
function decoratePasswords(root) {
  for (const input of (root || document).querySelectorAll('input[type="password"]:not([data-eye])')) {
    input.dataset.eye = "1";
    const wrap = document.createElement("span");
    wrap.className = "pw";
    // the wrapper becomes the flex item, so it has to inherit the sizing
    // the input was carrying or the row it sits in collapses
    for (const prop of ["flex", "minWidth", "marginTop"]) {
      if (input.style[prop]) { wrap.style[prop] = input.style[prop]; input.style[prop] = ""; }
    }
    input.parentNode.insertBefore(wrap, input);
    wrap.appendChild(input);

    const eye = document.createElement("button");
    eye.type = "button";
    eye.className = "pw-eye";
    eye.textContent = "👁";
    eye.title = "Show password";
    eye.setAttribute("aria-label", "Show password");
    wrap.appendChild(eye);
  }
}

document.addEventListener("click", e => {
  const eye = e.target.closest(".pw-eye");
  if (!eye) return;
  const input = eye.parentNode.querySelector("input");
  if (!input) return;
  const show = input.type === "password";
  input.type = show ? "text" : "password";
  eye.textContent = show ? "🙈" : "👁";
  eye.classList.toggle("on", show);
  eye.title = show ? "Hide password" : "Show password";
  eye.setAttribute("aria-label", eye.title);
  input.focus();
});

/* Back to dots — so a revealed password isn't still on screen the next
   time the dialog is opened. */
function hidePasswords(root) {
  for (const eye of (root || document).querySelectorAll(".pw-eye")) {
    const input = eye.parentNode.querySelector("input");
    if (input) input.type = "password";
    eye.textContent = "👁";
    eye.classList.remove("on");
    eye.title = "Show password";
    eye.setAttribute("aria-label", eye.title);
  }
}

/* ---- the clock in the header ----
   The server's clock, not this browser's — a dashboard open on a phone in
   another timezone should still say what time it is on the machine holding
   the media, which is the time its logs and schedules are in.

   The status poll carries the server's instant, its UTC offset, and what it
   calls its zone. Between polls the seconds run locally against the
   difference between the two clocks, so this costs no extra requests and
   still cannot drift. Tabular numerals keep the pill from twitching. */
let clockSkewMs = null;        // server time − this browser's time
let clockOffsetMin = 0, clockZone = "", clockZoneFull = "";

function noteServerClock(status) {
  if (!status || !status.timeUtc) return;
  const server = Date.parse(status.timeUtc);
  if (Number.isNaN(server)) return;
  clockSkewMs = server - Date.now();
  clockOffsetMin = status.utcOffsetMinutes || 0;
  clockZone = status.timeZone || "";
  clockZoneFull = status.timeZoneFull || "";
  tickClock();
}

function tickClock() {
  const pill = $("clockpill");
  if (!pill) return;
  // Two spans rather than one string, so the date and the time can stack
  // without building markup at runtime — textContent on each keeps whatever
  // the locale produces out of the HTML parser.
  const dEl = $("clock-date"), tEl = $("clock-time");
  if (!dEl || !tEl) return;
  if (clockSkewMs === null) { dEl.textContent = "—"; tEl.textContent = ""; return; }

  // shift the instant by the server's offset and then read it as UTC: that
  // prints the server's wall clock whatever zone this browser is in
  const shifted = new Date(Date.now() + clockSkewMs + clockOffsetMin * 60000);
  const time = shifted.toLocaleTimeString([], {
    hour: "2-digit", minute: "2-digit", second: "2-digit", timeZone: "UTC" });
  const date = shifted.toLocaleDateString([], {
    weekday: "short", day: "numeric", month: "short", timeZone: "UTC" });
  dEl.textContent = date;
  tEl.textContent = time + (clockZone ? " " + clockZone : "");
  pill.title = (clockZoneFull || clockZone || "server time") + " — the server's clock";
}
tickClock();
setInterval(tickClock, 1000);

/* ---- theme: follows the system until you pick, then stays picked ---- */
const THEME_KEY = "j0kers-theme";
const THEMES = ["dark", "light", "cloud", "forest", "donut", "assassin", "royal"];

function currentTheme() {
  const t = document.documentElement.getAttribute("data-theme");
  return THEMES.includes(t) ? t : "dark";
}

/* The icon shows what you'd get, not what you're in — a sun on a dark page
   reads as "go light" rather than as a status light. With three themes the
   button is a cycle, so it names the next one as well. */
const THEME_NEXT = {
  dark:   { icon: "☀",  label: "Switch to the light theme" },
  light:  { icon: "☁",  label: "Switch to the cloud theme" },
  cloud:  { icon: "🍃", label: "Switch to the forest theme" },
  forest: { icon: "🍩", label: "Switch to the donut sprinkles theme" },
  donut:  { icon: "🗡", label: "Switch to the assassin theme" },
  assassin: { icon: "👑", label: "Switch to the royal theme" },
  royal:  { icon: "🌙", label: "Switch to the dark theme" },
};

function paintThemeButton() {
  const next = THEME_NEXT[currentTheme()];
  $("themebtn").textContent = next.icon;
  $("themebtn").title = next.label;
  $("themebtn").setAttribute("aria-label", next.label);
}

function toggleTheme() {
  const next = THEMES[(THEMES.indexOf(currentTheme()) + 1) % THEMES.length];
  document.documentElement.setAttribute("data-theme", next);
  try { prefSet(THEME_KEY, next); } catch { /* private mode */ }
  paintThemeButton();
}
paintThemeButton();

/* Who are we, and does this server have accounts at all? Drives both the
   sign-in gate and which controls the page is allowed to show. */
async function refreshAuth() {
  try {
    authState = await (await fetch("/api/auth/state", { headers: headers() })).json();
  } catch { return false; }
  me = authState.user || null;
  const role = (me && me.role) || "";
  document.body.classList.toggle("is-server-admin", role === "serveradmin");
  document.body.classList.toggle("is-admin", role === "admin" || role === "serveradmin");
  document.body.classList.toggle("can-edit", role === "admin" || role === "edit" || role === "serveradmin");
  // Preferences follow the account, not the browser. Done here because
  // this is the first moment anybody knows whose they are - the theme and
  // the card order have already been painted from whoever was here last.
  usePreferencesFor(me && me.username);
  $("acct-pill").textContent = me ? (me.username || "local") : "sign in";
  $("acctbtn").style.display = authState.authRequired ? "" : "none";
  if (!authState.authenticated) { showLogin(); return false; }
  return true;
}

/* The sign-in form is its own page, served at / to anyone who hasn't
   signed in — so a session that expires mid-visit goes back there rather
   than growing a second login form inside the dashboard. */
let leaving = false;
function showLogin() {
  if (leaving) return;
  leaving = true;
  location.replace("/");
}

async function signOut() {
  await send("POST", "/api/auth/logout");
  token = "";
  localStorage.removeItem("j0kers-key");
  sessionStorage.removeItem("j0kers-token");
  location.reload();
}

