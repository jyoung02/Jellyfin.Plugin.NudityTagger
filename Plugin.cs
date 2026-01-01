using System.Reflection;
using Jellyfin.Plugin.NudityTagger.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.NudityTagger;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Nudity Tagger";

    public override string Description => "Tags movies and TV shows with nudity/sexual content warnings from IMDB Parents Guide.";

    public override Guid Id => Guid.Parse("a5b6c7d8-e9f0-1234-5678-9abcdef01234");

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }

    public string GetCachePath()
    {
        var cachePath = Path.Combine(ApplicationPaths.CachePath, "nuditytagger");
        Directory.CreateDirectory(cachePath);
        return cachePath;
    }
}
