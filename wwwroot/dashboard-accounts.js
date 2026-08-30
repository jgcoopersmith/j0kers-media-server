/* Your own account and, for an administrator, everyone else's: the account
   panel with its password change and personal keys, and the user list that
   creates, edits and removes accounts. Split out of dashboard.html; see
   dashboard-core.js for why every function here stays global. */
"use strict";
/* ---- account panel: own password and own keys ---- */

async function openAccount() {
  if (!await refreshAuth()) return;
  $("acct-msg").textContent = "";
  $("acct-secret").innerHTML = "";
  $("acct-cur").value = $("acct-new").value = "";
  hidePasswords($("acct-overlay"));
  $("acct-name").textContent = me ? (me.displayName || me.username) : "Account";
  $("acct-meta").textContent = me
    ? me.role + (me.lastLoginUtc ? " · last signed in " + new Date(me.lastLoginUtc).toLocaleString() : "")
    : "";
  renderKeys($("acct-keys"), (me && me.keys) || [], null);
  $("acct-overlay").style.display = "flex";
}

function renderKeys(box, keys, userId) {
  if (!keys.length) { box.innerHTML = '<div class="meta" style="color:var(--muted);font-size:11.5px">No keys.</div>'; return; }
  box.innerHTML = keys.map(k =>
    '<div class="keyrow"><span>🔑 ' + esc(k.label) + '</span>'
    + '<span class="meta">' + (k.expired ? "expired" : k.lastUsedUtc
        ? "last used " + new Date(k.lastUsedUtc).toLocaleDateString()
        : "never used") + '</span>'
    + '<button data-act="key-revoke" data-arg="' + esc(k.id) + '"'
    + (userId ? ' data-user="' + esc(userId) + '"' : '') + '>Revoke</button></div>').join("");
}

async function mintOwnKey() {
  const [ok, data] = await send("POST", "/api/auth/keys", { label: $("acct-keylabel").value.trim() });
  const msg = $("acct-msg");
  if (!ok) { msg.className = "msg"; msg.textContent = data.error || "could not create the key"; return; }
  $("acct-keylabel").value = "";
  showSecret($("acct-secret"), data.key);
  await refreshAuth();
  renderKeys($("acct-keys"), (me && me.keys) || [], null);
}

/* A key is shown exactly once — only its digest is kept server-side. */
function showSecret(box, key) {
  box.innerHTML = '<div class="secret">' + esc(key) + '</div>'
    + '<div class="hint">Copy this now — it can\'t be shown again.</div>'
    + '<div style="display:flex;justify-content:flex-end;margin-top:6px">'
    + '<button data-act="copy" data-arg="' + esc(key) + '">Copy</button></div>';
}

async function changePassword() {
  const msg = $("acct-msg");
  msg.className = "msg";
  const [ok, data] = await send("POST", "/api/auth/password", {
    currentPassword: $("acct-cur").value,
    newPassword: $("acct-new").value,
  });
  $("acct-cur").value = $("acct-new").value = "";
  if (!ok) { msg.textContent = data.error || "could not change the password"; return; }
  msg.className = "msg ok";
  msg.textContent = "Password updated. Other sessions for this account were signed out.";
}

/* ---- user administration ---- */

async function openUsers() {
  $("users-msg").textContent = "";
  hidePasswords($("users-overlay"));
  $("users-overlay").style.display = "flex";
  await loadUsers();
}

let lastUsers = [];   // the last list fetched, so an action can name its subject

async function loadUsers() {
  let data;
  try { data = await api("/api/users"); }
  catch (e) { $("users-list").innerHTML = '<div class="empty">' + esc(e.message) + "</div>"; return; }
  lastUsers = data.users || [];
  $("users-list").innerHTML = data.users.map(u => {
    const badges = '<span class="badge ' + esc(u.role) + '">' + esc(u.roleLabel || u.role) + "</span>"
      + (u.enabled ? "" : '<span class="badge off">disabled</span>')
      + (u.passwordless ? '<span class="badge">passwordless</span>'
                        : u.hasPassword ? "" : '<span class="badge">key only</span>')
      + (u.self ? '<span class="badge">you</span>' : "");
    return '<div class="usr">'
      + '<div class="top"><span class="nm">' + esc(u.displayName || u.username) + "</span>" + badges
      + '<span style="flex:1"></span>'
      + '<button data-act="usr-keys" data-arg="' + esc(u.id) + '" title="' + u.keys.length + ' key' + (u.keys.length === 1 ? '' : 's') + '">🔑</button>'
      + (u.sessions > 0
          ? '<button data-act="usr-signout" data-arg="' + esc(u.id) + '" title="End every session this account has open. It can sign in again; anything it is watching stops.">Sign out</button>'
          : "")
      + '<button data-act="usr-edit" data-arg="' + esc(u.id) + '">Edit</button>'
      + (u.self ? "" : '<button class="danger" data-act="usr-remove" data-arg="' + esc(u.id) + '">Remove</button>')
      + "</div>"
      + '<div class="meta">' + esc(u.username)
      + (u.sessions ? " · " + u.sessions + " active session" + (u.sessions === 1 ? "" : "s") : "")
      + (u.lastLoginUtc ? " · last signed in " + new Date(u.lastLoginUtc).toLocaleString() : " · never signed in")
      + "</div>"
      + '<div class="usr-panel" id="up-' + esc(u.id) + '" style="display:none;margin-top:8px"></div>'
      + "</div>";
  }).join("") || '<div class="empty">No accounts yet.</div>';
}

/* Passwordless is read-only, so its password and role controls have nothing
   to say — grey them out (and pin the role to read) whenever it is ticked.
   Works for both the add row (prefix "nu") and an edit panel (prefix "ue"). */
function syncGuest(prefix, id) {
  const s = id ? "-" + id : "";
  const cb = $(prefix + "-guest" + s);
  if (!cb) return;
  const on = cb.checked;
  const pass = $(prefix + "-pass" + s), role = $(prefix + "-role" + s);
  if (pass) {
    if (pass.dataset.ph === undefined) pass.dataset.ph = pass.placeholder;
    pass.disabled = on;
    if (on) pass.value = "";
    pass.placeholder = on ? "no password — signs in by username" : pass.dataset.ph;
  }
  if (role) { role.disabled = on; if (on) role.value = "read"; }
}

async function createUser() {
  const msg = $("users-msg");
  msg.className = "msg";
  const [ok, data] = await send("POST", "/api/users", {
    username: $("nu-name").value.trim(),
    password: $("nu-pass").value,
    role: $("nu-role").value,
    passwordless: $("nu-guest").checked,
  });
  if (!ok) { msg.textContent = data.error || "could not create the account"; return; }
  $("nu-name").value = $("nu-pass").value = "";
  $("nu-guest").checked = false; syncGuest("nu");
  msg.className = "msg ok";
  msg.textContent = "Account created.";
  loadUsers();
}

function userPanel(id) { return $("up-" + id); }

/* Ends every session one account has open.
   Confirmed, because it is felt at the other end: somebody watching on a TV
   is signed out mid-film, and the person clicking is usually not the person
   holding the remote. Signing yourself out is allowed and lands you back at
   the sign-in page, which is the honest consequence rather than a special
   case worth coding around. */
async function signOutUser(id) {
  const u = (lastUsers || []).find(x => x.id === id);
  const who = u ? (u.displayName || u.username) : "this account";
  const n = u ? u.sessions : 0;
  if (!confirm("Sign " + who + " out of " + n + " session" + (n === 1 ? "" : "s") + "?\n\n"
             + "They can sign in again. Anything they are watching right now stops.")) return;
  const [ok, d] = await send("POST", "/api/users/signout?id=" + encodeURIComponent(id));
  const msg = $("users-msg");
  if (msg) msg.textContent = ok
    ? "signed " + who + " out of " + d.signedOut + " session" + (d.signedOut === 1 ? "" : "s")
    : (d.error || "could not sign that account out");
  loadUsers();
}

async function toggleUserEdit(id) {
  const box = userPanel(id);
  if (!box) return;
  if (box.dataset.mode === "edit" && box.style.display !== "none") {
    box.style.display = "none"; box.dataset.mode = ""; return;
  }
  const data = await api("/api/users");
  const u = data.users.find(x => x.id === id);
  if (!u) return;
  box.dataset.mode = "edit";
  box.style.display = "";
  box.innerHTML =
    '<div style="display:flex;gap:8px;flex-wrap:wrap;align-items:center">'
    + '<input class="am-in" id="ue-name-' + id + '" value="' + esc(u.username) + '" style="width:140px">'
    + '<input class="am-in" id="ue-disp-' + id + '" value="' + esc(u.displayName) + '" placeholder="display name" style="flex:1;min-width:120px">'
    + '<select class="am-in" id="ue-role-' + id + '">'
    + [["read", "read"], ["edit", "edit"], ["admin", "admin"], ["serveradmin", "Server Admin"]].map(([r, label]) =>
        '<option value="' + r + '"' + (u.role === r ? " selected" : "") + ">" + label + "</option>").join("")
    + "</select>"
    + '<label class="chk" style="margin:0"><input type="checkbox" id="ue-on-' + id + '"' + (u.enabled ? " checked" : "") + "><span>enabled</span></label>"
    + '<label class="chk" style="margin:0"><input type="checkbox" id="ue-guest-' + id + '"' + (u.passwordless ? " checked" : "") + ' onchange="syncGuest(\'ue\',\'' + id + '\')"><span>passwordless (read-only)</span></label>'
    + "</div>"
    + '<div style="display:flex;gap:8px;margin-top:8px">'
    + '<input class="am-in" type="password" id="ue-pass-' + id + '" placeholder="set a new password (optional)" style="flex:1" autocomplete="new-password">'
    + '<button class="go" data-act="usr-save" data-arg="' + id + '">Save</button></div>'
    + '<div class="msg" id="ue-msg-' + id + '"></div>';
  decoratePasswords(box);   // the box was just built, so its field has no eye yet
  syncGuest("ue", id);      // grey out password/role if it is already passwordless
}

async function saveUser(id) {
  const msg = $("ue-msg-" + id);
  msg.className = "msg";
  const body = {
    username: $("ue-name-" + id).value.trim(),
    displayName: $("ue-disp-" + id).value.trim(),
    role: $("ue-role-" + id).value,
    enabled: $("ue-on-" + id).checked,
    passwordless: $("ue-guest-" + id).checked,
  };
  const pass = $("ue-pass-" + id).value;
  if (pass && !body.passwordless) body.password = pass;
  const [ok, data] = await send("PUT", "/api/users?id=" + encodeURIComponent(id), body);
  if (!ok) { msg.textContent = data.error || "could not save"; return; }
  userPanel(id).style.display = "none";
  loadUsers();
  refreshAuth();
}

async function removeUser(id) {
  if (!confirm("Remove this account? Their keys and sessions stop working immediately.")) return;
  const [ok, data] = await send("DELETE", "/api/users?id=" + encodeURIComponent(id));
  const msg = $("users-msg");
  msg.className = "msg";
  if (!ok) { msg.textContent = data.error || "could not remove the account"; return; }
  loadUsers();
}

async function toggleUserKeys(id) {
  const box = userPanel(id);
  if (!box) return;
  if (box.dataset.mode === "keys" && box.style.display !== "none") {
    box.style.display = "none"; box.dataset.mode = ""; return;
  }
  const data = await api("/api/users");
  const u = data.users.find(x => x.id === id);
  if (!u) return;
  box.dataset.mode = "keys";
  box.style.display = "";
  box.innerHTML = '<div id="uk-list-' + id + '"></div>'
    + '<div style="display:flex;gap:8px;margin-top:6px">'
    + '<input class="am-in" id="uk-label-' + id + '" placeholder="what is this key for?" style="flex:1">'
    + '<button data-act="usr-key-new" data-arg="' + id + '">＋ New key</button></div>'
    + '<div id="uk-secret-' + id + '"></div>';
  renderKeys($("uk-list-" + id), u.keys, id);
}

async function mintUserKey(id) {
  const [ok, data] = await send("POST", "/api/users/keys?id=" + encodeURIComponent(id),
    { label: $("uk-label-" + id).value.trim() });
  if (!ok) { $("users-msg").textContent = data.error || "could not create the key"; return; }
  $("uk-label-" + id).value = "";
  showSecret($("uk-secret-" + id), data.key);
  const users = await api("/api/users");
  const u = users.users.find(x => x.id === id);
  if (u) renderKeys($("uk-list-" + id), u.keys, id);
}

async function revokeKey(keyId, userId) {
  if (!confirm("Revoke this key? Anything using it stops working immediately.")) return;
  const path = userId
    ? "/api/users/keys?id=" + encodeURIComponent(userId) + "&keyId=" + encodeURIComponent(keyId)
    : "/api/auth/keys?id=" + encodeURIComponent(keyId);
  await send("DELETE", path);
  if (userId) {
    await loadUsers();          // re-renders the row (and closes its panel)
    toggleUserKeys(userId);     // …so reopen the key list the user was in
  } else {
    await refreshAuth();
    renderKeys($("acct-keys"), (me && me.keys) || [], null);
  }
}

