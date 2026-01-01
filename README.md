# Jellyfin Nudity Tagger Plugin

A Jellyfin plugin that automatically tags movies and TV shows with nudity/sexual content warnings based on IMDB Parents Guide data.

## Features

- **Automatic tagging** - Scrapes IMDB Parents Guide for sex/nudity content ratings
- **Detailed categories** - Tags content with specific labels:
  - `No Nudity`
  - `Brief Nudity`
  - `Partial Nudity`
  - `Full Nudity`
  - `Sexual Content`
  - `Graphic Sexual Content`
- **Configurable thresholds** - Set minimum severity level to tag (None, Mild, Moderate, Severe)
- **Smart caching** - Caches IMDB results to avoid repeated requests
- **Rate limiting** - Configurable delay between requests to respect IMDB
- **Scheduled task** - Runs daily or manually via Jellyfin's task scheduler

## Installation

1. Download the latest release
2. Extract to your Jellyfin plugins folder:
   - Windows: `C:\ProgramData\Jellyfin\Server\plugins\NudityTagger\`
   - Linux: `/var/lib/jellyfin/plugins/NudityTagger/`
   - Docker: `/config/plugins/NudityTagger/`
3. Restart Jellyfin
4. Configure the plugin in Dashboard → Plugins → Nudity Tagger

## Required Files

Copy these DLLs to the plugin folder:
- `Jellyfin.Plugin.NudityTagger.dll`
- `HtmlAgilityPack.dll`

## Configuration Options

| Option | Description | Default |
|--------|-------------|---------|
| Enable Plugin | Turn the plugin on/off | Enabled |
| Minimum Severity | Only tag content at or above this level | Mild |
| Tag Prefix | Optional prefix for tags (e.g., "Content: ") | None |
| Cache Duration | How long to cache IMDB results (hours) | 168 (1 week) |
| Request Delay | Delay between IMDB requests (ms) | 2000 |
| Skip Already Tagged | Skip items that already have nudity tags | Enabled |

## Usage

1. After installation, go to **Dashboard → Scheduled Tasks**
2. Find **"Tag Nudity Content"** under the "Nudity Tagger" category
3. Click **Run** to manually process your library, or wait for the daily scheduled run (3 AM)

## Building from Source

Requires .NET 8.0 SDK.

```bash
git clone https://github.com/jyoung02/Jellyfin.Plugin.NudityTagger.git
cd Jellyfin.Plugin.NudityTagger
dotnet publish --configuration Release --output bin/publish
```

## How It Works

1. The plugin queries your Jellyfin library for movies and TV shows
2. For each item with an IMDB ID, it fetches the Parents Guide page
3. It parses the "Sex & Nudity" section for severity ratings and descriptions
4. Based on severity and content keywords, it assigns appropriate tags
5. Tags are applied to items in your Jellyfin library

## License

MIT License
