using WhyLine.Core.Domain;

namespace WhyLine.Core.Engine;

public sealed record TeamSnapshot(
    TeamSide Side,
    string Name,
    string ShortName,
    string Color,
    string Profile,
    double PossessionPct,
    int Passes,
    int PassesCompleted,
    double PassAccuracyPct,
    int Shots,
    int ShotsOnTarget,
    double Xg,
    int Tackles,
    int Interceptions,
    int Pressures,
    double PressuresPerMinLast5);

public sealed record PlayerSnapshot(
    int Id,
    string Name,
    int Number,
    TeamSide Team,
    string Position,
    int Touches,
    int Passes,
    int PassesCompleted,
    double AvgPassDifficulty,
    int Shots,
    int Goals,
    int Tackles,
    int Interceptions,
    int Pressures,
    double DistanceKm,
    double TopSpeedKmh,
    int BestPassStreak,
    double? LastTouchSeconds);

public sealed record MomentumPoint(int Minute, double Value);

public sealed record MatchSnapshot(
    string MatchId,
    double MatchSeconds,
    int Period,
    int HomeScore,
    int AwayScore,
    TeamSnapshot Home,
    TeamSnapshot Away,
    IReadOnlyList<PlayerSnapshot> Players,
    IReadOnlyList<MomentumPoint> Momentum,
    double ControlIndex,
    string ControlLabel,
    double PassesPerMinuteLast5)
{
    public int Minute => (int)(MatchSeconds / 60) + 1;
    public TeamSnapshot TeamOf(TeamSide side) => side == TeamSide.Home ? Home : Away;
    public string ScoreLine => $"{Home.ShortName} {HomeScore}-{AwayScore} {Away.ShortName}";
}

/// <summary>A tracking-derived threshold crossing (fast path, no AI involved).</summary>
public sealed record ThresholdCrossing(CueType Kind, int PlayerId, double Value, double MatchSeconds, Vec2 Position);
