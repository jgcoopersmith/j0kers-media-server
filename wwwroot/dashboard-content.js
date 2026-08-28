/* Changing what the server offers, from the page: the add-mount form, and
   removing or re-transcoding an HLS stream. Split out of dashboard.html;
   see dashboard-core.js for why every function here stays global. */
"use strict";
/* ---- add-mount flow (uses pickPath for the file source) ---- */
function toggleAddMount() {
  const f = $("addmount");
  const show = f.style.display === "none";
  f.style.display = show ? "block" : "none";
  $("am-toggle").textContent = show ? "− Close" : "+ Add mount";
  $("am-msg").textContent = "";
  if (show) { syncAddMount(); $("am-path").focus(); }
}

function syncAddMount() {
  const isFile = $("am-source").value === "file";
  $("am-filerow").style.display = isFile ? "flex" : "none";
  $("am-tonerow").style.display = isFile ? "none" : "flex";
}

async function browseMountFile() {
  const p = await pickPath({ mode: "file", title: "Pick an audio file (raw 8 kHz G.711 µ-law)" });
  if (!p) return;
  $("am-file").value = p;
  if (!$("am-path").value) {
    // suggest a mount path from the file name
    const base = p.split(/[\\/]/).pop().replace(/\.[^.]*$/, "").replace(/[^\w-]+/g, "-").toLowerCase();
    if (base) $("am-path").value = "/" + base;
  }
}

async function submitAddMount() {
  const msg = $("am-msg");
  msg.textContent = "";
  const body = {
    path: $("am-path").value.trim(),
    source: $("am-source").value,
    description: $("am-desc").value.trim(),
  };
  if (body.source === "file") {
    body.file = $("am-file").value;
    if (!body.file) { msg.textContent = "pick an audio file first"; return; }
  } else {
    body.toneFrequencyHz = parseFloat($("am-freq").value) || 440;
  }
  if (!body.path) { msg.textContent = "enter a mount path, e.g. /music"; return; }
  if (!body.path.startsWith("/")) body.path = "/" + body.path;

  try {
    const r = await fetch("/api/mounts", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers() },
      body: JSON.stringify(body),
    });
    const data = await r.json();
    if (!r.ok) { msg.textContent = data.error || ("failed: " + r.status); return; }
    toggleAddMount();
    $("am-path").value = ""; $("am-file").value = ""; $("am-desc").value = "";
    mountsLoaded = 0;
    refreshMounts();
  } catch (e) {
    msg.textContent = "request failed: " + e.message;
  }
}

async function removeMount(path) {
  if (!confirm("Remove mount " + path + "? (Mounts from server.json are hidden persistently but the config file itself is not modified.)")) return;
  await fetch("/api/mounts?path=" + encodeURIComponent(path), { method: "DELETE", headers: headers() });
  mountsLoaded = 0;
  refreshMounts();
}

async function removeHlsStream(name, at) {
  // Two outcomes, because they are genuinely different decisions: keeping
  // the conversion saves rebuilding it, and deleting it is the only thing
  // that gives the disk back. Keeping is offered first and is the safe one.
  //
  // Unless the answer has already been settled once and for all, from the
  // tick below or the Config dialog: then it is simply done, with no prompt.
  let choice = (cfgLoaded && cfgLoaded.streamRemoveAction) || streamRemoveAction || "ask";
  if (choice !== "keep" && choice !== "delete") {
    const remember = { label: "Always do this - don't ask again" };
    choice = await choiceAt(at, "Remove '" + name + "' from the list?"
        + "\n\nKeeping the conversion means playing this media again brings it back"
        + "\nwithout converting it a second time. Deleting frees the disk space.",
        [{ label: "Keep files", value: "keep" },
         { label: "Delete files", value: "purge", danger: true }],
        remember);
    if (!choice) return;
    // Ticked: save it as the server's setting, so it holds for this browser,
    // the next one, and shows in Config where it can be changed back.
    if (remember.checked) {
      const pick = choice === "purge" ? "delete" : "keep";
      streamRemoveAction = pick;
      if (cfgLoaded) cfgLoaded.streamRemoveAction = pick;
      send("POST", "/api/settings", { streamRemoveAction: pick })
        .catch(() => { /* the removal itself still goes ahead */ });
    }
  }
  const purge = (choice === "purge" || choice === "delete");
  const r = await fetch("/api/hls?stream=" + encodeURIComponent(name)
      + (purge ? "&purge=1" : ""), { method: "DELETE", headers: headers() });
  if (!r.ok) {
    const data = await r.json().catch(() => ({}));
    alert(data.error || "delete failed");
  }
  refreshHls();
}

/* Convert this media again from scratch.
   Unlinking keeps a conversion precisely because rebuilding it would
   produce the same bytes. This is the case where that is not true — the
   codec settings changed, or the conversion came out wrong — so the old
   one is thrown away and the job runs again. */
async function retranscodeStream(name, at) {
  if (!await confirmAt(at, "Convert '" + name + "' again from scratch?" +
      "\n\nThe existing conversion is deleted and rebuilt from the original" +
      "\nfile. It will be unavailable until the conversion finishes.", "Rebuild")) return;
  try {
    const r = await fetch("/api/hls/retranscode?stream=" + encodeURIComponent(name),
                          { method: "POST", headers: headers() });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { alert(d.error || ("retranscode failed: HTTP " + r.status)); return; }
  } catch (e) { alert("retranscode failed: " + e.message); return; }
  refreshHls();
}

/* Delete every stream.
   The confirmation names the count and lists what is about to go, because
   "delete all" on a card you have scrolled past is otherwise a decision
   made blind — and this one reaches the disk. Deletions run one at a time
   rather than in parallel: the server is removing directories, and a
   failure part-way should leave a comprehensible half rather than a race. */
async function removeAllHlsStreams(at) {
  const streams = (lastHls && lastHls.streams) ? lastHls.streams.map(s => s.name) : [];
  if (!streams.length) { alert("There are no HLS streams to delete."); return; }

  const shown = streams.slice(0, 12).join("\n  ");
  const more = streams.length > 12 ? "\n  …and " + (streams.length - 12) + " more" : "";
  const choice = await choiceAt(at, "Remove ALL " + streams.length + " HLS stream" + (streams.length > 1 ? "s" : "") + " from the list?\n\n  "
      + shown + more
      + "\n\nKeeping the conversions means playing any of these again brings it"
      + "\nstraight back. Deleting frees the disk space and cannot be undone.",
      [{ label: "Keep files", value: "keep" },
       { label: "Delete files", value: "purge", danger: true }]);
  if (!choice) return;
  const qs = choice === "purge" ? "&purge=1" : "";

  // Keep the server's own words. An earlier version discarded the response
  // body and reported a bare "0 of 8", when the server had said exactly why
  // each one was refused.
  const failed = [];
  for (const name of streams) {
    try {
      const r = await fetch("/api/hls?stream=" + encodeURIComponent(name) + qs, { method: "DELETE", headers: headers() });
      if (!r.ok) {
        const d = await r.json().catch(() => ({}));
        failed.push(name + " — " + (d.error || ("HTTP " + r.status)));
      }
    } catch (e) { failed.push(name + " — " + e.message); }
  }
  refreshHls();
  if (failed.length) alert("Removed " + (streams.length - failed.length) + " of " + streams.length
    + ".\n\nThese could not be removed:\n  " + failed.join("\n  "));
}

