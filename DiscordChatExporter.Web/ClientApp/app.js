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
    else if (v !== null && v !== undefined) e.setAttribute(k, v);
  }
  for (const c of children.flat()) {
    if (c == null) continue;
    e.append(c.nodeType ? c : document.createTextNode(c));
  }
  return e;
};

const root = () => document.getElementById("app");
const clear = (n) => {
  while (n.firstChild) n.removeChild(n.firstChild);
};
const esc = (s) => (s || "").replace(/[<>&]/g, (c) => ({ "<": "&lt;", ">": "&gt;", "&": "&amp;" })[c]);

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
      const res = await api.post("/api/session", { token: token.value });
      state.session = res;
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

// ---- dashboard: pick server, export --------------------------------------------------------

async function showDashboard() {
  const view = el("div", {});
  render(view);

  const guildCard = el("div", { class: "card" }, el("h2", {}, "Servers"), el("p", { class: "muted" }, "Loading servers…"));
  const runsCard = el("div", { class: "card" }, el("h2", {}, "Previous exports"), el("p", { class: "muted" }, "Loading…"));
  view.append(guildCard, runsCard);

  try {
    const guilds = await api.get("/api/guilds");
    clear(guildCard);
    guildCard.append(el("h2", {}, "Servers"));
    if (!guilds.length) guildCard.append(el("p", { class: "muted" }, "No servers found for this account."));
    for (const g of guilds) {
      guildCard.append(
        el(
          "div",
          { class: "list-item", onclick: () => showExportSetup(g) },
          el("strong", {}, g.name),
          el("div", { class: "spacer" }),
          el("span", { class: "muted" }, g.id),
        ),
      );
    }
  } catch (e) {
    clear(guildCard);
    guildCard.append(el("h2", {}, "Servers"), el("div", { class: "banner danger" }, e.message));
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
          el("span", { class: "muted" }, r.runId),
        ),
      );
    }
  } catch (e) {
    card.append(el("div", { class: "banner danger" }, e.message));
  }
}

function showExportSetup(guild) {
  const threads = el("select", {}, el("option", { value: "all" }, "Active + archived threads"), el("option", { value: "active" }, "Active threads only"), el("option", { value: "none" }, "No threads"));
  const media = el("input", { type: "checkbox", checked: "" });
  const voice = el("input", { type: "checkbox", checked: "" });
  const fmtHtml = el("input", { type: "checkbox", checked: "" });
  const fmtJson = el("input", { type: "checkbox", checked: "" });
  const btn = el("button", {}, "Export everything");

  btn.addEventListener("click", async () => {
    const formats = [];
    if (fmtHtml.checked) formats.push(1); // HtmlDark
    if (fmtJson.checked) formats.push(4); // Json
    if (!formats.length) formats.push(1);
    btn.disabled = true;
    try {
      const res = await api.post("/api/exports", {
        guildId: guild.id,
        options: {
          threads: threads.value,
          includeVoiceChannels: voice.checked,
          downloadMedia: media.checked,
          formatMarkdown: true,
          formats,
        },
      });
      runJob(res.jobId, "Exporting " + guild.name, (ev) => {
        if (ev.type === "run" && ev.payload.runId) {
          window._lastRunId = ev.payload.runId;
        }
      }, () => {
        if (window._lastRunId) showRun(window._lastRunId);
        else showDashboard();
      });
    } catch (e) {
      alert(e.message);
      btn.disabled = false;
    }
  });

  render(
    el(
      "div",
      {},
      el("button", { class: "secondary", onclick: showDashboard }, "← Back"),
      el(
        "div",
        { class: "card" },
        el("h2", {}, "Export " + guild.name),
        el("label", {}, "Threads"),
        threads,
        el("div", { class: "check" }, voice, el("label", {}, "Include voice channel text")),
        el("div", { class: "check" }, media, el("label", {}, "Download media (attachments, images)")),
        el("label", {}, "Formats"),
        el("div", { class: "check" }, fmtHtml, el("label", {}, "HTML (readable)")),
        el("div", { class: "check" }, fmtJson, el("label", {}, "JSON (for re-import / analysis)")),
        el("div", { style: "margin-top:14px" }, btn),
      ),
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

  render(
    el(
      "div",
      {},
      el("div", { class: "card" }, el("h2", {}, title), el("div", { class: "progress" }, bar), status, el("div", { style: "margin:10px 0" }, cancelBtn), log),
    ),
  );

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
    const next = el("button", { onclick: () => onDone && onDone(d) }, "Continue");
    status.after(next);
  });
  es.onerror = () => {
    // Stream closes normally on done; ignore transient errors
  };
}

// ---- run view: channels, messages, deletion ------------------------------------------------

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

  // Summary
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
    el("p", { class: "muted" }, "Open the backed-up files: ", el("a", { href: `/files/${runId}/`, target: "_blank" }, "browse export folder")),
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

  // Channel table
  const chanCard = el("div", { class: "card" }, el("h2", {}, "Channels"));
  const tbl = el("table", {}, el("tr", {}, el("th", {}, "Channel"), el("th", {}, "Messages"), el("th", {}, "Status")));
  for (const c of data.channels) {
    const pillClass = c.status === "Complete" ? "ok" : c.status === "CompleteMediaErrors" ? "warn" : c.status === "Failed" ? "err" : "muted";
    tbl.append(
      el(
        "tr",
        {},
        el("td", {}, c.name),
        el("td", {}, String(c.messageCount)),
        el("td", {}, el("span", { class: "pill " + pillClass }, c.status + (c.failedAttachmentCount ? ` (${c.failedAttachmentCount})` : ""))),
      ),
    );
  }
  chanCard.append(tbl);
  view.append(chanCard);

  // Deletion section
  view.append(deletionCard(runId, run, data.channels));
  render(view);
}

function deletionCard(runId, run, channels) {
  const scope = el("select", {}, el("option", { value: "Mine" }, "Only my own messages"), el("option", { value: "Everyone" }, "Everyone's messages (needs permission)"));
  const keepPinned = el("input", { type: "checkbox", checked: "" });
  const keepAttach = el("input", { type: "checkbox" });
  const allowMedia = el("input", { type: "checkbox" });
  const keepContains = el("input", { type: "text", placeholder: "comma-separated phrases to keep" });
  const excludeMsgs = el("textarea", { placeholder: "message IDs or links to keep, one per line" });

  const excludeChannelsWrap = el("div", {});
  const channelChecks = {};
  for (const c of channels) {
    const cb = el("input", { type: "checkbox" });
    channelChecks[c.id] = cb;
    excludeChannelsWrap.append(el("div", { class: "check" }, cb, el("label", {}, c.name)));
  }

  const result = el("div", {});

  function criteria() {
    return {
      scope: scope.value,
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
    el("div", { class: "banner warn" }, "Deletion is permanent. Only messages that were backed up in this export can be deleted. Preview and dry-run first."),
    el("label", {}, "Whose messages"),
    scope,
    el("div", { class: "check" }, keepPinned, el("label", {}, "Keep pinned messages")),
    el("div", { class: "check" }, keepAttach, el("label", {}, "Keep messages with attachments")),
    el("div", { class: "check" }, allowMedia, el("label", {}, "Include channels with missing media backups")),
    el("label", {}, "Keep messages containing"),
    keepContains,
    el("label", {}, "Exclude specific messages"),
    excludeMsgs,
    el("label", {}, "Exclude whole channels"),
    excludeChannelsWrap,
    el("div", { style: "margin-top:14px" }, previewBtn),
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
    wrap.append(el("div", { class: "banner danger" }, `The export was made by a different account. You are signed in as ${p.deletingUser.name}; only that account's messages will be touched under "mine".`));
  }

  wrap.append(
    el("div", { class: "stat-row" }, el("div", {}, el("div", { class: "stat" }, String(p.totalCandidates)), el("div", { class: "muted" }, "would be deleted")), el("div", {}, el("div", { class: "stat" }, String(p.totalKept)), el("div", { class: "muted" }, "kept by your rules"))),
  );

  const perChan = el("table", {}, el("tr", {}, el("th", {}, "Channel"), el("th", {}, "To delete"), el("th", {}, "Kept")));
  for (const c of p.channels) perChan.append(el("tr", {}, el("td", {}, c.channelName), el("td", {}, String(c.candidateCount)), el("td", {}, String(c.keptCount))));
  wrap.append(perChan);

  const confirm = el("input", { type: "text", placeholder: `type "${run.guildName}" to confirm` });
  const dryRun = el("input", { type: "checkbox", checked: "" });
  const go = el("button", { class: "danger" }, "Delete");

  go.addEventListener("click", async () => {
    go.disabled = true;
    try {
      const res = await api.post(`/api/exports/${runId}/deletions`, {
        planId: p.planId,
        confirm: confirm.value,
        dryRun: dryRun.checked,
      });
      runJob(res.jobId, dryRun.checked ? "Dry run" : "Deleting messages", null, () => showRun(runId));
    } catch (e) {
      alert(e.message);
      go.disabled = false;
    }
  });

  wrap.append(
    el("div", { class: "check" }, dryRun, el("label", {}, "Dry run (count only, delete nothing)")),
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
