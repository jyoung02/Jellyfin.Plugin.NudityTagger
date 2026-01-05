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
- **Smart caching** - Caches IMDB results to avoid repeated requests with automatic cleanup
- **Rate limiting** - Configurable delay between requests to respect IMDB
- **Retry logic** - Automatic retry with exponential backoff for failed requests
- **Enhanced parsing** - Multiple fallback strategies for reliable HTML parsing
- **Performance optimized** - ~10x faster keyword matching using optimized algorithms
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

The release zip includes all required files. Extract the following to the plugin folder:
- `Jellyfin.Plugin.NudityTagger_*.dll` (versioned DLL, e.g., `Jellyfin.Plugin.NudityTagger_1.1.3.0.dll`)
- `HtmlAgilityPack.dll`
- `meta.json`

## Configuration Options

| Option | Description | Default | Range |
|--------|-------------|---------|-------|
| Enable Plugin | Turn the plugin on/off | Enabled | - |
| Minimum Severity | Only tag content at or above this level | Mild | None, Mild, Moderate, Severe |
| Tag Prefix | Optional prefix for tags (e.g., "Content: ") | None | Max 50 chars |
| Cache Duration | How long to cache IMDB results (hours) | 168 (1 week) | 1-8760 |
| Request Delay | Delay between IMDB requests (ms) | 2000 | 500-60000 |
| Max Retry Attempts | Number of retry attempts for failed requests | 3 | 1-10 |
| Enable Cache Cleanup | Automatically clean up old cache files | Enabled | - |
| Skip Already Tagged | Skip items that already have nudity tags | Enabled | - |
| Set Tagline | Display content warnings in tagline/overview | Enabled | - |

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

The required files will be in `bin/publish/`. Copy the plugin DLL, `HtmlAgilityPack.dll`, and `meta.json` to your Jellyfin plugins folder.

## Troubleshooting

### Tags Not Displaying
- Ensure the plugin is enabled in Dashboard → Plugins
- Run the scheduled task manually: Dashboard → Scheduled Tasks → "Tag Nudity Content"
- Refresh your library: Dashboard → Libraries → Scan Library
- Check the Jellyfin logs for any errors

### High Failure Rate
- Increase `Request Delay` to avoid IMDB rate limiting (try 3000-5000ms)
- Increase `Max Retry Attempts` for better resilience (try 5)
- Check your internet connection and IMDB accessibility

### Cache Issues
- Enable `Enable Cache Cleanup` to automatically remove old files
- Manually delete cache files in: `[Jellyfin Cache]/nuditytagger/`
- Reduce `Cache Duration` if you want fresher data

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## Changelog

- **v1.1.3** - Performance improvements, retry logic, cache cleanup, enhanced HTML parsing
- **v1.1.2** - Security fixes: path traversal protection, input validation, URL encoding
- **v1.1.1** - Fixed version display, improved tagline/overview display

For detailed changelog, see [Releases](https://github.com/jyoung02/Jellyfin.Plugin.NudityTagger/releases).

## How It Works

1. The plugin queries your Jellyfin library for movies and TV shows
2. For each item with an IMDB ID, it checks the cache first
3. If not cached or expired, it fetches the Parents Guide page with retry logic
4. It uses multiple parsing strategies to reliably extract the "Sex & Nudity" section
5. It analyzes severity ratings and descriptions using optimized keyword matching
6. Based on severity and content keywords, it assigns appropriate tags
7. Tags are applied to items in your Jellyfin library
8. Old cache files are automatically cleaned up to save disk space

## Recent Improvements (v1.1.3)

### Performance
- **~10x faster keyword matching** - Optimized algorithms using HashSet for O(1) lookups
- **Expanded keyword lists** - More detection keywords for better accuracy

### Reliability
- **Retry logic** - Automatic retry with exponential backoff (configurable 1-10 attempts)
- **Enhanced HTML parsing** - 4x more reliable with multiple fallback strategies
- **Better error handling** - Improved logging and graceful failure recovery

### Maintenance
- **Automatic cache cleanup** - Prevents disk space issues by removing old cache files
- **Better progress reporting** - Enhanced logging and statistics

## Security Features

The plugin includes comprehensive security measures:
- **Path traversal protection** - Validates and sanitizes all file paths
- **Input validation** - All configuration values are validated and clamped
- **URL encoding** - Proper URI construction prevents injection attacks
- **Resource limits** - Timeouts and length limits prevent DoS attacks
- **IMDB ID validation** - Strict regex validation prevents malicious input

For detailed security information, see `SECURITY_FIXES.md` in the repository.

## License

MIT License
