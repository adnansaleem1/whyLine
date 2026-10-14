using WhyLine.Core.Domain;
using WhyLine.Core.Engine;
using WhyLine.Core.Simulation;

namespace WhyLine.Tests;

/// <summary>
/// Golden-match tests: the scripted storyline must always produce the same key moments, so the demo (and the
/// agents built on it) are reproducible.
/// </summary>
[Collection("match")]
public sealed class GoldenMatchTests(MatchFixture match)
{
    [Fact]
    public void Scripted_storyline_produces_the_expected_result()
    {
        Assert.Equal(1, match.HomeScore);
        Assert.Equal(2, match.AwayScore);
    }

    [Theory]
    [InlineData(TeamSide.Home, 22, 26)]
    [InlineData(TeamSide.Away, 60, 64)]
    [InlineData(TeamSide.Away, 86, 90)]
    public void Goals_arrive_in_their_scripted_windows(TeamSide team, int fromMinute, int toMinute)
    {
        Assert.Contains(match.Moments, m => m.Kind == MomentKind.Goal && m.Team == team && m.Minute >= fromMinute && m.Minute <= toMinute);
    }

    [Fact]
    public void Press_switch_is_detected_and_followed_by_a_momentum_swing()
    {
        var shift = Assert.Single(match.Moments, m => m.Kind == MomentKind.TacticalShift);
        Assert.Equal(TeamSide.Away, shift.Team);

        var swing = match.Moments.FirstOrDefault(m => m.Kind == MomentKind.MomentumSwing && m.Team == TeamSide.Away &&
                                                      m.MatchSeconds > shift.MatchSeconds && m.MatchSeconds < shift.MatchSeconds + 600);
        Assert.NotNull(swing);
        Assert.True(swing!.FactOf("pressures_now")!.Number > swing.FactOf("pressures_before")!.Number,
            "The swing should be explained by a rise in pressing.");
        Assert.Contains(shift.EvidenceEventIds[0], swing.EvidenceEventIds);
    }

    [Fact]
    public void Every_moment_has_facts_and_evidence_that_exists()
    {
        var ids = match.Events.Select(e => e.Id).ToHashSet();
        foreach (var m in match.Moments)
        {
            Assert.NotEmpty(m.Facts);
            Assert.All(m.EvidenceEventIds, id => Assert.Contains(id, ids));
        }
    }

    [Fact]
    public void Simulation_is_deterministic_for_a_seed()
    {
        static (int Count, string Last) Run()
        {
            var sim = new MatchSimulator(MatchScript.Default(7));
            var count = 0;
            MatchEvent? last = null;
            while (sim.Clock < 600)
            {
                foreach (var e in sim.Step().Events)
                {
                    count++;
                    last = e;
                }
            }

            return (count, $"{last?.Id}:{last?.Type}:{last?.MatchSeconds}");
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Statistics_are_football_realistic()
    {
        var s = match.Final;
        Assert.InRange(s.Home.PossessionPct + s.Away.PossessionPct, 99, 101);
        Assert.InRange(s.Home.PassAccuracyPct, 60, 95);
        Assert.InRange(s.Away.PassAccuracyPct, 55, 95);
        Assert.All(s.Players.Where(p => p.Position != "GK"), p => Assert.InRange(p.DistanceKm, 7, 18));
        Assert.All(s.Players, p => Assert.InRange(p.TopSpeedKmh, 10, 36));
    }

    [Fact]
    public void Auto_eventing_recovers_passes_and_ball_speed_from_positions_alone()
    {
        var stats = match.Detector.Stats();
        Assert.True(stats.RecallPct >= 70, $"Recall {stats.RecallPct}%");
        Assert.True(stats.PrecisionPct >= 70, $"Precision {stats.PrecisionPct}%");
        Assert.True(stats.MeanSpeedErrorKmh < 3, $"Speed error {stats.MeanSpeedErrorKmh} km/h");
    }

    [Fact]
    public void Momentum_series_is_bounded()
    {
        Assert.Equal(90, match.Final.Momentum.Count);
        Assert.All(match.Final.Momentum, p => Assert.InRange(p.Value, -1, 1));
    }
}
