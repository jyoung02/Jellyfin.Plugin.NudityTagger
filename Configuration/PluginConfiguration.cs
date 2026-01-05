using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.NudityTagger.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        EnablePlugin = true;
        MinimumSeverityToTag = "Mild";
        TagPrefix = "";
        CacheDurationHours = 168; // 1 week
        RequestDelayMs = 2000;
        SkipAlreadyTagged = true;
        SetTagline = true;
        MaxRetryAttempts = 3;
        EnableCacheCleanup = true;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin is enabled.
    /// </summary>
    public bool EnablePlugin { get; set; }

    /// <summary>
    /// Gets or sets the minimum severity level to apply tags.
    /// Options: None, Mild, Moderate, Severe
    /// </summary>
    public string MinimumSeverityToTag { get; set; }

    /// <summary>
    /// Gets or sets an optional prefix for tags (e.g., "Content: ").
    /// </summary>
    public string TagPrefix { get; set; }

    /// <summary>
    /// Gets or sets how long to cache IMDB results in hours.
    /// </summary>
    public int CacheDurationHours { get; set; }

    /// <summary>
    /// Gets or sets the delay between IMDB requests in milliseconds.
    /// </summary>
    public int RequestDelayMs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to skip items that already have nudity tags.
    /// </summary>
    public bool SkipAlreadyTagged { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to set the tagline with content warnings.
    /// </summary>
    public bool SetTagline { get; set; }

    /// <summary>
    /// Gets or sets the number of retry attempts for failed HTTP requests.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Gets or sets whether to clean up old cache files automatically.
    /// </summary>
    public bool EnableCacheCleanup { get; set; } = true;
}
