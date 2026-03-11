# Jellyfin AniWorld Downloader

![GitHub Release](https://img.shields.io/github/v/release/SiroxCW/Jellyfin-AniWorld-Downloader)
![GitHub License](https://img.shields.io/github/license/SiroxCW/Jellyfin-AniWorld-Downloader)

A Jellyfin plugin for searching and downloading anime and series from [aniworld.to](https://aniworld.to), [s.to](https://s.to), and [hianime.to](https://hianime.to), directly inside Jellyfin's web interface.

Series View| Search View
:---:|:---:
![Series View](screenshots/preview_anime.png) | ![Search View](screenshots/preview_search.png)

## Features

- **Search and browse** anime and series with cover art, popular titles, and new releases
- **Download** individual episodes, full seasons, or entire series
- **Three sites supported**: aniworld.to (anime), s.to (series), and hianime.to (English sub/dub)
- **Multiple languages**: German Dub, German Sub, English Sub (aniworld), German Dub, English Dub (s.to), English Sub, English Dub (hianime)
- **Multiple providers**: VOE, Filemoon, Vidoza and Vidmoly (aniworld/s.to), MegaCloud auto-extraction (hianime)
- **Download manager** with real-time progress, cancel, retry, and batch operations
- **Automatic retries** with exponential backoff and provider fallback
- **Auto library scan** so new episodes appear in Jellyfin immediately
- **Jellyfin-compatible naming**: `Series Name/Season 01/Series Name - S01E01 - Episode Title.mkv`

## Looking for more?

This plugin is a lightweight downloader built into Jellyfin for convenience. If you need a standalone tool with its own web UI, more configuration options, and additional features, check out [AniWorld-Downloader](https://github.com/phoenixthrush/AniWorld-Downloader) by phoenixthrush which I also actively maintain.

## Requirements

- Jellyfin **10.9.0** or newer
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

### Per-site settings (aniworld.to / s.to / hianime.to)

Each site can be enabled or disabled independently and has its own settings. If a per-site setting is left empty, the global default is used.

| Setting | Description |
|---------|-------------|
| Enabled | Toggle this site on or off |
| Download Path | Where to save files (should point to a Jellyfin library folder) |
| Preferred Language | Default language for downloads |
| Preferred Provider | Default streaming provider (aniworld/s.to only) |
| Fallback Provider | Backup provider if the primary one fails after all retries (aniworld/s.to only) |

> **Note:** HiAnime is best used for English dub or when aniworld.to does not have the anime you are looking for. HiAnime does not have seasons, so each entry maps to a single season. Search for the specific season you want. HiAnime's download path defaults to the AniWorld download path if left empty.

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

### hianime.to

1. Searches use hianime's search page
2. Episode lists are fetched via hianime's AJAX API
3. Server list is retrieved per episode (HD-2 and HD-3, HD-1 is skipped due to Cloudflare)
4. MegaCloud embed page is visited to extract an auth token
5. Token is used to fetch HLS stream URLs from MegaCloud's getSources API
6. ffmpeg downloads the HLS stream with the required Referer header

### Supported providers

| Provider | Site | Method |
|----------|------|--------|
| **VOE** | aniworld/s.to | Decodes obfuscated JSON (ROT13, base64, char shift) to extract HLS URLs |
| **Filemoon** | aniworld/s.to | Handles both modern Byse API (AES-256-GCM) and legacy packed JS |
| **Vidmoly** | aniworld/s.to | Extracts HLS URLs from JavaScript sources |
| **Vidoza** | aniworld/s.to | Extracts MP4 URLs from source tags |
| **MegaCloud** | hianime | Extracts auth token from embed page, fetches HLS URLs via getSources API |

## License

MIT
