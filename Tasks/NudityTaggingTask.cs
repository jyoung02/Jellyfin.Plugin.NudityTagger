using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Jellyfin.Data.Enums;
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

    public NudityTaggingTask(ILogger<NudityTaggingTask> logger, ILibraryManager libraryManager, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
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
                    await ApplyTagsAsync(item, tags, config.TagPrefix, cancellationToken);
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
        if (!imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            imdbId = "tt" + imdbId;

        // Check cache
        var cacheFile = Path.Combine(cachePath, $"{imdbId}.json");
        if (File.Exists(cacheFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(cacheFile, ct);
                var cached = JsonSerializer.Deserialize<ParentsGuideData>(json);
                if (cached != null && DateTime.UtcNow - cached.FetchedAt < TimeSpan.FromHours(cacheDurationHours))
                    return cached;
            }
            catch { /* ignore cache errors */ }
        }

        try
        {
            var url = $"https://www.imdb.com/title/{imdbId}/parentalguide";
            var response = await _httpClient.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var data = ParseParentsGuide(html, imdbId);

            if (data != null)
            {
                try
                {
                    var json = JsonSerializer.Serialize(data);
                    await File.WriteAllTextAsync(cacheFile, json, ct);
                }
                catch { /* ignore cache write errors */ }
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

    private async Task ApplyTagsAsync(BaseItem item, List<string> newTags, string prefix, CancellationToken ct)
    {
        var currentTags = item.Tags?.ToList() ?? new List<string>();
        var allNudityTags = NudityCategory.AllCategories.Select(c => prefix + c).Concat(NudityCategory.AllCategories).ToHashSet();
        currentTags.RemoveAll(t => allNudityTags.Contains(t));
        currentTags.AddRange(newTags);
        item.Tags = currentTags.Distinct().ToArray();
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
        _logger.LogInformation("Applied tags {Tags} to {ItemName}", string.Join(", ", newTags), item.Name);
    }
}
