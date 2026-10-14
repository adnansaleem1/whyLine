using WhyLine.Core.Domain;

namespace WhyLine.Core.Simulation;

/// <summary>A fictional club: name, three-letter code, kit colours and starting tactical profile.</summary>
public sealed record ClubSpec(string Name, string Code, string PrimaryColor, string SecondaryColor, TacticalProfile Profile,
    IReadOnlyList<string>? PlayerNames = null);

/// <summary>Builds fictional clubs and players. No real league, club or player data is used.</summary>
public static class SquadFactory
{
    /// <summary>4-3-3 slot positions, expressed as (progress from own goal 0..1, width 0..1).</summary>
    public static readonly (string Pos, double Prog, double Width)[] Formation433 =
    [
        ("GK", 0.03, 0.50),
        ("LB", 0.22, 0.12), ("CB", 0.18, 0.37), ("CB", 0.18, 0.63), ("RB", 0.22, 0.88),
        ("CM", 0.40, 0.28), ("DM", 0.34, 0.50), ("CM", 0.40, 0.72),
        ("LW", 0.62, 0.12), ("ST", 0.66, 0.50), ("RW", 0.62, 0.88)
    ];

    private static readonly string[] HomeNames =
    [
        "Tomas Varga", "Ibrahim Sall", "Jonah Mercer", "Luca Ferrandi", "Dev Malhotra",
        "Kenji Arata", "Samuel Okafor", "Mateo Ruiz", "Yusuf Demir", "Elias Brandt", "Rafael Costa"
    ];

    private static readonly string[] AwayNames =
    [
        "Piotr Nowak", "Callum Reid", "Andre Silva", "Hamza Qureshi", "Felix Moreau",
        "Omar Haddad", "Niklas Berg", "Diego Alvarez", "Kofi Mensah", "Bilal Rana", "Theo Laurent"
    ];

    // Invented name pools for the other clubs. Squads are drawn from these deterministically per club.
    private static readonly string[] FirstNames =
    [
        "Aron", "Bastian", "Cyrus", "Dario", "Emre", "Farid", "Gideon", "Hugo", "Idris", "Jasper", "Kai", "Leandro",
        "Marek", "Nabil", "Oskar", "Pavel", "Quentin", "Rohan", "Stellan", "Tariq", "Umar", "Viktor", "Wendel", "Xavi",
        "Yannick", "Zoran", "Ansel", "Bruno", "Caio", "Dmitri", "Ezra", "Florin", "Goran", "Hassan", "Ilya", "Joaquin",
        "Kwame", "Lorcan", "Milan", "Noah"
    ];

    private static readonly string[] LastNames =
    [
        "Albrecht", "Baptiste", "Castellano", "Dragan", "Eriksen", "Farouk", "Galvao", "Halloran", "Ivanic", "Jansen",
        "Kowal", "Lindqvist", "Mbeki", "Nakamura", "Orsini", "Petrakis", "Quaresma", "Rashid", "Sorensen", "Tavares",
        "Udeh", "Vasquez", "Whitlock", "Yilmaz", "Zielinski", "Abara", "Bellamy", "Cardoso", "Delacroix", "Esposito",
        "Fenwick", "Grieve", "Hakimi-Ross", "Iqbal", "Juric", "Kearney", "Lanzini", "Moretti", "Nwosu", "Okonjo",
        "Pereira", "Rautio", "Strand", "Teodor", "Varela", "Wojcik"
    ];

    private static readonly int[] Numbers = [1, 3, 4, 5, 2, 8, 6, 10, 11, 9, 7];

    public static ClubSpec Northbridge => new("Northbridge City", "NBC", "#38bdf8", "#0c4a6e", TacticalProfile.Possession, HomeNames);
    public static ClubSpec Harbour => new("Harbour United", "HAR", "#f97316", "#7c2d12", TacticalProfile.LowBlock, AwayNames);

    public static MatchMetadata CreateDefaultMatch(int seed) =>
        CreateMatch(seed, Northbridge, Harbour, "Riverside Stadium", $"WL-{seed:0000}");

    /// <summary>Builds a match between two fictional clubs. Player ids are 1-11 (home) and 12-22 (away), in 4-3-3 slot order.</summary>
    public static MatchMetadata CreateMatch(int seed, ClubSpec homeClub, ClubSpec awayClub, string venue, string matchId)
    {
        var rng = new Random(seed);
        var home = new Team(TeamSide.Home, homeClub.Name, homeClub.Code, homeClub.PrimaryColor, homeClub.SecondaryColor, homeClub.Profile);
        var away = new Team(TeamSide.Away, awayClub.Name, awayClub.Code, awayClub.PrimaryColor, awayClub.SecondaryColor, awayClub.Profile);
        var homeNames = homeClub.PlayerNames ?? Squad(homeClub.Code, exclude: []);
        var awayNames = awayClub.PlayerNames ?? Squad(awayClub.Code, exclude: homeNames);

        var players = new List<Player>();
        for (var i = 0; i < 11; i++)
            players.Add(new Player(i + 1, homeNames[i], Numbers[i], TeamSide.Home, Formation433[i].Pos,
                Pace: 0.55 + rng.NextDouble() * 0.4, Technique: 0.55 + rng.NextDouble() * 0.4));
        for (var i = 0; i < 11; i++)
            players.Add(new Player(i + 12, awayNames[i], Numbers[i], TeamSide.Away, Formation433[i].Pos,
                Pace: 0.55 + rng.NextDouble() * 0.4, Technique: 0.55 + rng.NextDouble() * 0.4));

        return new MatchMetadata(matchId, "WhyLine Synthetic League", venue, home, away, players, seed);
    }

    /// <summary>Eleven invented names for a club, stable for its code, with no repeated surname in the match.</summary>
    private static IReadOnlyList<string> Squad(string code, IReadOnlyList<string> exclude)
    {
        var rng = new Random(code.Aggregate(17, (h, c) => h * 31 + c));
        var usedLast = exclude.Select(n => n[(n.LastIndexOf(' ') + 1)..]).ToHashSet();
        var usedFirst = new HashSet<string>();
        var names = new List<string>();
        while (names.Count < 11)
        {
            var first = FirstNames[rng.Next(FirstNames.Length)];
            var last = LastNames[rng.Next(LastNames.Length)];
            if (!usedFirst.Add(first)) continue;
            if (!usedLast.Add(last)) { usedFirst.Remove(first); continue; }
            names.Add($"{first} {last}");
        }

        return names;
    }
}
