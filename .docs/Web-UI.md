# Web UI

**DiscordChatExporter.Web** is a self-hosted, browser-based interface for exporting a whole Discord
server in one click and then selectively deleting messages that were backed up. It reuses
`DiscordChatExporter.Core` for exporting and adds message deletion on top.

> [!WARNING]
> Deletion is permanent and cannot be undone. Automating a user account — including bulk-deleting
> your own messages with a user token — violates Discord's Terms of Service and can get the account
> banned. For a server you administer, use a bot token with the **Manage Messages** permission.
> Always run a **preview** and a **dry run** before deleting for real.

## Running

### From source

```
dotnet run --project DiscordChatExporter.Web
```

It prints a URL containing a one-time access key, e.g. `http://127.0.0.1:5080/?key=<key>`. Open it;
the key is exchanged for a cookie and stripped from the address bar.

Options:

- `--urls http://127.0.0.1:PORT` — change the bind address (or `ASPNETCORE_URLS`).
- `--data-dir PATH` — where exports are written (or `DCE_DATA_DIR`). Defaults to
  `~/.local/share/DiscordChatExporter.Web`.
- `--open` — open the URL in your browser automatically (`xdg-open`).
- `DISCORD_TOKEN` — if set, the app signs in with it at startup.
- `DCE_ACCESS_KEY` — use a fixed access key instead of a random one.
- `DCE_ALLOWED_HOSTS` — semicolon-separated host allowlist (default `localhost;127.0.0.1;[::1]`).

### Single-file binary

```
dotnet publish DiscordChatExporter.Web -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
./out/DiscordChatExporter.Web
```

The browser UI is embedded in the assembly, so the single binary is self-sufficient.

### Docker

```
docker build -f DiscordChatExporter.Web.dockerfile -t dce-web .
docker run --rm -p 127.0.0.1:5080:5080 -v "$PWD/dce-data:/data" -e DCE_ACCESS_KEY=pick-a-secret dce-web
```

Keep the port bound to `127.0.0.1`. If you expose it more widely, set `DCE_ACCESS_KEY` and
`DCE_ALLOWED_HOSTS`, and put it behind TLS and your own auth.

## What a run contains

Each export is written to its own folder that is never reused:

```
exports/<Guild> [<id>]/<timestamp>_<random>/
  <Guild - Category - channel [id]>.html
  <Guild - Category - channel [id]>.json
  media/                 downloaded attachments and images
  manifest.db            SQLite index of every backed-up message
  deletion-log.jsonl     append-only record of every delete attempt
```

## Export

Pick a server, then choose:

- **Threads**: active + archived, active only, or none. Forum and media channels are expanded into
  their threads automatically.
- **Voice channel text**, **media download**, and **formats** (HTML and/or JSON).

Thread discovery runs in *strict* mode: if a request fails for a reason other than a channel being
inaccessible (for example an expired token or a server error), the job fails loudly rather than
silently producing a short list.

If some attachments fail to download, the channel is marked **complete with missing media**. Use
**Retry missing media** to re-fetch just those files into `media/` — this never rewrites the HTML or
JSON, it only fills in the files they already link to.

## Delete

1. **Criteria** — scope (your own messages, or everyone's), and what to keep: pinned messages,
   messages with attachments, messages containing given phrases, specific message IDs or links, and
   whole channels. Channels with missing media backups are excluded unless you opt in.
2. **Preview** — shows how many messages would be deleted per channel and how many your rules keep.
   The exact set of message IDs is saved as a plan.
3. **Delete** — you must type the server name to confirm, with a dry-run option on by default. The
   job deletes only the previewed IDs, re-checking each one, newest first, paced to respect rate
   limits. Bot tokens use bulk deletion for recent messages. It is resumable: re-running continues
   where it left off without re-deleting.

If the account, the export, or your keep choices change after a preview, the plan is invalidated and
you are asked to preview again.

## Safety model

- **Backed-up only.** Deletion candidates come solely from the export manifest, so you can only ever
  delete messages that were successfully exported.
- **Local only.** Binds to loopback; every API and file request needs the access cookie, and
  state-changing requests need a custom header plus a same-origin check.
- **Token in memory only.** Never written to disk, never returned to the browser, never logged.
- **Sandboxed viewing.** Exported HTML is served with a strict Content-Security-Policy that blocks
  scripts, so a crafted message can't call the API with your cookie.

## Limitations

- Media recovery needs the attachment's message to still exist (to get a fresh signed URL); if a
  message was deleted, its attachment can't be recovered.
- Direct messages are out of scope in this version — neither exported nor deleted. Only servers you are a member of are listed.
