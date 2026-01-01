namespace Jellyfin.Plugin.NudityTagger.Models;

public static class NudityCategory
{
    public const string NoNudity = "No Nudity";
    public const string BriefNudity = "Brief Nudity";
    public const string PartialNudity = "Partial Nudity";
    public const string FullNudity = "Full Nudity";
    public const string SexualContent = "Sexual Content";
    public const string GraphicSexualContent = "Graphic Sexual Content";

    public static readonly string[] AllCategories = new[]
    {
        NoNudity,
        BriefNudity,
        PartialNudity,
        FullNudity,
        SexualContent,
        GraphicSexualContent
    };
}
