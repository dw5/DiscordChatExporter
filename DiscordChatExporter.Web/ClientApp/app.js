"use strict";

// ---- tiny API client -----------------------------------------------------------------------

const api = {
  async req(method, path, body) {
    const opts = {
      method,
      headers: { "X-DCE-Request": "1" },
      credentials: "same-origin",
    };
    if (body !== undefined) {
      opts.headers["Content-Type"] = "application/json";
      opts.body = JSON.stringify(body);
    }
    const res = await fetch(path, opts);
    const text = await res.text();
    const data = text ? JSON.parse(text) : null;
    if (!res.ok) throw new Error((data && data.error) || `HTTP ${res.status}`);
    return data;
  },
  get: (p) => api.req("GET", p),
  post: (p, b) => api.req("POST", p, b),
  put: (p, b) => api.req("PUT", p, b),
  del: (p) => api.req("DELETE", p),
};

const el = (tag, props = {}, ...children) => {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === "class") e.className = v;
    else if (k === "html") e.innerHTML = v;
    else if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
    else if (v !== null && v !== undefined && v !== false) e.setAttribute(k, v === true ? "" : v);
  }
  for (const c of children.flat()) {
    if (c == null || c === false) continue;
    e.append(c.nodeType ? c : document.createTextNode(c));
  }
  return e;
};

const root = () => document.getElementById("app");
const clear = (n) => {
  while (n.firstChild) n.removeChild(n.firstChild);
};

let state = { session: null };

// ---- shell ---------------------------------------------------------------------------------

function appBar() {
  const bar = el("header", { class: "app-bar" }, el("img", { src: "/favicon.png" }), el("h1", {}, "DiscordChatExporter"));
  bar.append(el("div", { class: "spacer" }));
  if (state.session) {
    bar.append(
      el("span", { class: "muted" }, `${state.session.user.name} (${state.session.tokenKind})`),
      el("button", { class: "secondary", onclick: signOut }, "Sign out"),
    );
  }
  return bar;
}

function render(view) {
  const r = root();
  clear(r);
  r.append(appBar(), view);
}

async function signOut() {
  try {
    await api.del("/api/session");
  } catch {}
  state.session = null;
  showSignIn();
}

// ---- sign in -------------------------------------------------------------------------------

function showSignIn() {
  const token = el("input", { type: "password", placeholder: "User or bot token" });
  const err = el("div", { class: "banner danger hidden" });
  const btn = el("button", {}, "Connect");

  btn.addEventListener("click", async () => {
    err.classList.add("hidden");
    btn.disabled = true;
    try {
      state.session = await api.post("/api/session", { token: token.value });
      showDashboard();
    } catch (e) {
      err.textContent = e.message;
      err.classList.remove("hidden");
      btn.disabled = false;
    }
  });

  render(
    el(
      "div",
      { class: "card" },
      el("h2", {}, "Connect to Discord"),
      el("p", { class: "muted" }, "Your token stays in this app's memory only. It is never stored or sent anywhere except Discord."),
      el("div", { class: "banner warn" }, "Automating a user account is against Discord's ToS and can get it banned. For a server you administer, prefer a bot token with the Manage Messages permission."),
      err,
      el("label", {}, "Token"),
      token,
      el("div", { style: "margin-top:14px" }, btn),
    ),
  );
  token.focus();
}

// ---- dashboard: pick server ----------------------------------------------------------------

async function showDashboard() {
  const view = el("div", {});
  render(view);

  const search = el("input", { type: "search", placeholder: "Search servers…" });
  const listWrap = el("div", {});
  const guildCard = el("div", { class: "card" }, el("h2", {}, "Choose a server"), search, listWrap);
  const runsCard = el("div", { class: "card" }, el("h2", {}, "Previous exports"), el("p", { class: "muted" }, "Loading…"));
  view.append(guildCard, runsCard);

  let guilds = [];
  const draw = () => {
    const q = search.value.trim().toLowerCase();
    const shown = guilds.filter((g) => !q || g.name.toLowerCase().includes(q));
    clear(listWrap);
    if (!shown.length) {
      listWrap.append(el("p", { class: "muted" }, guilds.length ? "No servers match." : "No servers found for this account."));
      return;
    }
    for (const g of shown) {
      const icon = g.icon
        ? el("img", { src: g.icon, class: "guild-icon", loading: "lazy" })
        : el("div", { class: "guild-icon placeholder" }, (g.name[0] || "?").toUpperCase());
      listWrap.append(
        el(
          "div",
          { class: "list-item", onclick: () => showExportSetup(g) },
          icon,
          el("strong", {}, g.name),
          el("div", { class: "spacer" }),
          el("span", { class: "muted small" }, g.id),
        ),
      );
    }
  };
  search.addEventListener("input", draw);

  try {
    guilds = await api.get("/api/guilds");
    draw();
  } catch (e) {
    clear(listWrap);
    listWrap.append(el("div", { class: "banner danger" }, e.message));
  }

  loadRuns(runsCard);
}

async function loadRuns(card) {
  try {
    const runs = await api.get("/api/exports");
    clear(card);
    card.append(el("h2", {}, "Previous exports"));
    if (!runs.length) card.append(el("p", { class: "muted" }, "None yet."));
    for (const r of runs) {
      card.append(
        el(
          "div",
          { class: "list-item", onclick: () => showRun(r.runId) },
          el("strong", {}, r.guild || r.runId),
          el("div", { class: "spacer" }),
          el("span", { class: "muted small" }, r.runId),
        ),
      );
    }
  } catch (e) {
    card.append(el("div", { class: "banner danger" }, e.message));
  }
}

// ---- export setup: what and which channels -------------------------------------------------

async function showExportSetup(guild) {
  const view = el("div", {});
  view.append(el("button", { class: "secondary", onclick: showDashboard }, "← Back to servers"));
  render(view);

  // What to back up
  const modeEverything = el("input", { type: "radio", name: "mode", value: "all", checked: true });
  const modeMine = el("input", { type: "radio", name: "mode", value: "mine" });

  // Channel scope
  const chanAll = el("input", { type: "radio", name: "chans", value: "all", checked: true });
  const chanPick = el("input", { type: "radio", name: "chans", value: "pick" });
  const chanListWrap = el("div", { class: "channel-list hidden" }, el("p", { class: "muted" }, "Loading channels…"));
  const channelChecks = {};

  const loadChannels = async () => {
    if (Object.keys(channelChecks).length) return;
    try {
      const chans = await api.get(`/api/guilds/${guild.id}/channels`);
      clear(chanListWrap);
      if (!chans.length) chanListWrap.append(el("p", { class: "muted" }, "No channels."));
      for (const c of chans) {
        const cb = el("input", { type: "checkbox", checked: true });
        channelChecks[c.id] = cb;
        const label = (c.category ? c.category + " / " : "") + c.name + (c.isForum ? " (forum)" : c.isVoice ? " (voice)" : "");
        chanListWrap.append(el("div", { class: "check tight" }, cb, el("label", {}, label)));
      }
    } catch (e) {
      clear(chanListWrap);
      chanListWrap.append(el("div", { class: "banner danger" }, e.message));
    }
  };
  chanPick.addEventListener("change", () => {
    chanListWrap.classList.remove("hidden");
    loadChannels();
  });
  chanAll.addEventListener("change", () => chanListWrap.classList.add("hidden"));

  // Options
  const threads = el("select", {}, el("option", { value: "all" }, "Active + archived threads"), el("option", { value: "active" }, "Active threads only"), el("option", { value: "none" }, "No threads"));
  const media = el("input", { type: "checkbox", checked: true });
  const voice = el("input", { type: "checkbox", checked: true });
  const fmtHtml = el("input", { type: "checkbox", checked: true });
  const fmtJson = el("input", { type: "checkbox", checked: true });
  const btn = el("button", {}, "Start backup");

  btn.addEventListener("click", async () => {
    const formats = [];
    if (fmtHtml.checked) formats.push("HtmlDark");
    if (fmtJson.checked) formats.push("Json");
    if (!formats.length) formats.push("HtmlDark");

    const channelIds = chanPick.checked
      ? Object.entries(channelChecks).filter(([, cb]) => cb.checked).map(([id]) => id)
      : [];
    if (chanPick.checked && !channelIds.length) {
      alert("Select at least one channel, or choose All channels.");
      return;
    }

    btn.disabled = true;
    try {
      const res = await api.post("/api/exports", {
        guildId: guild.id,
        options: {
          threads: threads.value,
          includeVoiceChannels: voice.checked,
          downloadMedia: media.checked,
          formatMarkdown: true,
          onlyMyMessages: modeMine.checked,
          channelIds,
          formats,
        },
      });
      window._lastRunId = null;
      runJob(res.jobId, "Backing up " + guild.name, (ev) => {
        if (ev.type === "run" && ev.payload.runId) window._lastRunId = ev.payload.runId;
      }, () => (window._lastRunId ? showRun(window._lastRunId) : showDashboard()));
    } catch (e) {
      alert(e.message);
      btn.disabled = false;
    }
  });

  view.append(
    el(
      "div",
      { class: "card" },
      el("h2", {}, "Back up " + guild.name),
      el("p", { class: "muted" }, "A backup is required before anything can be deleted — you can only ever delete messages that were backed up here."),

      el("h3", {}, "What to back up"),
      el("div", { class: "check" }, modeEverything, el("label", {}, el("strong", {}, "Everything"), el("div", { class: "muted small" }, "All messages from everyone (a full archive)."))),
      el("div", { class: "check" }, modeMine, el("label", {}, el("strong", {}, "Only messages I sent"), el("div", { class: "muted small" }, `Only ${state.session.user.name}'s messages — lighter, and the usual choice if you just want to clean up after yourself.`))),

      el("h3", {}, "Which channels"),
      el("div", { class: "check" }, chanAll, el("label", {}, "All channels")),
      el("div", { class: "check" }, chanPick, el("label", {}, "Choose channels")),
      chanListWrap,

      el("h3", {}, "Options"),
      el("label", {}, "Threads"),
      threads,
      el("div", { class: "check" }, voice, el("label", {}, "Include voice channel text")),
      el("div", { class: "check" }, media, el("label", {}, "Download media (attachments, images)")),
      el("label", {}, "Save as"),
      el("div", { class: "check" }, fmtHtml, el("label", {}, "HTML (readable)")),
      el("div", { class: "check" }, fmtJson, el("label", {}, "JSON (for re-import / analysis)")),

      el("div", { style: "margin-top:16px" }, btn),
    ),
  );
}

// ---- job runner (SSE) ----------------------------------------------------------------------

function runJob(jobId, title, onEvent, onDone) {
  const bar = el("div", {}, el("div", {}));
  const barInner = bar.firstChild;
  const status = el("div", { class: "muted" }, "Starting…");
  const log = el("div", { class: "log" });
  const cancelBtn = el("button", { class: "danger" }, "Cancel");
  cancelBtn.addEventListener("click", () => api.post(`/api/jobs/${jobId}/cancel`).catch(() => {}));

  render(el("div", {}, el("div", { class: "card" }, el("h2", {}, title), el("div", { class: "progress" }, bar), status, el("div", { style: "margin:10px 0" }, cancelBtn), log)));

  const es = new EventSource(`/api/jobs/${jobId}/events`);
  const addLog = (m) => {
    log.append(document.createTextNode(m + "\n"));
    log.scrollTop = log.scrollHeight;
  };
  es.addEventListener("log", (e) => addLog(JSON.parse(e.data).message));
  es.addEventListener("progress", (e) => {
    const p = JSON.parse(e.data);
    barInner.style.width = Math.round((p.progress || 0) * 100) + "%";
    if (p.status) status.textContent = p.status;
  });
  es.addEventListener("run", (e) => onEvent && onEvent({ type: "run", payload: JSON.parse(e.data) }));
  es.addEventListener("summary", (e) => onEvent && onEvent({ type: "summary", payload: JSON.parse(e.data) }));
  es.addEventListener("done", (e) => {
    const d = JSON.parse(e.data);
    es.close();
    cancelBtn.classList.add("hidden");
    addLog(`[${d.state}]` + (d.error ? " " + d.error : ""));
    status.textContent = d.state;
    status.after(el("button", { onclick: () => onDone && onDone(d) }, "Continue"));
  });
  es.onerror = () => {};
}

// ---- run view: summary, channels, deletion -------------------------------------------------

async function showRun(runId) {
  let data;
  try {
    data = await api.get(`/api/exports/${runId}`);
  } catch (e) {
    alert(e.message);
    return showDashboard();
  }
  const run = data.run;
  const view = el("div", {});
  view.append(el("button", { class: "secondary", onclick: showDashboard }, "← Back"));

  const completeChannels = data.channels.filter((c) => c.status === "Complete").length;
  const mediaErr = data.channels.filter((c) => c.status === "CompleteMediaErrors").length;
  const totalMsgs = data.channels.reduce((a, c) => a + c.messageCount, 0);

  const summary = el(
    "div",
    { class: "card" },
    el("h2", {}, run.guildName || runId),
    el(
      "div",
      { class: "stat-row" },
      el("div", {}, el("div", { class: "stat" }, String(totalMsgs)), el("div", { class: "muted" }, "messages backed up")),
      el("div", {}, el("div", { class: "stat" }, String(completeChannels)), el("div", { class: "muted" }, "channels complete")),
      el("div", {}, el("div", { class: "stat" }, String(mediaErr)), el("div", { class: "muted" }, "with missing media")),
    ),
    el("p", { class: "muted" }, "Backed-up files: ", el("a", { href: `/files/${runId}/`, target: "_blank" }, "browse export folder")),
  );
  if (mediaErr > 0) {
    const retry = el("button", { class: "secondary" }, "Retry missing media");
    retry.addEventListener("click", async () => {
      try {
        const res = await api.post(`/api/exports/${runId}/retry-media`);
        runJob(res.jobId, "Recovering media", null, () => showRun(runId));
      } catch (e) {
        alert(e.message);
      }
    });
    summary.append(retry);
  }
  view.append(summary);

  const chanCard = el("div", { class: "card" }, el("h2", {}, "Channels"));
  const tbl = el("table", {}, el("tr", {}, el("th", {}, "Channel"), el("th", {}, "Messages"), el("th", {}, "Status")));
  for (const c of data.channels) {
    const pillClass = c.status === "Complete" ? "ok" : c.status === "CompleteMediaErrors" ? "warn" : c.status === "Failed" ? "err" : "muted";
    tbl.append(el("tr", {}, el("td", {}, c.name), el("td", {}, String(c.messageCount)), el("td", {}, el("span", { class: "pill " + pillClass }, c.status + (c.failedAttachmentCount ? ` (${c.failedAttachmentCount})` : "")))));
  }
  chanCard.append(tbl);
  view.append(chanCard);

  view.append(messageBrowserCard(runId, data.channels));
  view.append(deletionCard(runId, run, data.channels));
  render(view);
}

// ---- message browser: click to protect individual messages ---------------------------------

function messageBrowserCard(runId, channels) {
  const myId = state.session.user.id;
  const channelName = Object.fromEntries(channels.map((c) => [c.id, c.name]));

  const channelSel = el("select", {}, el("option", { value: "" }, "All channels"), ...channels.map((c) => el("option", { value: c.id }, c.name)));
  const search = el("input", { type: "search", placeholder: "Search message text…" });
  const onlyMine = el("input", { type: "checkbox" });
  const keptOnly = el("input", { type: "checkbox" });
  const loadBtn = el("button", { class: "secondary" }, "Search");
  const moreBtn = el("button", { class: "secondary hidden" }, "Load more");
  const info = el("div", { class: "muted small" });

  const tbl = el("table", {}, el("tr", {}, el("th", {}, "Protect"), el("th", {}, "Author"), el("th", {}, "When"), el("th", {}, "Channel"), el("th", {}, "Message")));
  const body = el("tbody", {});
  tbl.append(body);

  let cursor = 0;

  function row(m) {
    const cb = el("input", { type: "checkbox", title: "Protect this message from deletion" });
    cb.checked = m.keep;
    cb.addEventListener("change", async () => {
      cb.disabled = true;
      try {
        await api.put(`/api/exports/${runId}/messages/${m.id}/keep`, { keep: cb.checked });
      } catch (e) {
        alert(e.message);
        cb.checked = !cb.checked;
      }
      cb.disabled = false;
    });
    const when = new Date(m.timestamp).toLocaleString();
    const deleted = m.deleteStatus === "Deleted" || m.deleteStatus === "Gone";
    return el(
      "tr",
      { class: deleted ? "struck" : "" },
      el("td", {}, cb),
      el("td", {}, m.authorName + (m.isPinned ? " 📌" : "")),
      el("td", { class: "small nowrap" }, when),
      el("td", { class: "small" }, channelName[m.channelId] || ""),
      el("td", { class: "msg-content" }, m.content || el("span", { class: "muted" }, "(no text)")),
    );
  }

  async function load(reset) {
    if (reset) {
      cursor = 0;
      clear(body);
    }
    loadBtn.disabled = true;
    try {
      const params = new URLSearchParams();
      if (channelSel.value) params.set("channel", channelSel.value);
      if (search.value.trim()) params.set("q", search.value.trim());
      if (onlyMine.checked) params.set("author", myId);
      if (keptOnly.checked) params.set("keep", "true");
      params.set("cursor", cursor);
      const res = await api.get(`/api/exports/${runId}/messages?` + params.toString());
      for (const m of res.items) body.append(row(m));
      info.textContent = `${res.total} message(s) match`;
      if (res.nextCursor >= 0) {
        cursor = res.nextCursor;
        moreBtn.classList.remove("hidden");
      } else {
        moreBtn.classList.add("hidden");
      }
    } catch (e) {
      info.textContent = e.message;
    }
    loadBtn.disabled = false;
  }

  loadBtn.addEventListener("click", () => load(true));
  moreBtn.addEventListener("click", () => load(false));
  search.addEventListener("keydown", (e) => e.key === "Enter" && load(true));

  return el(
    "div",
    { class: "card" },
    el("h2", {}, "Browse & protect messages"),
    el("p", { class: "muted" }, "Tick a message to protect it from deletion — no need to copy IDs. Protected messages are kept no matter what the delete filters say."),
    el("div", { class: "row" }, channelSel, search),
    el("div", { style: "display:flex; gap:18px; align-items:center; margin:8px 0" }, el("div", { class: "check tight" }, onlyMine, el("label", {}, "Only my messages")), el("div", { class: "check tight" }, keptOnly, el("label", {}, "Only protected")), loadBtn),
    info,
    tbl,
    el("div", { style: "margin-top:10px" }, moreBtn),
  );
}

function deletionCard(runId, run, channels) {
  const mine = state.session.user;

  const scope = el("select", {}, el("option", { value: "Mine" }, `Only my own messages (${mine.name})`), el("option", { value: "Everyone" }, "Everyone's messages (needs Manage Messages)"));

  // Narrowing (undiscord-style) filters
  const onlyContains = el("input", { type: "text", placeholder: "only delete if text contains…" });
  const onlyLink = el("input", { type: "checkbox" });
  const onlyAttach = el("input", { type: "checkbox" });
  const afterDate = el("input", { type: "text", placeholder: "after (YYYY-MM-DD or message ID)" });
  const beforeDate = el("input", { type: "text", placeholder: "before (YYYY-MM-DD or message ID)" });

  // Protect-from-deletion
  const keepPinned = el("input", { type: "checkbox", checked: true });
  const keepAttach = el("input", { type: "checkbox" });
  const keepContains = el("input", { type: "text", placeholder: "comma-separated phrases to protect" });
  const excludeMsgs = el("textarea", { placeholder: "message IDs or links to protect, one per line" });
  const allowMedia = el("input", { type: "checkbox" });

  const excludeChannelsWrap = el("div", { class: "channel-list" });
  const channelChecks = {};
  for (const c of channels) {
    const cb = el("input", { type: "checkbox" });
    channelChecks[c.id] = cb;
    excludeChannelsWrap.append(el("div", { class: "check tight" }, cb, el("label", {}, c.name)));
  }

  const result = el("div", {});

  function criteria() {
    return {
      scope: scope.value,
      onlyContains: onlyContains.value.trim() || null,
      onlyWithLink: onlyLink.checked,
      onlyWithAttachment: onlyAttach.checked,
      onlyAfter: afterDate.value.trim() || null,
      onlyBefore: beforeDate.value.trim() || null,
      keepPinned: keepPinned.checked,
      keepWithAttachments: keepAttach.checked,
      allowIncompleteMedia: allowMedia.checked,
      keepIfContains: keepContains.value.split(",").map((s) => s.trim()).filter(Boolean),
      excludedMessageIds: excludeMsgs.value.split("\n").map((s) => s.trim()).filter(Boolean).map(parseRef).filter(Boolean),
      excludedChannelIds: Object.entries(channelChecks).filter(([, cb]) => cb.checked).map(([id]) => id),
    };
  }

  const previewBtn = el("button", {}, "Preview deletion");
  previewBtn.addEventListener("click", async () => {
    clear(result);
    previewBtn.disabled = true;
    try {
      const p = await api.post(`/api/exports/${runId}/deletions/preview`, criteria());
      result.append(renderPreview(runId, run, p));
    } catch (e) {
      result.append(el("div", { class: "banner danger" }, e.message));
    }
    previewBtn.disabled = false;
  });

  return el(
    "div",
    { class: "card" },
    el("h2", {}, "Delete messages"),
    el("div", { class: "banner danger" }, "Deletion is permanent. Only messages backed up in this export can be deleted. Preview and dry-run first."),

    el("h3", {}, "Whose messages to delete"),
    scope,

    el("h3", {}, "Only delete messages that… (optional)"),
    el("label", {}, "Contain text"),
    onlyContains,
    el("div", { class: "check" }, onlyLink, el("label", {}, "Contain a link")),
    el("div", { class: "check" }, onlyAttach, el("label", {}, "Have an attachment")),
    el("div", { class: "row" }, el("div", {}, el("label", {}, "Sent after"), afterDate), el("div", {}, el("label", {}, "Sent before"), beforeDate)),

    el("h3", {}, "Protect from deletion"),
    el("div", { class: "check" }, keepPinned, el("label", {}, "Keep pinned messages")),
    el("div", { class: "check" }, keepAttach, el("label", {}, "Keep messages with attachments")),
    el("label", {}, "Keep messages containing"),
    keepContains,
    el("label", {}, "Protect specific messages"),
    excludeMsgs,
    el("details", {}, el("summary", { class: "muted" }, "Exclude whole channels"), excludeChannelsWrap),
    el("div", { class: "check" }, allowMedia, el("label", {}, "Also include channels with missing media backups")),

    el("div", { style: "margin-top:16px" }, previewBtn),
    result,
  );
}

function parseRef(v) {
  if (v.includes("discord.com/channels/")) {
    const last = v.split("/").filter(Boolean).pop();
    return /^\d+$/.test(last) ? last : null;
  }
  return /^\d+$/.test(v) ? v : null;
}

function renderPreview(runId, run, p) {
  const wrap = el("div", { style: "margin-top:16px" });
  if (p.accountMismatch) {
    wrap.append(el("div", { class: "banner danger" }, `This export was made by a different account. You are signed in as ${p.deletingUser.name}; under "only my own", only that account's messages are affected.`));
  }

  wrap.append(el("div", { class: "stat-row" }, el("div", {}, el("div", { class: "stat" }, String(p.totalCandidates)), el("div", { class: "muted" }, "would be deleted")), el("div", {}, el("div", { class: "stat" }, String(p.totalKept)), el("div", { class: "muted" }, "protected by your rules"))));

  if (p.totalCandidates === 0) {
    wrap.append(el("div", { class: "banner warn" }, "Nothing matches — adjust the filters above."));
    return wrap;
  }

  const perChan = el("table", {}, el("tr", {}, el("th", {}, "Channel"), el("th", {}, "To delete"), el("th", {}, "Protected")));
  for (const c of p.channels) perChan.append(el("tr", {}, el("td", {}, c.channelName), el("td", {}, String(c.candidateCount)), el("td", {}, String(c.keptCount))));
  wrap.append(perChan);

  const confirm = el("input", { type: "text", placeholder: `type "${run.guildName}" to confirm` });
  const dryRun = el("input", { type: "checkbox", checked: true });
  const go = el("button", { class: "danger" }, "Delete");

  go.addEventListener("click", async () => {
    go.disabled = true;
    try {
      const res = await api.post(`/api/exports/${runId}/deletions`, { planId: p.planId, confirm: confirm.value, dryRun: dryRun.checked });
      runJob(res.jobId, dryRun.checked ? "Dry run (nothing deleted)" : "Deleting messages", null, () => showRun(runId));
    } catch (e) {
      alert(e.message);
      go.disabled = false;
    }
  });

  wrap.append(
    el("div", { class: "check", style: "margin-top:12px" }, dryRun, el("label", {}, el("strong", {}, "Dry run"), " — count only, delete nothing")),
    el("label", {}, "Confirm server name"),
    confirm,
    el("div", { style: "margin-top:10px" }, go),
  );
  return wrap;
}

// ---- boot ----------------------------------------------------------------------------------

(async function boot() {
  try {
    const s = await api.get("/api/session");
    if (s.authenticated) {
      state.session = s;
      showDashboard();
    } else {
      showSignIn();
    }
  } catch {
    showSignIn();
  }
})();
