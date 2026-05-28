# Jellyfin AniWorld Downloader

![GitHub Release](https://img.shields.io/github/v/release/SiroxCW/Jellyfin-AniWorld-Downloader)
![GitHub License](https://img.shields.io/github/license/SiroxCW/Jellyfin-AniWorld-Downloader)

A Jellyfin plugin for searching and downloading anime and series from [aniworld.to](https://aniworld.to) and [s.to](https://s.to), directly inside Jellyfin's web interface.

Series View| Search View
:---:|:---:
![Series View](screenshots/preview_anime.png) | ![Search View](screenshots/preview_search.png)

## Features

- **Search and browse** anime and series with cover art, popular titles, and new releases
- **Download** individual episodes, full seasons, or entire series
- **Two sites supported**: aniworld.to (anime), s.to (series) and hianime.to (currently disabled)
- **Multiple languages**: German Dub, German Sub, English Sub (aniworld), German Dub, English Dub (s.to)
- **Multiple providers**: VOE, Filemoon, Vidoza and Vidmoly
- **Download manager** with real-time progress, cancel, retry, and batch operations
- **Automatic retries** with exponential backoff and provider fallback
- **Auto library scan** so new episodes appear in Jellyfin immediately
- **Jellyfin-compatible naming**: `Series Name/Season 01/Series Name - S01E01 - Episode Title.mkv`

## Looking for more?

This plugin is a lightweight downloader built into Jellyfin for convenience. If you need a standalone tool with its own web UI, more configuration options, and additional features, check out [AniWorld-Downloader](https://github.com/phoenixthrush/AniWorld-Downloader) by phoenixthrush which I also actively maintain.

## Requirements

- Jellyfin **10.11.0** or newer
- **ffmpeg** (bundled with Jellyfin)
- **[File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)** plugin (optional, required for non-admin access)

## Installation

### Plugin Repository (recommended)

1. In Jellyfin, go to **Dashboard > Plugins > Repositories**
2. Add a new repository with this URL:
   ```
   https://raw.githubusercontent.com/SiroxCW/Jellyfin-AniWorld-Downloader/main/manifest.json
   ```
4. Go to **Catalog**, find **AniWorld Downloader**, and click **Install**
5. Restart Jellyfin

*If the plugin does not show up in the Catalog, restarting Jellyfin made it appear.*

Updates will show up automatically in the plugin catalog.

### Manual Install

1. Download the latest `.zip` from [Releases](https://github.com/SiroxCW/Jellyfin-AniWorld-Downloader/releases)
2. Extract it to your Jellyfin plugins directory:
   ```
   /var/lib/jellyfin/plugins/AniWorldDownloader/
   ```
   The folder should contain `Jellyfin.Plugin.AniWorld.dll` and `meta.json`.
3. Restart Jellyfin

### Build from Source

Requires .NET 9.0 SDK.

```bash
cd Jellyfin.Plugin.AniWorld
dotnet build --configuration Release
```

Then copy the output:

```bash
mkdir -p /var/lib/jellyfin/plugins/AniWorldDownloader
cp bin/Release/net9.0/Jellyfin.Plugin.AniWorld.dll /var/lib/jellyfin/plugins/AniWorldDownloader/
cp meta.json /var/lib/jellyfin/plugins/AniWorldDownloader/
sudo systemctl restart jellyfin
```

## Configuration

After installing, go to **Dashboard > Plugins > AniWorld Downloader** to configure.

### General

| Setting | Description |
|---------|-------------|
| Max Concurrent Downloads | How many downloads run at the same time (default: 2) |
| Max Retry Attempts | How many times to retry a failed download before giving up (default: 3) |
| Auto-scan Library | Trigger a Jellyfin library scan when a download finishes |
| Enable for non-admin users | Allow non-admin users to access the downloader via the sidebar (see [Non-admin access](#non-admin-access)) |
| Proxy Server | Route all network requests and downloads through a proxy (e.g. `http://proxy:8080` or `socks5://proxy:1080`). Leave empty to connect directly. Requires a server restart after changing. |
| Movie download path | Default save location for movies (should point to a Jellyfin library folder) |
| Language Fallback order | When the requested language is unavailable, fall back to other languages in the chosen priority order (default: No Fallback) |

### Per-site settings (aniworld.to / s.to)

Each site can be enabled or disabled independently and has its own settings. If a per-site setting is left empty, the global default is used.

| Setting | Description |
|---------|-------------|
| Enabled | Toggle this site on or off |
| Download Path | Where to save files (should point to a Jellyfin library folder) |
| Preferred Language | Default language for downloads |
| Preferred Provider | Default streaming provider |
| Fallback Provider | Backup provider if the primary one fails after all retries |

## Non-admin access

By default, the plugin UI is only accessible from the admin dashboard. You can enable it for all users so it appears as a sidebar entry.

### Setup

1. Install the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin
2. Restart Jellyfin
3. Go to **Dashboard > Plugins > AniWorld Downloader** and enable **Enable for non-admin users**
4. Restart Jellyfin again

Non-admin users will see an **AniWorld Downloader** entry in the sidebar that opens the full UI in a modal overlay. The settings button is hidden in this view. Configuration is only available through the admin dashboard.

> **Note:** The File Transformation plugin injects a script tag into Jellyfin's `index.html` at runtime (no files are modified on disk). Disabling the setting and restarting will remove the sidebar entry.

## Usage

1. Open **AniWorld Downloader** from the admin dashboard sidebar (or the sidebar entry if non-admin access is enabled)
2. Use **Search** to find a title, or browse **Popular** / **New Releases**
3. Click a title to see its seasons and episodes
4. Hit **Download** on an episode, or use **Download Season** / **Download All Seasons** for batch downloads
5. Switch to the **Downloads** tab to monitor progress
6. Check **History** for past downloads and stats

## How It Works

### aniworld.to / s.to

1. Searches use each site's AJAX search endpoint
2. Series, season, and episode pages are scraped to find provider links
3. Provider redirect URLs are resolved to embed pages
4. Each provider has a dedicated extractor that pulls out the direct stream URL
5. ffmpeg downloads the stream and saves it as MKV

### Supported providers

| Provider | Site | Method |
|----------|------|--------|
| **VOE** | aniworld/s.to | Decodes obfuscated JSON (ROT13, base64, char shift) to extract HLS URLs |
| **Filemoon** | aniworld/s.to | Handles both modern Byse API (AES-256-GCM) and legacy packed JS |
| **Vidmoly** | aniworld/s.to | Extracts HLS URLs from JavaScript sources |
| **Vidoza** | aniworld/s.to | Extracts MP4 URLs from source tags |

## Known Issues

### s.to downloads fail with "No JSON script blocks found in VOE page"

s.to has put their provider redirector (`/r?t=...`) behind a Cloudflare Turnstile gate that activates **per-IP** for "flagged" egress addresses. Many datacenter ranges (Hetzner, OVH, etc.) get the gate; most residential IPs don't. From a flagged IP, every download retry produces logs like:

```
Resolved to embed URL: "https://s.to/r?t=..."
No JSON script blocks found in VOE page
Failed to extract VOE source from page
```

Because the gate requires a real browser to solve an interactive Turnstile widget, the plugin can't bypass it on its own. Your options:

1. **Route the plugin through a clean proxy.** Set **Proxy Server** in plugin settings to a SOCKS5 or HTTP proxy on a residential / unflagged IP (e.g. `socks5://user:pass@proxy:1080`). Restart Jellyfin. This is the recommended fix.
2. **Use a VPN on the Jellyfin host** so all outbound traffic exits a clean IP.
3. **aniworld.to is currently unaffected** — if you only need anime, downloads still work normally on flagged IPs.

To check whether your server's IP is flagged, the repo includes [`scripts/check-sto-flag.sh`](scripts/check-sto-flag.sh): copy it to the server and run it.

## Legal Disclaimer

Jellyfin AniWorld Downloader is a **client-side** tool that enables access to content hosted on third-party websites. It **does not host, upload, store, or distribute any media itself**.

This software is **not intended to promote piracy or copyright infringement**. You are solely responsible for how you use Jellyfin AniWorld Downloader and for ensuring that your use **complies with applicable laws** and the **terms of service of the websites you access**.

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
