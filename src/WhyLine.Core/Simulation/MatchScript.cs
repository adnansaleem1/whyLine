using WhyLine.Core.Domain;

namespace WhyLine.Core.Simulation;

public enum BeatKind
{
    /// <summary>Team switches tactical profile (e.g. manager orders a high press).</summary>
    ProfileChange,

    /// <summary>The team will create a chance for <see cref="StorylineBeat.PlayerId"/> soon after this minute.</summary>
    Chance
}

/// <summary>A scripted storyline beat. Storylines guarantee the demo always has something worth explaining.</summary>
public sealed record StorylineBeat(
    double AtMinute,
    BeatKind Kind,
    TeamSide Team,
    TacticalProfile? NewProfile = null,
    int? PlayerId = null,
    bool Scores = false,
    double ChanceXg = 0.35,
    string? Note = null);

public sealed record MatchScript(MatchMetadata Metadata, IReadOnlyList<StorylineBeat> Beats, bool AllowUnscriptedGoals = false)
{
    /// <summary>The original demo match: Harbour United's press flips the game (1-2).</summary>
    public static MatchScript Default(int seed = 7) => MatchCatalog.Build(MatchCatalog.DefaultId, seed);
}

/// <summary>
/// A scripted fixture the Studio can load: two fictional clubs and a storyline. <see cref="Story"/> is a spoiler-free
/// teaser shown to viewers; <see cref="ExpectedScore"/> is the scripted result, used by tests and never shown.
/// </summary>
public sealed record MatchScenario(string Id, string Title, string Story, string ExpectedScore, Func<int, MatchScript> Build);

/// <summary>
/// The fixtures WhyLine can play. Each one stresses a different part of the newsroom: a tactical swing, a comeback,
/// a goalless chess match, a smash-and-grab and an end-to-end thriller. All clubs and players are fictional.
/// </summary>
public static class MatchCatalog
{
    public const string DefaultId = "press-flip";

    public static IReadOnlyList<MatchScenario> All { get; } =
    [
        new(DefaultId, "Northbridge City v Harbour United", "Possession against a low block, and an away manager with a plan for the second half.", "1-2",
            seed => PressFlip(seed)),
        new("comeback", "Ironvale Rovers v Saltmere Athletic", "Ironvale love the ball; Saltmere love the space behind it. Patience against pace.", "3-2",
            seed => Comeback(seed)),
        new("stalemate", "Larkhaven FC v Eastmarsh Albion", "A possession side against a deep, disciplined block. Who blinks first?", "0-0",
            seed => Stalemate(seed)),
        new("smash-and-grab", "Corvane City v Duskmoor United", "Corvane press everything that moves; Duskmoor are happy to wait for their moment.", "0-1",
            seed => SmashAndGrab(seed)),
        new("thriller", "Halden Forge v Marrow Bay", "Two pressing sides and nobody sitting back. Expect it to be open.", "3-3",
            seed => Thriller(seed))
    ];

    public static MatchScenario ById(string? id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static MatchScript Build(string? id, int seed) => ById(id).Build(seed);

    private static StorylineBeat Goal(MatchMetadata m, double minute, TeamSide team, string pos, double xg, string note) =>
        new(minute, BeatKind.Chance, team, PlayerId: Slot(m, team, pos), Scores: true, ChanceXg: xg, Note: note);

    private static StorylineBeat Miss(MatchMetadata m, double minute, TeamSide team, string pos, double xg, string note) =>
        new(minute, BeatKind.Chance, team, PlayerId: Slot(m, team, pos), Scores: false, ChanceXg: xg, Note: note);

    private static StorylineBeat Switch(MatchMetadata m, double minute, TeamSide team, TacticalProfile profile, string note) =>
        new(minute, BeatKind.ProfileChange, team, NewProfile: profile, Note: $"{m.TeamOf(team).Name} {note}");

    private static int Slot(MatchMetadata m, TeamSide team, string pos) => m.Players.First(p => p.Team == team && p.Position == pos).Id;

    private static MatchScript PressFlip(int seed)
    {
        var m = SquadFactory.CreateDefaultMatch(seed);
        return new MatchScript(m,
        [
            Goal(m, 22, TeamSide.Home, "ST", 0.38, "Home's patient build-up finally opens the low block"),
            Switch(m, 52, TeamSide.Away, TacticalProfile.HighPress, "switch to a high press"),
            Goal(m, 60, TeamSide.Away, "RW", 0.31, "Press forces a turnover high up the pitch"),
            Miss(m, 76, TeamSide.Home, "LW", 0.47, "Big chance missed"),
            Goal(m, 86, TeamSide.Away, "ST", 0.28, "Quiet striker finally strikes")
        ]);
    }

    private static MatchScript Comeback(int seed)
    {
        var m = SquadFactory.CreateMatch(seed,
            new ClubSpec("Ironvale Rovers", "IVR", "#a3e635", "#365314", TacticalProfile.Possession),
            new ClubSpec("Saltmere Athletic", "SAL", "#e879f9", "#701a75", TacticalProfile.Counter),
            "Ironvale Park", $"WL-CB-{seed:0000}");
        return new MatchScript(m,
        [
            Goal(m, 14, TeamSide.Away, "ST", 0.42, "Counter catches Ironvale high up the pitch"),
            Goal(m, 37, TeamSide.Away, "RW", 0.33, "Second counter, same space behind the full-back"),
            Switch(m, 50, TeamSide.Home, TacticalProfile.HighPress, "throw men forward with a high press"),
            Goal(m, 58, TeamSide.Home, "ST", 0.36, "Press wins it back on the edge of the box"),
            Goal(m, 73, TeamSide.Home, "LW", 0.29, "Ironvale level it"),
            Miss(m, 80, TeamSide.Away, "ST", 0.52, "Saltmere waste the chance to restore the lead"),
            Goal(m, 89, TeamSide.Home, "RW", 0.31, "Stoppage-time winner completes the comeback")
        ]);
    }

    private static MatchScript Stalemate(int seed)
    {
        var m = SquadFactory.CreateMatch(seed,
            new ClubSpec("Larkhaven FC", "LRK", "#facc15", "#713f12", TacticalProfile.Possession),
            new ClubSpec("Eastmarsh Albion", "EMA", "#60a5fa", "#1e3a8a", TacticalProfile.LowBlock),
            "Larkhaven Ground", $"WL-ST-{seed:0000}");
        return new MatchScript(m,
        [
            Miss(m, 18, TeamSide.Home, "ST", 0.45, "First clear chance of the game goes wide"),
            Miss(m, 33, TeamSide.Away, "ST", 0.22, "Rare Eastmarsh break, comfortable save"),
            Switch(m, 58, TeamSide.Home, TacticalProfile.HighPress, "push their press higher to force an opening"),
            Miss(m, 67, TeamSide.Home, "RW", 0.52, "Best chance of the match, kept out"),
            Switch(m, 75, TeamSide.Away, TacticalProfile.Counter, "start to break with more men"),
            Miss(m, 84, TeamSide.Away, "LW", 0.40, "Late Eastmarsh chance on the counter")
        ]);
    }

    private static MatchScript SmashAndGrab(int seed)
    {
        var m = SquadFactory.CreateMatch(seed,
            new ClubSpec("Corvane City", "COR", "#f43f5e", "#881337", TacticalProfile.HighPress),
            new ClubSpec("Duskmoor United", "DSK", "#2dd4bf", "#134e4a", TacticalProfile.Counter),
            "Corvane Arena", $"WL-SG-{seed:0000}");
        return new MatchScript(m,
        [
            Miss(m, 27, TeamSide.Home, "LW", 0.40, "Corvane's first big chance, saved"),
            Miss(m, 41, TeamSide.Home, "ST", 0.33, "Another chance spurned before the break"),
            Switch(m, 55, TeamSide.Away, TacticalProfile.LowBlock, "drop into a deep low block"),
            Miss(m, 71, TeamSide.Home, "ST", 0.55, "Open goal chance missed"),
            Goal(m, 87, TeamSide.Away, "RW", 0.24, "One counter, one goal: Duskmoor steal it")
        ]);
    }

    private static MatchScript Thriller(int seed)
    {
        var m = SquadFactory.CreateMatch(seed,
            new ClubSpec("Halden Forge", "HAL", "#fb923c", "#431407", TacticalProfile.HighPress),
            new ClubSpec("Marrow Bay", "MAR", "#c4b5fd", "#3b0764", TacticalProfile.HighPress),
            "Forge Lane", $"WL-TH-{seed:0000}");
        return new MatchScript(m,
        [
            Goal(m, 9, TeamSide.Home, "ST", 0.41, "Early goal from a high turnover"),
            Goal(m, 21, TeamSide.Away, "ST", 0.35, "Marrow Bay hit straight back"),
            Goal(m, 40, TeamSide.Away, "LW", 0.30, "Marrow Bay ahead before the break"),
            Goal(m, 47, TeamSide.Home, "RW", 0.37, "Halden level straight after half-time"),
            Switch(m, 60, TeamSide.Away, TacticalProfile.Counter, "sit off and look to counter"),
            Goal(m, 66, TeamSide.Home, "LW", 0.32, "Halden in front again"),
            Goal(m, 89, TeamSide.Away, "RW", 0.26, "Late equaliser on the break")
        ]);
    }
}
