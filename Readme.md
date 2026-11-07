# DiscordChatExporter

[![Status](https://img.shields.io/badge/status-maintenance-ffd700.svg)](https://github.com/Tyrrrz/.github/blob/prime/docs/project-status.md)
[![Build](https://img.shields.io/github/actions/workflow/status/Tyrrrz/DiscordChatExporter/main.yml?branch=prime)](https://github.com/Tyrrrz/DiscordChatExporter/actions)
[![Coverage](https://img.shields.io/codecov/c/github/Tyrrrz/DiscordChatExporter/prime)](https://codecov.io/gh/Tyrrrz/DiscordChatExporter)
[![Release](https://img.shields.io/github/release/Tyrrrz/DiscordChatExporter.svg)](https://github.com/Tyrrrz/DiscordChatExporter/releases)
[![Downloads](https://img.shields.io/github/downloads/Tyrrrz/DiscordChatExporter/total.svg)](https://github.com/Tyrrrz/DiscordChatExporter/releases)
[![Pulls](https://img.shields.io/docker/pulls/tyrrrz/discordchatexporter)](https://hub.docker.com/r/tyrrrz/discordchatexporter)
[![Discord](https://img.shields.io/discord/869237470565392384?label=discord)](https://discord.gg/2SUWKFnHSm)

<table>
    <tr>
        <td width="99999" align="center">Development of this project is entirely funded by the community. <b><a href="https://tyrrrz.me/donate">Consider donating to support!</a></b></td>
    </tr>
</table>

<p align="center">
    <img src="favicon.png" alt="Icon" />
</p>

> **Made in 🇺🇦 Ukraine — [despite Russia's best efforts](https://tyrrrz.me/ukraine)**.
> Using this project or its source code comes with the implicit agreement that you condemn Russia's war of aggression against Ukraine, recognize Russia as a terrorist state, and support Ukraine's territorial integrity.

**DiscordChatExporter** is an application that can be used to export message history from any [Discord](https://discord.com) channel to a file.
It works with direct messages, group messages, and server channels, and supports Discord's dialect of markdown as well as most other rich media features.

> [!WARNING]
> While **DiscordChatExporter** allows it, automating user accounts is against Discord TOS and may result in you getting banned.
> If possible, use a bot to export chat logs from accessible channels.

**See also**:
- 📦 [mlomb/chat-analytics](https://github.com/mlomb/chat-analytics) — solution for analyzing chat patterns of Discord users, using exports produced by **DiscordChatExporter**
- 📦 [slatinsky/DiscordChatExporter-frontend](https://github.com/slatinsky/DiscordChatExporter-frontend) — convenient viewer for exports produced by **DiscordChatExporter**

## Download

- **Graphical user interface** (desktop app):
  - 🟢 **[Stable release](https://github.com/Tyrrrz/DiscordChatExporter/releases/latest)**: look for `DiscordChatExporter.*.zip`
  - 🟠 [CI build](https://github.com/Tyrrrz/DiscordChatExporter/actions/workflows/main.yml): look for `DiscordChatExporter.*.zip`
  - 📦 [Scoop](https://scoop.sh/#/apps?q=DiscordChatExporter&p=1&id=c71b7367623c560a2dc746b9739b9568b79b59ae): `scoop install extras/discordchatexporter` (community-maintained)
  - 📦 [WinGet](https://winstall.app/apps/Tyrrrz.DiscordChatExporter.GUI): `winget install Tyrrrz.DiscordChatExporter.GUI` (community-maintained)
  - 📦 [AUR](https://aur.archlinux.org/packages/discord-chat-exporter-gui): `yay -S discord-chat-exporter-gui` (community-maintained)
  - 📦 [Nix](https://search.nixos.org/packages?show=discordchatexporter-desktop): `nix-shell -p discordchatexporter-desktop` (community-maintained)
- **Command-line interface** (terminal app):
  - 🟢 **[Stable release](https://github.com/Tyrrrz/DiscordChatExporter/releases/latest)**: look for `DiscordChatExporter.Cli.*.zip`
  - 🟠 [CI build](https://github.com/Tyrrrz/DiscordChatExporter/actions/workflows/main.yml): look for `DiscordChatExporter.Cli.*.zip`
  - 🐋 [Docker](https://hub.docker.com/r/tyrrrz/discordchatexporter): `docker pull tyrrrz/discordchatexporter`
  - 📦 [WinGet](https://winstall.app/apps/Tyrrrz.DiscordChatExporter.CLI): `winget install Tyrrrz.DiscordChatExporter.CLI` (community-maintained)
  - 📦 [AUR](https://aur.archlinux.org/packages/discord-chat-exporter-cli): `yay -S discord-chat-exporter-cli` (community-maintained)
  - 📦 [Nix](https://search.nixos.org/packages?show=discordchatexporter-cli): `nix-shell -p discordchatexporter-cli` (community-maintained)

> [!NOTE]
> Community-maintained packages are published independently from this repository and may not always be up to date with the latest release.

> [!NOTE]
> If you're unsure which build is right for your system, consult with [this page](https://useragent.cc) to determine your OS and CPU architecture.

## Web UI (self-hosted)

This fork adds **DiscordChatExporter.Web**, a browser-based UI you run on your own machine. It does one-click "export everything" for a whole server, then lets you **selectively delete** messages — only ever the messages the export already backed up.

> [!WARNING]
> Deletion is permanent, and automating a user account (including deleting your own messages with a user token) is against Discord's ToS. For a server you administer, prefer a bot token with the **Manage Messages** permission. Always preview and dry-run first.

Run it locally:

```
dotnet run --project DiscordChatExporter.Web
# then open the printed http://127.0.0.1:5080/?key=... URL
```

Or with Docker (keep it bound to loopback):

```
docker build -f DiscordChatExporter.Web.dockerfile -t dce-web .
docker run --rm -p 127.0.0.1:5080:5080 -v "$PWD/dce-data:/data" \
  -e DCE_ACCESS_KEY=pick-a-secret dce-web
```

How it works and stays safe:

- Binds to `127.0.0.1:5080` by default. Every request needs the access key the app prints at startup (or `DCE_ACCESS_KEY`); it sets a strict cookie, and state-changing requests need a custom header and same-origin check. `DCE_ALLOWED_HOSTS` guards against DNS rebinding.
- **Export**: pick a server, choose threads/formats/media, click *Export everything*. Each run is written to its own immutable folder under the data dir, with the HTML/JSON files, a `media/` folder, a SQLite `manifest.db`, and a `deletion-log.jsonl` audit trail.
- **Delete**: choose a scope (your own messages, or everyone's with a bot) and exclusions (channels, pinned, attachments, phrases, specific messages). *Preview* persists the exact candidate set; the real run deletes only those IDs, re-checking each, paced to respect rate limits, resumable, and requires typing the server name to confirm.
- The token is held in memory only — never written to disk or sent anywhere but Discord.

See [the Web UI docs](.docs/Web-UI.md) for details.

## Features

- Cross-platform graphical and command-line interfaces
- Authentication via either a user or a bot token
- Multiple output formats: HTML (dark/light), TXT, CSV, JSON
- Support for markdown, attachments, embeds, emoji, and other rich media features
- File partitioning, date ranges, message filtering, and other export options
- Self-contained exports that can be viewed offline

## Screenshots

![channel list](.assets/list.png)
![rendered output](.assets/output.png)
