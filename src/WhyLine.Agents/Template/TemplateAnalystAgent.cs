using WhyLine.Agents.Abstractions;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Template;

/// <summary>Offline Analyst: explains moments with rules over the computed facts. Also the fallback for the Foundry Analyst.</summary>
public sealed class TemplateAnalystAgent : IAnalystAgent
{
    public Task<AnalystBrief> BriefAsync(MomentContext context, CancellationToken ct) => Task.FromResult(Brief(context));

    public static AnalystBrief Brief(MomentContext context)
    {
        var m = context.Moment;
        var f = new FactReader(m.Facts);
        var (headline, why, next) = m.Kind switch
        {
            MomentKind.Goal => (
                $"{f["scorer"]} scores for {f["team"]}",
                $"{GoalOrigin(f)} It was a {f.Num("xg", "0.00")} xG chance{(f.Has("shot_speed") ? $", struck at {f.Num("shot_speed")} km/h" : "")}.",
                f["state"] == "level" ? $"It's level at {f["score"]}: the next goal changes everything." : $"{f["state"]}, {f["score"]}. Watch how the trailing side responds."),

            MomentKind.BigChance => (
                $"Big chance for {f["player"]}",
                $"A {f.Num("xg", "0.00")} xG chance from {f.Num("shot_distance")} metres ended {f["outcome"]}. That is a chance a striker would expect to score far more often than not from this range.",
                $"Still {f["score"]}. Missed chances like this often decide tight games."),

            MomentKind.MomentumSwing => (
                $"{f["team"]} have taken control",
                $"{(f.Has("trigger") ? $"{f["trigger"]}. " : "")}Their pressing has risen from {f.Num("pressures_before")} to {f.Num("pressures_now")} per minute, with {f.Num("high_regains")} ball regains in advanced areas in the last five minutes. Possession moved from {f.Num("possession_before")}% to {f.Num("possession_now")}%.",
                $"Watch whether {f["opponent"]} can play through the press or are forced to go long."),

            MomentKind.PressSurge => (
                $"{f["team"]} turn up the press",
                $"{f["team"]} are pressing {f.Num("pressures_now")} times a minute, up from {f.Num("pressures_before")}. Their opponents are completing only {f.Num("opponent_completion")}% of passes.",
                "Pressing this hard is tiring. Watch for gaps opening behind it later."),

            MomentKind.ControlSpell => (
                $"{f["team"]} are running the game",
                $"{f["team"]} have had {f.Num("possession")}% of the ball over the last five minutes and completed {f.Num("completion")}% of {f.Num("passes")} passes.",
                "Control is not the same as chances. Watch whether the possession turns into shots."),

            MomentKind.DifficultPass => (
                $"Elite pass from {f["player"]}",
                $"{f["player"]} found {f["receiver"]} with a {f.Num("distance")} m pass that bypassed {f.Num("bypassed")} opponents, under pressure from {f.Num("pressure")} m away. Difficulty: {f.Num("difficulty")}/100.",
                "Passes like this break lines and create the space for chances."),

            MomentKind.PassStreak => (
                $"{f["player"]}: {f.Num("streak")} passes in a row",
                $"{f["player"]} has completed {f.Num("streak")} consecutive passes, {f.Num("under_pressure")} of them under pressure, with average difficulty {f.Num("avg_difficulty")}/100.",
                "A player this secure on the ball lets the team keep control."),

            MomentKind.ShotSpeed => (
                $"Rocket from {f["player"]}",
                $"{f["player"]} hit that at {f.Num("shot_speed")} km/h. The effort ended {f["outcome"]}.",
                "Power alone is not enough. Placement decides these."),

            MomentKind.QuietPlayer => (
                $"{f["player"]} has gone quiet",
                $"{f["player"]} has not touched the ball for {f.Num("minutes_without_touch")} minutes ({f.Num("touches")} touches so far). The team has had {f.Num("team_possession")}% possession in that spell.",
                "Watch whether the team changes shape to bring this player back into the game."),

            MomentKind.TacticalShift => (
                $"Tactical change: {f["team"]}",
                $"{f["change"]}. Until now they pressed {f.Num("pressures_before")} times a minute with {f.Num("possession_before")}% of the ball.",
                "Watch the pressing numbers over the next five minutes to see if it works."),

            MomentKind.HalfTime => (
                $"Half-time: {f["home"]} {f["score"]} {f["away"]}",
                $"Possession {f.Num("home_possession")}% to {f.Num("away_possession")}%, xG {f.Num("home_xg", "0.00")} to {f.Num("away_xg", "0.00")}, shots {f.Num("home_shots")} to {f.Num("away_shots")}.",
                "Watch for tactical changes at the restart."),

            MomentKind.FullTime => (
                $"Full-time: {f["home"]} {f["score"]} {f["away"]}",
                $"Possession {f.Num("home_possession")}% to {f.Num("away_possession")}%, xG {f.Num("home_xg", "0.00")} to {f.Num("away_xg", "0.00")}, shots {f.Num("home_shots")} to {f.Num("away_shots")}.",
                "The numbers and the result tell the story together."),

            _ => (m.Kind.ToString(), "", "")
        };

        return new AnalystBrief(headline, why, next, m.Facts, "template-analyst");
    }

    /// <summary>How the goal started: a regain (by the scorer or a teammate), a passing move, or a solo run.</summary>
    private static string GoalOrigin(FactReader f)
    {
        var how = f["won_how"] switch
        {
            "tackle" => "a tackle",
            "interception" => "an interception",
            "" => "a regain",
            var other => other
        };

        if (f.Has("won_by_scorer"))
            return $"{f["scorer"]} won the ball back himself with {how} in the {f["won_third"]}, just {f.Num("won_seconds_before")} seconds before scoring.";
        if (f.Has("won_by"))
            return $"It started with {f["won_by"]} winning the ball with {how} in the {f["won_third"]}, just {f.Num("won_seconds_before")} seconds earlier.";
        if (f.Has("build_up_passes"))
            return $"It finished a {f.Num("build_up_passes")}-pass move{(f.Has("assist") ? $" set up by {f["assist"]}" : "")}.";
        return $"{f["scorer"]} made it on his own, without a pass in the move.";
    }
}
