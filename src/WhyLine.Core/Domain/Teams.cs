namespace WhyLine.Core.Domain;

/// <summary>How a team behaves. All values are 0..1.</summary>
public sealed record TacticalProfile(
    string Name,
    double PressHeight,
    double PressIntensity,
    double Directness,
    double Tempo,
    double PassingQuality)
{
    public static TacticalProfile HighPress => new("High press", 0.85, 0.85, 0.45, 0.75, 0.78);
    public static TacticalProfile Possession => new("Possession", 0.6, 0.55, 0.2, 0.45, 0.88);
    public static TacticalProfile LowBlock => new("Low block", 0.2, 0.35, 0.7, 0.55, 0.72);
    public static TacticalProfile Counter => new("Counter-attack", 0.3, 0.5, 0.85, 0.85, 0.74);
}

public sealed record Player(int Id, string Name, int Number, TeamSide Team, string Position, double Pace, double Technique)
{
    public string ShortName => Name.Contains(' ') ? Name[(Name.LastIndexOf(' ') + 1)..] : Name;
}

public sealed record Team(TeamSide Side, string Name, string ShortName, string PrimaryColor, string SecondaryColor, TacticalProfile Profile);

public sealed record MatchMetadata(
    string MatchId,
    string Competition,
    string Venue,
    Team Home,
    Team Away,
    IReadOnlyList<Player> Players,
    int Seed)
{
    public Team TeamOf(TeamSide side) => side == TeamSide.Home ? Home : Away;
    public Player? PlayerById(int? id) => id is null ? null : Players.FirstOrDefault(p => p.Id == id);
}
