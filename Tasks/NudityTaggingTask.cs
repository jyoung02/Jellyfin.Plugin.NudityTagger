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

    // Keywords for categorizing content - using HashSet for O(1) lookup performance
    private static readonly HashSet<string> FullNudityKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "full frontal", "fully nude", "completely nude", "full nudity", "genitalia", "genital", "penis", "vagina", "pubic",
        "naked", "nude", "nudity", "bare", "exposed", "topless", "bottomless"
    };
    
    private static readonly HashSet<string> GraphicSexualKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "graphic sex", "explicit sex", "sex scene", "sexual intercourse", "thrusting", "orgasm", "ejaculation", "masturbation",
        "pornographic", "hardcore", "explicit", "simulated sex", "sex act"
    };
    
    private static readonly HashSet<string> SexualContentKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "sexual", "sex", "intercourse", "making love", "intimate", "moaning", "passion", "sensual", "erotic",
        "seduction", "foreplay", "kissing", "caressing", "undressing", "bedroom", "romance"
    };
    
    private static readonly HashSet<string> BriefNudityKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "brief", "quick", "fleeting", "glimpse", "blink", "moment", "background", "distant", "unclear",
        "partial", "suggested", "implied", "shadow", "silhouette"
    };

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

        // Clean up old cache files if enabled
        if (config.EnableCacheCleanup)
        {
            CleanupOldCacheFiles(cachePath, config.CacheDurationHours);
        }

        int total = items.Count, processed = 0, tagged = 0, skipped = 0, failed = 0;
        
        _logger.LogInformation("Found {Count} items to process", total);

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

                var parentsGuide = await FetchParentsGuideAsync(imdbId, cachePath, config.CacheDurationHours, config.MaxRetryAttempts, cancellationToken);
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

        // Validate MaxRetryAttempts (1 to 10)
        if (config.MaxRetryAttempts < 1 || config.MaxRetryAttempts > 10)
        {
            _logger.LogWarning("Invalid MaxRetryAttempts {Value}, clamping to valid range", config.MaxRetryAttempts);
            config.MaxRetryAttempts = Math.Clamp(config.MaxRetryAttempts, 1, 10);
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

    private void CleanupOldCacheFiles(string cachePath, int cacheDurationHours)
    {
        try
        {
            if (!Directory.Exists(cachePath)) return;

            var cutoffTime = DateTime.UtcNow.AddHours(-cacheDurationHours * 2); // Clean files older than 2x cache duration
            var files = Directory.GetFiles(cachePath, "*.json");
            var deletedCount = 0;

            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTimeUtc < cutoffTime)
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error deleting old cache file: {File}", file);
                }
            }

            if (deletedCount > 0)
            {
                _logger.LogInformation("Cleaned up {Count} old cache files", deletedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during cache cleanup");
        }
    }

    private async Task<ParentsGuideData?> FetchParentsGuideAsync(string imdbId, string cachePath, int cacheDurationHours, int maxRetries, CancellationToken ct)
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

        // Retry logic for HTTP requests
        Exception? lastException = null;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Use Uri constructor to properly encode the URL and prevent injection
                var baseUri = new Uri("https://www.imdb.com/title/");
                var fullUri = new Uri(baseUri, sanitizedId + "/parentalguide");
                
                if (attempt > 1)
                {
                    _logger.LogInformation("Retry attempt {Attempt}/{MaxRetries} for {ImdbId}", attempt, maxRetries, sanitizedId);
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct); // Exponential backoff
                }

                var response = await _httpClient.GetAsync(fullUri, ct);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Parents Guide not found for {ImdbId}", sanitizedId);
                    return null;
                }
                
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
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                lastException = ex;
                _logger.LogWarning(ex, "HTTP request failed for {ImdbId} (attempt {Attempt}/{MaxRetries})", sanitizedId, attempt, maxRetries);
                // Continue to retry
            }
            catch (TaskCanceledException ex) when (attempt < maxRetries)
            {
                lastException = ex;
                _logger.LogWarning(ex, "Request timeout for {ImdbId} (attempt {Attempt}/{MaxRetries})", sanitizedId, attempt, maxRetries);
                // Continue to retry
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Parents Guide for {ImdbId}", sanitizedId);
                return null;
            }
        }

        _logger.LogError(lastException, "Failed to fetch Parents Guide for {ImdbId} after {MaxRetries} attempts", sanitizedId, maxRetries);
        return null;
    }

    private ParentsGuideData ParseParentsGuide(string html, string imdbId)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var data = new ParentsGuideData { ImdbId = imdbId, FetchedAt = DateTime.UtcNow };

        // Multiple strategies to find Sex & Nudity section (IMDB HTML structure can vary)
        HtmlNode? nuditySection = null;

        // Strategy 1: Look for section with id="advisory-nudity"
        nuditySection = doc.DocumentNode.SelectSingleNode("//section[@id='advisory-nudity']");

        // Strategy 2: Look for any element with "nudity" in the id
        if (nuditySection == null)
        {
            nuditySection = doc.DocumentNode.SelectNodes("//section | //div")
                ?.FirstOrDefault(s => s.GetAttributeValue("id", "").Contains("nudity", StringComparison.OrdinalIgnoreCase));
        }

        // Strategy 3: Look for section/div with heading containing "Sex" or "Nudity"
        if (nuditySection == null)
        {
            var allSections = doc.DocumentNode.SelectNodes("//section | //div[@class*='advisory'] | //div[@class*='parental']");
            if (allSections != null)
            {
                foreach (var section in allSections)
                {
                    var heading = section.SelectSingleNode(".//h2 | .//h3 | .//h4 | .//span[@class*='heading'] | .//div[@class*='heading']");
                    if (heading != null)
                    {
                        var headingText = heading.InnerText?.ToLowerInvariant() ?? "";
                        if (headingText.Contains("sex", StringComparison.OrdinalIgnoreCase) ||
                            headingText.Contains("nudity", StringComparison.OrdinalIgnoreCase))
                        {
                            nuditySection = section;
                            break;
                        }
                    }
                }
            }
        }

        // Strategy 4: Look for text content containing "Sex & Nudity" or similar
        if (nuditySection == null)
        {
            var nodesWithText = doc.DocumentNode.SelectNodes("//*[contains(text(), 'Sex & Nudity') or contains(text(), 'Sex and Nudity')]");
            if (nodesWithText != null && nodesWithText.Count > 0)
            {
                // Find the parent section/div
                var parent = nodesWithText[0].ParentNode;
                while (parent != null && parent.Name != "section" && parent.Name != "div")
                {
                    parent = parent.ParentNode;
                }
                nuditySection = parent;
            }
        }

        if (nuditySection != null)
        {
            // Extract severity with multiple strategies
            var severityText = "";
            
            // Look for severity indicators in various formats
            var severityNodes = nuditySection.SelectNodes(".//*[contains(@class, 'severity')] | .//*[contains(@class, 'rating')] | .//span | .//div");
            if (severityNodes != null)
            {
                foreach (var node in severityNodes)
                {
                    var text = node.InnerText?.ToLowerInvariant() ?? "";
                    if (text.Contains("severe") || text.Contains("moderate") || text.Contains("mild") || text.Contains("none"))
                    {
                        severityText = text;
                        break;
                    }
                }
            }

            // Fallback: search all text in section
            if (string.IsNullOrEmpty(severityText))
            {
                var allText = nuditySection.InnerText?.ToLowerInvariant() ?? "";
                if (allText.Contains("severe")) severityText = "severe";
                else if (allText.Contains("moderate")) severityText = "moderate";
                else if (allText.Contains("mild")) severityText = "mild";
                else if (allText.Contains("none")) severityText = "none";
            }

            // Parse severity
            if (!string.IsNullOrEmpty(severityText))
            {
                data.Severity = severityText.Contains("severe") ? SeverityLevel.Severe
                    : severityText.Contains("moderate") ? SeverityLevel.Moderate
                    : severityText.Contains("mild") ? SeverityLevel.Mild
                    : severityText.Contains("none") ? SeverityLevel.None
                    : SeverityLevel.Unknown;
            }

            // Extract descriptions with better filtering
            var listItems = nuditySection.SelectNodes(".//li | .//p | .//div[@class*='item']");
            if (listItems != null)
            {
                foreach (var item in listItems)
                {
                    var text = HtmlEntity.DeEntitize(item.InnerText ?? "").Trim();
                    // Filter out short text, edit links, and navigation elements
                    if (text.Length > 15 && 
                        !text.StartsWith("Edit", StringComparison.OrdinalIgnoreCase) &&
                        !text.StartsWith("Add", StringComparison.OrdinalIgnoreCase) &&
                        !text.Contains("See more", StringComparison.OrdinalIgnoreCase) &&
                        !text.Contains("See less", StringComparison.OrdinalIgnoreCase))
                    {
                        data.Descriptions.Add(text);
                    }
                }
            }
        }
        else
        {
            _logger.LogWarning("Could not find Sex & Nudity section in HTML for {ImdbId}", imdbId);
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

        // Optimized keyword matching: check full text for phrases, then use HashSet for single words
        var allDesc = string.Join(" ", data.Descriptions).ToLowerInvariant();
        var descWords = new HashSet<string>(allDesc.Split(new[] { ' ', '\n', '\r', '\t', '.', ',', '!', '?', ';', ':' }, 
            StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        
        // Check for multi-word phrases in full text, then single words using HashSet
        bool hasFullNudity = FullNudityKeywords.Any(k => k.Contains(' ') ? allDesc.Contains(k, StringComparison.OrdinalIgnoreCase) : descWords.Contains(k));
        bool hasGraphicSex = GraphicSexualKeywords.Any(k => k.Contains(' ') ? allDesc.Contains(k, StringComparison.OrdinalIgnoreCase) : descWords.Contains(k));
        bool hasSexContent = SexualContentKeywords.Any(k => k.Contains(' ') ? allDesc.Contains(k, StringComparison.OrdinalIgnoreCase) : descWords.Contains(k));
        bool isBrief = BriefNudityKeywords.Any(k => k.Contains(' ') ? allDesc.Contains(k, StringComparison.OrdinalIgnoreCase) : descWords.Contains(k));

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
