using WhyLine.Core.Domain;

namespace WhyLine.Core.Engine;

/// <summary>Pass difficulty rating (0 easy .. 1 very hard), computed from the event alone.</summary>
public static class PassQuality
{
    public static double Difficulty(MatchEvent pass)
    {
        if (pass.Type != MatchEventType.Pass || pass.Team is null) return 0;

        var distance = pass.Distance ?? 0;
        var distanceScore = Math.Clamp((distance - 5) / 40, 0, 1);
        var pressureScore = 1 - Math.Clamp((pass.PressureMeters ?? 10) / 5, 0, 1);
        var receiverScore = 1 - Math.Clamp((pass.ReceiverPressureMeters ?? 10) / 5, 0, 1);
        var linesScore = Math.Clamp((pass.OpponentsBypassed ?? 0) / 4.0, 0, 1);

        var progress = pass.End is { } end
            ? Pitch.Progress(pass.Team.Value, end) - Pitch.Progress(pass.Team.Value, pass.Start)
            : 0;
        var progressScore = Math.Clamp(progress / 0.3, 0, 1);

        var score = 0.25 * distanceScore + 0.22 * pressureScore + 0.18 * receiverScore + 0.25 * linesScore + 0.10 * progressScore;
        return Math.Round(Math.Clamp(score, 0, 1), 2);
    }

    public static string Label(double difficulty) => difficulty switch
    {
        >= 0.75 => "Elite",
        >= 0.55 => "Hard",
        >= 0.35 => "Medium",
        _ => "Routine"
    };

    /// <summary>Expected threat of a pitch location, 0..~0.2 (simple cubic proxy).</summary>
    public static double ThreatAt(TeamSide team, Vec2 p)
    {
        var prog = Pitch.Progress(team, p);
        var central = 1 - Math.Abs(p.Y - Pitch.Width / 2) / (Pitch.Width / 2) * 0.4;
        return (0.005 + 0.2 * prog * prog * prog) * central;
    }
}
