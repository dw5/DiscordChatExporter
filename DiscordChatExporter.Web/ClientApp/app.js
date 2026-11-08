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

  view.append(messageBrowserCard(runId, run.guildId, data.channels));
  view.append(deletionCard(runId, run, data.channels));
  render(view);
}

// ---- message browser: full-screen Discord-style reader to protect messages -----------------

const AUTHOR_COLORS = ["#e67e22", "#3498db", "#9b59b6", "#e74c3c", "#1abc9c", "#f1c40f", "#2ecc71", "#eb459e", "#00b894", "#5865f2"];
function authorColor(name) {
  let h = 0;
  for (const ch of name || "?") h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return AUTHOR_COLORS[h % AUTHOR_COLORS.length];
}
function initialsOf(name) {
  return (name || "?").trim().charAt(0).toUpperCase() || "?";
}
function fmtTime(ts) {
  const d = new Date(ts);
  return d.toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

// Renders a safe subset of Discord markdown to HTML. Everything is HTML-escaped first, so only the
// fixed whitelist of tags emitted below can ever reach the DOM — no raw user HTML, no scripts.
function renderContent(raw) {
  let s = escapeHtml(raw);

  // Stash code spans so inner markdown/URLs aren't reformatted.
  const stash = [];
  const keep = (html) => {
    stash.push(html);
    return "\u0000" + (stash.length - 1) + "\u0000";
  };
  s = s.replace(/```(?:[a-zA-Z0-9_+-]*\n)?([\s\S]*?)```/g, (_, code) => keep(`<pre class="dc-code">${code.replace(/\n$/, "")}</pre>`));
  s = s.replace(/`([^`\n]+?)`/g, (_, code) => keep(`<code class="dc-inline">${code}</code>`));

  // Links [text](url) and bare URLs — http/https only.
  s = s.replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, (_, text, url) => `<a href="${url}" target="_blank" rel="noopener noreferrer">${text}</a>`);
  s = s.replace(/(^|[\s(])(https?:\/\/[^\s<]+)/g, (_, pre, url) => `${pre}<a href="${url}" target="_blank" rel="noopener noreferrer">${url}</a>`);

  // Emphasis.
  s = s.replace(/\*\*([^*]+?)\*\*/g, "<strong>$1</strong>");
  s = s.replace(/__([^_]+?)__/g, "<u>$1</u>");
  s = s.replace(/~~([^~]+?)~~/g, "<del>$1</del>");
  s = s.replace(/\*([^*\n]+?)\*/g, "<em>$1</em>");
  s = s.replace(/(^|[^a-zA-Z0-9_])_([^_\n]+?)_(?![a-zA-Z0-9_])/g, "$1<em>$2</em>");

  // Discord entities (<@id>, <#id>, <@&id>, <:name:id>) → readable text.
  s = s.replace(/&lt;a?:(\w+):\d+&gt;/g, ":$1:");
  s = s.replace(/&lt;@!?(\d+)&gt;/g, "@mention");
  s = s.replace(/&lt;@&amp;\d+&gt;/g, "@role");
  s = s.replace(/&lt;#\d+&gt;/g, "#channel");

  return s.replace(/\u0000(\d+)\u0000/g, (_, i) => stash[+i]);
}

// The card on the run page is just an entry point; the reader itself is a full-screen overlay.
function messageBrowserCard(runId, guildId, channels) {
  const open = el("button", {}, "Browse & protect messages");
  open.addEventListener("click", () => openMessageBrowser(runId, guildId, channels));
  return el(
    "div",
    { class: "card" },
    el("h2", {}, "Browse & protect messages"),
    el("p", { class: "muted" }, "Open a full-screen reader that looks like Discord. Click the shield on any message to protect it from deletion — no copying IDs. Protected messages are kept no matter what the delete filters say."),
    el("div", { style: "margin-top:12px" }, open),
  );
}

function openMessageBrowser(runId, guildId, channels) {
  const myId = state.session.user.id;
  const channelName = Object.fromEntries(channels.map((c) => [c.id, c.name]));

  // --- sidebar: channel picker ---
  const chanList = el("div", { class: "dc-channels" });
  let currentChannel = ""; // "" = all channels
  const chanButtons = {};

  const makeChanBtn = (id, label) => {
    const b = el("div", { class: "dc-channel" + (id === currentChannel ? " active" : "") }, el("span", { class: "dc-hash" }, id ? "#" : "✳"), el("span", {}, label));
    b.addEventListener("click", () => {
      currentChannel = id;
      for (const [cid, btn] of Object.entries(chanButtons)) btn.classList.toggle("active", cid === id);
      titleEl.textContent = id ? "#" + (channels.find((c) => c.id === id)?.name || "") : "All channels";
      load(true);
    });
    chanButtons[id] = b;
    return b;
  };
  // Only channels that actually have backed-up messages are worth browsing.
  const activeChannels = channels.filter((c) => (c.messageCount || 0) > 0);
  chanList.append(makeChanBtn("", "All channels"));
  for (const c of activeChannels) chanList.append(makeChanBtn(c.id, c.name));

  const sidebar = el("div", { class: "dc-sidebar" }, el("div", { class: "dc-sidebar-head" }, "Channels"), chanList);

  // --- top bar ---
  const titleEl = el("div", { class: "dc-title" }, "All channels");
  const search = el("input", { type: "search", placeholder: "Search messages…" });
  const onlyMine = el("input", { type: "checkbox" });
  const keptOnly = el("input", { type: "checkbox" });
  const keptCounter = el("span", { class: "dc-kept muted small" }, "");
  const closeBtn = el("button", { class: "secondary" }, "✕ Close");

  const controls = el(
    "div",
    { class: "dc-controls" },
    search,
    el("label", { class: "dc-toggle" }, onlyMine, "Only mine"),
    el("label", { class: "dc-toggle" }, keptOnly, "Only protected"),
    keptCounter,
  );
  const topbar = el("div", { class: "dc-topbar" }, titleEl, el("div", { class: "spacer" }), controls, closeBtn);

  // --- message pane ---
  const pane = el("div", { class: "dc-messages" });
  const info = el("div", { class: "dc-info muted small" }, "Loading…");
  const moreBtn = el("button", { class: "secondary hidden" }, "Load older");
  const paneWrap = el("div", { class: "dc-pane" }, pane, el("div", { class: "dc-foot" }, info, moreBtn));

  const main = el("div", { class: "dc-main" }, topbar, paneWrap);
  const overlay = el("div", { class: "modal-overlay" }, el("div", { class: "modal dc-modal" }, sidebar, main));

  let cursor = 0;
  let lastGroup = null; // { author, time }

  const close = () => {
    document.removeEventListener("keydown", onKey);
    overlay.remove();
  };
  const onKey = (e) => e.key === "Escape" && close();
  closeBtn.addEventListener("click", close);
  overlay.addEventListener("click", (e) => e.target === overlay && close());
  document.addEventListener("keydown", onKey);

  // Profile picture, falling back to a colored initial if there's no avatar or it fails to load.
  function avatarEl(m) {
    const fallback = el("div", { class: "dc-avatar", style: `background:${authorColor(m.authorName)}` }, initialsOf(m.authorName));
    if (!m.authorAvatarUrl) return fallback;
    const img = el("img", { class: "dc-avatar", src: m.authorAvatarUrl, loading: "lazy", alt: m.authorName });
    img.addEventListener("error", () => img.replaceWith(fallback));
    return img;
  }

  // Renders one attachment: the backed-up local file if present, falling back to the (expiring)
  // Discord CDN url if the file wasn't downloaded. Images/video/audio play inline; anything else
  // is a download link. Spoilers are blurred until clicked.
  function attachmentEl(a) {
    const localSrc = `/files/${runId}/media/${encodeURIComponent(a.localName)}`;
    const wrap = el("div", { class: "dc-att" + (a.isSpoiler ? " spoiler" : "") });
    const onErr = (node) => node.addEventListener("error", () => {
      if (a.url && node.getAttribute("src") !== a.url) node.setAttribute("src", a.url);
    });

    if (a.kind === "image") {
      const img = el("img", { class: "dc-att-img", src: localSrc, loading: "lazy", alt: a.fileName });
      onErr(img);
      wrap.append(el("a", { href: localSrc, target: "_blank", rel: "noopener noreferrer" }, img));
    } else if (a.kind === "video") {
      const v = el("video", { class: "dc-att-img", src: localSrc, controls: true, preload: "metadata" });
      onErr(v);
      wrap.append(v);
    } else if (a.kind === "audio") {
      const au = el("audio", { src: localSrc, controls: true, preload: "metadata" });
      onErr(au);
      wrap.append(au);
    } else {
      wrap.append(el("a", { class: "dc-att-file", href: localSrc, target: "_blank", rel: "noopener noreferrer", download: a.fileName }, "📄 " + a.fileName));
    }

    if (a.isSpoiler) wrap.addEventListener("click", () => wrap.classList.remove("spoiler"), { once: true });
    return wrap;
  }

  // A sticker renders as its image straight from Discord's public (non-expiring) sticker CDN.
  // Lottie stickers are JSON-animated with no static image, so they show as a labelled chip.
  function stickerEl(s) {
    const wrap = el("div", { class: "dc-att dc-sticker" });
    if (s.isImage) {
      wrap.append(el("img", { class: "dc-sticker-img", src: s.url, loading: "lazy", alt: s.name, title: s.name }));
    } else {
      wrap.append(el("span", { class: "dc-att-file" }, "🏷 " + s.name + " (animated sticker)"));
    }
    return wrap;
  }

  // A forwarded message shown as a quoted block with its own text, stickers and attachments.
  function forwardedEl(f) {
    const kids = [el("div", { class: "dc-fwd-label" }, "➦ Forwarded")];
    const t = (f.content || "").trim();
    if (t) kids.push(el("div", { class: "dc-text", html: renderContent(f.content) }));
    const fs = f.stickers || [];
    if (fs.length) {
      const row = el("div", { class: "dc-media" });
      for (const s of fs) row.append(stickerEl(s));
      kids.push(row);
    }
    const fa = f.attachments || [];
    if (fa.length) {
      const grid = el("div", { class: "dc-media" });
      for (const a of fa) grid.append(attachmentEl(a));
      kids.push(grid);
    }
    if (!t && !fs.length && !fa.length) kids.push(el("div", { class: "dc-text muted" }, "(no content)"));
    return el("div", { class: "dc-fwd" }, ...kids);
  }

  function renderMessage(m) {
    // Grouping also breaks on a channel change, so the "All channels" view never merges messages
    // from different channels under one header.
    const sameGroup =
      lastGroup &&
      lastGroup.author === m.authorId &&
      lastGroup.channel === m.channelId &&
      new Date(m.timestamp) - lastGroup.time < 7 * 60 * 1000;
    lastGroup = { author: m.authorId, channel: m.channelId, time: new Date(m.timestamp) };

    const deleted = m.deleteStatus === "Deleted" || m.deleteStatus === "Gone";
    const msg = el("div", { class: "dc-msg" + (sameGroup ? " grouped" : "") + (m.keep ? " protected" : "") + (deleted ? " deleted" : "") });

    const gutter = sameGroup
      ? el("div", { class: "dc-gutter dc-ts-hover" }, new Date(m.timestamp).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }))
      : el("div", { class: "dc-gutter" }, avatarEl(m));

    const bodyKids = [];
    if (!sameGroup) {
      bodyKids.push(
        el(
          "div",
          { class: "dc-head" },
          el("span", { class: "dc-author", style: `color:${authorColor(m.authorName)}` }, m.authorName),
          el("span", { class: "dc-ts" }, fmtTime(m.timestamp)),
          // In the all-channels view, say which channel this message is in.
          !currentChannel ? el("span", { class: "dc-chan-tag" }, "#" + (channelName[m.channelId] || "")) : null,
          m.isPinned ? el("span", { class: "dc-pin", title: "Pinned" }, "📌") : null,
        ),
      );
    }
    const text = (m.content || "").trim();
    if (text) {
      bodyKids.push(el("div", { class: "dc-text", html: renderContent(m.content) }));
    }
    const stickers = m.stickers || [];
    if (stickers.length) {
      const row = el("div", { class: "dc-media" });
      for (const s of stickers) row.append(stickerEl(s));
      bodyKids.push(row);
    }
    const atts = m.attachments || [];
    if (atts.length) {
      const grid = el("div", { class: "dc-media" });
      for (const a of atts) grid.append(attachmentEl(a));
      bodyKids.push(grid);
    } else if (m.attachmentCount) {
      // Older exports (before attachments were recorded) only know the count. Re-export to see them.
      bodyKids.push(el("div", { class: "dc-attach muted small" }, `📎 ${m.attachmentCount} attachment${m.attachmentCount > 1 ? "s" : ""} — re-export to view`));
    }
    // A forwarded message carries its own text/stickers/attachments but no author of its own.
    if (m.forwarded) bodyKids.push(forwardedEl(m.forwarded));
    if (m.embedCount) bodyKids.push(el("div", { class: "dc-attach muted small" }, `🔗 ${m.embedCount} embed${m.embedCount > 1 ? "s" : ""}`));
    // Only fall back to "(no text)" when nothing at all was rendered for this message.
    const hasAnything = text || stickers.length || atts.length || m.attachmentCount || m.forwarded || m.embedCount;
    if (!hasAnything) bodyKids.push(el("div", { class: "dc-text muted" }, "(no text)"));
    if (deleted) bodyKids.push(el("div", { class: "dc-attach muted small" }, "already deleted"));

    const body = el("div", { class: "dc-body" }, ...bodyKids);

    const shield = el("button", { class: "dc-shield" + (m.keep ? " on" : ""), title: m.keep ? "Protected — click to unprotect" : "Protect from deletion" }, "🛡");
    shield.addEventListener("click", async (e) => {
      e.stopPropagation();
      const next = !m.keep;
      shield.disabled = true;
      try {
        await api.put(`/api/exports/${runId}/messages/${m.id}/keep`, { keep: next });
        m.keep = next;
        msg.classList.toggle("protected", next);
        shield.classList.toggle("on", next);
        shield.title = next ? "Protected — click to unprotect" : "Protect from deletion";
        keptCount += next ? 1 : -1;
        updateKeptCounter();
      } catch (err) {
        alert(err.message);
      }
      shield.disabled = false;
    });

    // Jump to the live message on Discord for context before deciding to keep or delete it.
    const discordUrl = guildId ? `https://discord.com/channels/${guildId}/${m.channelId}/${m.id}` : null;
    const actions = [shield];
    if (discordUrl) actions.push(el("a", { class: "dc-open", href: discordUrl, target: "_blank", rel: "noopener noreferrer", title: "Open in Discord" }, "↗"));

    msg.append(gutter, body, el("div", { class: "dc-actions" }, ...actions));
    return msg;
  }

  let keptCount = 0;
  const updateKeptCounter = () => (keptCounter.textContent = keptCount ? `${keptCount} protected` : "");

  async function load(reset) {
    if (reset) {
      cursor = 0;
      lastGroup = null;
      clear(pane);
      info.textContent = "Loading…";
    }
    moreBtn.disabled = true;
    try {
      const params = new URLSearchParams();
      if (currentChannel) params.set("channel", currentChannel);
      if (search.value.trim()) params.set("q", search.value.trim());
      if (onlyMine.checked) params.set("author", myId);
      if (keptOnly.checked) params.set("keep", "true");
      params.set("cursor", cursor);
      const res = await api.get(`/api/exports/${runId}/messages?` + params.toString());
      for (const m of res.items) pane.append(renderMessage(m));
      info.textContent = res.total ? `${res.total} message(s) match` : "No messages match.";
      if (res.nextCursor >= 0) {
        cursor = res.nextCursor;
        moreBtn.classList.remove("hidden");
      } else {
        moreBtn.classList.add("hidden");
      }
    } catch (e) {
      info.textContent = e.message;
    }
    moreBtn.disabled = false;
  }

  // Live protected count across the whole export (independent of current filter).
  async function refreshKeptCount() {
    try {
      const res = await api.get(`/api/exports/${runId}/messages?keep=true&cursor=0`);
      keptCount = res.total || 0;
      updateKeptCounter();
    } catch {}
  }

  let searchTimer;
  search.addEventListener("input", () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => load(true), 250);
  });
  onlyMine.addEventListener("change", () => load(true));
  keptOnly.addEventListener("change", () => load(true));
  moreBtn.addEventListener("click", () => load(false));
  pane.addEventListener("scroll", () => {
    if (!moreBtn.classList.contains("hidden") && !moreBtn.disabled && pane.scrollTop + pane.clientHeight >= pane.scrollHeight - 200) load(false);
  });

  document.body.append(overlay);
  refreshKeptCount();
  load(true);
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
      // Send raw, nonblank references so the server validates them and rejects anything unreadable
      // visibly — never silently drop a line the user meant as protection.
      excludedMessageIds: excludeMsgs.value.split("\n").map((s) => s.trim()).filter(Boolean),
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

  wrap.append(el("div", { class: "stat-row" }, el("div", {}, el("div", { class: "stat" }, String(p.totalCandidates)), el("div", { class: "muted" }, "would be deleted")), el("div", {}, el("div", { class: "stat" }, String(p.totalKept)), el("div", { class: "muted" }, "protected / kept"))));

  if (p.totalCandidates === 0) {
    wrap.append(el("div", { class: "banner warn" }, "Nothing matches — adjust the filters above."));
    return wrap;
  }

  // What will be gone, and from where: per-channel counts with a few example messages.
  const samplesByChannel = Object.fromEntries((p.samples || []).map((s) => [s.channelId, s]));
  wrap.append(el("p", { class: "muted small" }, "These messages will be permanently deleted from Discord. Review by channel below — protect anything you want to keep in “Browse & protect messages” first."));

  const list = el("div", { class: "del-list" });
  const channelsToShow = p.channels.filter((c) => c.candidateCount > 0).sort((a, b) => b.candidateCount - a.candidateCount);
  for (const c of channelsToShow) {
    const s = samplesByChannel[c.id] || samplesByChannel[c.channelId];
    const head = el(
      "div",
      { class: "del-chan-head" },
      el("span", { class: "del-chan-name" }, "#" + c.channelName),
      el("span", { class: "pill err" }, `${c.candidateCount} to delete`),
      c.keptCount ? el("span", { class: "pill muted" }, `${c.keptCount} kept`) : null,
    );
    const samples = el("div", { class: "del-samples" });
    if (s && s.sample) {
      for (const m of s.sample) {
        samples.append(
          el(
            "div",
            { class: "del-sample" },
            el("span", { class: "del-sample-author", style: `color:${authorColor(m.authorName)}` }, m.authorName),
            el("span", { class: "del-sample-ts muted small" }, fmtTime(m.timestamp)),
            el("span", { class: "del-sample-text" }, m.content || el("span", { class: "muted" }, "(no text)")),
          ),
        );
      }
      if (c.candidateCount > s.sample.length) samples.append(el("div", { class: "muted small del-more" }, `…and ${c.candidateCount - s.sample.length} more in #${c.channelName}`));
    }
    list.append(el("div", { class: "del-chan" }, head, samples));
  }
  wrap.append(list);

  const needsUser = p.accountMismatch;
  const confirm = el("input", { type: "text", placeholder: `type ${JSON.stringify(p.confirmText || run.guildName)} to confirm` });
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
    el("label", {}, needsUser ? "Confirm server name and your username" : "Confirm server name"),
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
