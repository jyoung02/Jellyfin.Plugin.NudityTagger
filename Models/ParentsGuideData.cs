namespace Jellyfin.Plugin.NudityTagger.Models;

public class ParentsGuideData
{
    public string ImdbId { get; set; } = string.Empty;
    public SeverityLevel Severity { get; set; } = SeverityLevel.Unknown;
    public List<string> Descriptions { get; set; } = new();
    public int VotesNone { get; set; }
    public int VotesMild { get; set; }
    public int VotesModerate { get; set; }
    public int VotesSevere { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

public enum SeverityLevel
{
    Unknown = 0,
    None = 1,
    Mild = 2,
    Moderate = 3,
    Severe = 4
}
