using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.NudityTagger.Configuration;
using Jellyfin.Plugin.NudityTagger.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NudityTagger.Tasks;

public class NudityTaggingTask : IScheduledTask
{
    private readonly ILogger<NudityTaggingTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly HttpClient _httpClient;

    // Keywords for categorizing content
    private static readonly string[] FullNudityKeywords = { "full frontal", "fully nude", "completely nude", "full nudity", "genitalia", "genital", "penis", "vagina", "pubic" };
    private static readonly string[] GraphicSexualKeywords = { "graphic sex", "explicit sex", "sex scene", "sexual intercourse", "thrusting", "orgasm", "ejaculation", "masturbation" };
    private static readonly string[] SexualContentKeywords = { "sexual", "sex", "intercourse", "making love", "intimate", "moaning", "passion", "sensual", "erotic" };
    private static readonly string[] BriefNudityKeywords = { "brief", "quick", "fleeting", "glimpse", "blink", "moment", "background", "distant", "unclear" };

    // IMDB ID format: "tt" followed by 7-8 digits
    private static readonly Regex ImdbIdRegex = new(@"^tt\d{7,8}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public NudityTaggingTask(ILogger<NudityTaggingTask> logger, ILibraryManager libraryManager, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        // Set timeout to prevent resource exhaustion (30 seconds)
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public string Name => "Tag Nudity Content";
    public string Description => "Scans library items and applies nudity/sexual content tags based on IMDB Parents Guide.";
    public string Category => "Nudity Tagger";
    public string Key => "NudityTaggerTask";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnablePlugin)
        {
            _logger.LogInformation("Nudity Tagger plugin is disabled, skipping task");
            return;
        }

        // Validate and sanitize configuration values
        config = ValidateAndSanitizeConfig(config);

        _logger.LogInformation("Starting Nudity Tagging Task");

        var items = GetTaggableItems().ToList();
        var processedImdbIds = new HashSet<string>();
        var cachePath = Plugin.Instance?.GetCachePath() ?? Path.GetTempPath();

        int total = items.Count, processed = 0, tagged = 0, skipped = 0, failed = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                processed++;
                progress.Report((double)processed / total * 100);

                if (config.SkipAlreadyTagged && HasNudityTags(item, config.TagPrefix))
                {
                    skipped++;
                    continue;
                }

                var imdbId = GetImdbId(item);
                if (string.IsNullOrEmpty(imdbId))
                {
                    skipped++;
                    continue;
                }

                if (item is Episode && processedImdbIds.Contains(imdbId))
                {
                    skipped++;
                    continue;
                }

                var parentsGuide = await FetchParentsGuideAsync(imdbId, cachePath, config.CacheDurationHours, cancellationToken);
                if (parentsGuide == null)
                {
                    _logger.LogWarning("Could not fetch Parents Guide for: {Name} ({ImdbId})", item.Name, imdbId);
                    failed++;
                    continue;
                }

                var tags = DetermineTags(parentsGuide, config.MinimumSeverityToTag, config.TagPrefix);
                if (tags.Count > 0)
                {
                    await ApplyTagsAsync(item, tags, config.TagPrefix, config.SetTagline, cancellationToken);
                    tagged++;
                    processedImdbIds.Add(imdbId);
                }
                else
                {
                    skipped++;
                }

                await Task.Delay(config.RequestDelayMs, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing item: {Name}", item.Name);
                failed++;
            }
        }

        _logger.LogInformation("Nudity Tagging Task completed. Processed: {Processed}, Tagged: {Tagged}, Skipped: {Skipped}, Failed: {Failed}",
            processed, tagged, skipped, failed);
    }

    private IEnumerable<BaseItem> GetTaggableItems()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true
        };
        return _libraryManager.GetItemList(query);
    }

    private PluginConfiguration ValidateAndSanitizeConfig(PluginConfiguration config)
    {
        // Validate CacheDurationHours (1 hour to 1 year)
        if (config.CacheDurationHours < 1 || config.CacheDurationHours > 8760)
        {
            _logger.LogWarning("Invalid CacheDurationHours {Value}, clamping to valid range", config.CacheDurationHours);
            config.CacheDurationHours = Math.Clamp(config.CacheDurationHours, 1, 8760);
        }

        // Validate RequestDelayMs (500ms to 60 seconds)
        if (config.RequestDelayMs < 500 || config.RequestDelayMs > 60000)
        {
            _logger.LogWarning("Invalid RequestDelayMs {Value}, clamping to valid range", config.RequestDelayMs);
            config.RequestDelayMs = Math.Clamp(config.RequestDelayMs, 500, 60000);
        }

        // Validate MinimumSeverityToTag
        var validSeverities = new[] { "None", "Mild", "Moderate", "Severe" };
        if (string.IsNullOrWhiteSpace(config.MinimumSeverityToTag) || 
            !validSeverities.Contains(config.MinimumSeverityToTag, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Invalid MinimumSeverityToTag {Value}, defaulting to Mild", config.MinimumSeverityToTag);
            config.MinimumSeverityToTag = "Mild";
        }

        // Sanitize TagPrefix - limit length and remove dangerous characters
        if (config.TagPrefix != null)
        {
            const int maxPrefixLength = 50;
            config.TagPrefix = config.TagPrefix.Trim();
            if (config.TagPrefix.Length > maxPrefixLength)
            {
                _logger.LogWarning("TagPrefix too long, truncating to {MaxLength} characters", maxPrefixLength);
                config.TagPrefix = config.TagPrefix.Substring(0, maxPrefixLength);
            }
        }
        else
        {
            config.TagPrefix = string.Empty;
        }

        return config;
    }

    private static string? GetImdbId(BaseItem item)
    {
        if (item.TryGetProviderId(MetadataProvider.Imdb, out var imdbId))
            return imdbId;

        if (item is Episode episode && episode.Series?.TryGetProviderId(MetadataProvider.Imdb, out var seriesId) == true)
            return seriesId;

        return null;
    }

    private static bool HasNudityTags(BaseItem item, string prefix)
    {
        if (item.Tags == null || item.Tags.Length == 0) return false;
        var allTags = NudityCategory.AllCategories.Select(c => prefix + c).Concat(NudityCategory.AllCategories).ToHashSet();
        return item.Tags.Any(t => allTags.Contains(t));
    }

    private async Task<ParentsGuideData?> FetchParentsGuideAsync(string imdbId, string cachePath, int cacheDurationHours, CancellationToken ct)
    {
        // Validate and sanitize IMDB ID to prevent path traversal
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            _logger.LogWarning("Empty or null IMDB ID provided");
            return null;
        }

        // Normalize IMDB ID format
        imdbId = imdbId.Trim();
        if (!imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            // Remove "tt" prefix if present and re-add, or add if missing
            imdbId = imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) 
                ? imdbId 
                : "tt" + imdbId;
        }

        // Validate IMDB ID format (tt followed by 7-8 digits) to prevent injection
        if (!ImdbIdRegex.IsMatch(imdbId))
        {
            _logger.LogWarning("Invalid IMDB ID format: {ImdbId}", imdbId);
            return null;
        }

        // Sanitize filename - only allow alphanumeric characters and ensure it's safe
        // IMDB IDs are already validated above, but double-check for path traversal
        var sanitizedId = imdbId.Replace("..", "").Replace("/", "").Replace("\\", "");
        if (sanitizedId != imdbId)
        {
            _logger.LogWarning("IMDB ID contained unsafe characters: {ImdbId}", imdbId);
            return null;
        }

        // Check cache - use sanitized ID for file path
        var cacheFile = Path.Combine(cachePath, $"{sanitizedId}.json");
        
        // Additional security: Ensure the resolved path is still within cachePath
        var resolvedPath = Path.GetFullPath(cacheFile);
        var resolvedCachePath = Path.GetFullPath(cachePath);
        if (!resolvedPath.StartsWith(resolvedCachePath, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("Path traversal detected! Cache file path outside cache directory: {Path}", resolvedPath);
            return null;
        }
        if (File.Exists(cacheFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(cacheFile, ct);
                var cached = JsonSerializer.Deserialize<ParentsGuideData>(json);
                if (cached != null && DateTime.UtcNow - cached.FetchedAt < TimeSpan.FromHours(cacheDurationHours))
                    return cached;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading cache file for {ImdbId}", sanitizedId);
                // Continue to fetch from web if cache read fails
            }
        }

        try
        {
            // Use Uri constructor to properly encode the URL and prevent injection
            var baseUri = new Uri("https://www.imdb.com/title/");
            var fullUri = new Uri(baseUri, sanitizedId + "/parentalguide");
            var response = await _httpClient.GetAsync(fullUri, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var data = ParseParentsGuide(html, sanitizedId);

            if (data != null)
            {
                try
                {
                    var json = JsonSerializer.Serialize(data);
                    await File.WriteAllTextAsync(cacheFile, json, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error writing cache file for {ImdbId}", sanitizedId);
                    // Continue even if cache write fails
                }
            }

            return data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Parents Guide for {ImdbId}", imdbId);
            return null;
        }
    }

    private ParentsGuideData ParseParentsGuide(string html, string imdbId)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var data = new ParentsGuideData { ImdbId = imdbId, FetchedAt = DateTime.UtcNow };

        // Find Sex & Nudity section
        var nuditySection = doc.DocumentNode.SelectSingleNode("//section[@id='advisory-nudity']")
            ?? doc.DocumentNode.SelectSingleNode("//*[contains(@id, 'nudity')]");

        if (nuditySection == null)
        {
            var sections = doc.DocumentNode.SelectNodes("//section");
            nuditySection = sections?.FirstOrDefault(s =>
                s.SelectSingleNode(".//h3 | .//h2")?.InnerText?.Contains("Sex", StringComparison.OrdinalIgnoreCase) == true);
        }

        if (nuditySection != null)
        {
            // Extract severity
            var severityNode = nuditySection.SelectSingleNode(".//*[contains(text(), 'Mild') or contains(text(), 'Moderate') or contains(text(), 'Severe') or contains(text(), 'None')]");
            if (severityNode != null)
            {
                var text = severityNode.InnerText.ToLowerInvariant();
                data.Severity = text.Contains("severe") ? SeverityLevel.Severe
                    : text.Contains("moderate") ? SeverityLevel.Moderate
                    : text.Contains("mild") ? SeverityLevel.Mild
                    : text.Contains("none") ? SeverityLevel.None
                    : SeverityLevel.Unknown;
            }

            // Extract descriptions
            var listItems = nuditySection.SelectNodes(".//li");
            if (listItems != null)
            {
                foreach (var li in listItems)
                {
                    var text = HtmlEntity.DeEntitize(li.InnerText).Trim();
                    if (text.Length > 10 && !text.StartsWith("Edit"))
                        data.Descriptions.Add(text);
                }
            }
        }

        return data;
    }

    private static List<string> DetermineTags(ParentsGuideData data, string minSeverity, string prefix)
    {
        var tags = new List<string>();
        var minLevel = minSeverity.ToLowerInvariant() switch
        {
            "none" => SeverityLevel.None,
            "moderate" => SeverityLevel.Moderate,
            "severe" => SeverityLevel.Severe,
            _ => SeverityLevel.Mild
        };

        if (data.Severity < minLevel)
        {
            if (data.Severity == SeverityLevel.None)
                tags.Add(prefix + NudityCategory.NoNudity);
            return tags;
        }

        var allDesc = string.Join(" ", data.Descriptions).ToLowerInvariant();
        bool hasFullNudity = FullNudityKeywords.Any(k => allDesc.Contains(k));
        bool hasGraphicSex = GraphicSexualKeywords.Any(k => allDesc.Contains(k));
        bool hasSexContent = SexualContentKeywords.Any(k => allDesc.Contains(k));
        bool isBrief = BriefNudityKeywords.Any(k => allDesc.Contains(k));

        switch (data.Severity)
        {
            case SeverityLevel.Severe:
                if (hasGraphicSex) tags.Add(prefix + NudityCategory.GraphicSexualContent);
                tags.Add(prefix + NudityCategory.FullNudity);
                break;
            case SeverityLevel.Moderate:
                if (hasSexContent) tags.Add(prefix + NudityCategory.SexualContent);
                tags.Add(prefix + (hasFullNudity ? NudityCategory.FullNudity : NudityCategory.PartialNudity));
                break;
            case SeverityLevel.Mild:
                tags.Add(prefix + (isBrief || !hasSexContent ? NudityCategory.BriefNudity : NudityCategory.SexualContent));
                break;
            case SeverityLevel.None:
                tags.Add(prefix + NudityCategory.NoNudity);
                break;
        }

        return tags.Distinct().ToList();
    }

    private async Task ApplyTagsAsync(BaseItem item, List<string> newTags, string prefix, bool setTagline, CancellationToken ct)
    {
        // Validate and sanitize tags - limit length to prevent DoS
        const int maxTagLength = 100;
        var sanitizedTags = newTags
            .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length <= maxTagLength)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (sanitizedTags.Count == 0)
        {
            _logger.LogWarning("No valid tags to apply for item {ItemName}", item.Name);
            return;
        }

        var currentTags = item.Tags?.ToList() ?? new List<string>();
        var allNudityTags = NudityCategory.AllCategories.Select(c => prefix + c).Concat(NudityCategory.AllCategories).ToHashSet();
        currentTags.RemoveAll(t => allNudityTags.Contains(t));
        currentTags.AddRange(sanitizedTags);
        item.Tags = currentTags.Distinct().ToArray();

        // Set tagline for prominent display (shows under title) - Movies only
        if (setTagline && item is Movie movie)
        {
            var contentWarning = "⚠️ " + string.Join(", ", sanitizedTags.Select(t => t.Replace(prefix, "")));
            // Limit tagline length to prevent DoS
            movie.Tagline = contentWarning.Length > 500 ? contentWarning.Substring(0, 497) + "..." : contentWarning;
        }

        // For series, prepend to overview
        if (setTagline && item is Series series)
        {
            var contentWarning = "⚠️ Content Warning: " + string.Join(", ", sanitizedTags.Select(t => t.Replace(prefix, "")));
            // Limit content warning length
            var limitedWarning = contentWarning.Length > 200 ? contentWarning.Substring(0, 197) + "..." : contentWarning;
            if (series.Overview != null && !series.Overview.StartsWith("⚠️"))
            {
                series.Overview = limitedWarning + "\n\n" + series.Overview;
            }
            else if (series.Overview == null)
            {
                series.Overview = limitedWarning;
            }
        }

        // Save the item to repository - this persists tags to the database
        // The tags should display after a library refresh or item reload in Jellyfin
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
        _logger.LogInformation("Applied tags {Tags} to {ItemName}", string.Join(", ", sanitizedTags), item.Name);
    }
}
