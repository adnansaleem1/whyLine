using Microsoft.Extensions.Logging.Abstractions;
using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Pipeline;
using WhyLine.Agents.Template;
using WhyLine.Core.Domain;
using WhyLine.Core.Engine;
using WhyLine.Core.Simulation;

namespace WhyLine.Tests;

/// <summary>Every fixture in the match picker must play out as scripted and produce only verified storytelling.</summary>
public sealed class ScenarioTests
{
    public static IEnumerable<object[]> Scenarios => MatchCatalog.All.Select(s => new object[] { s.Id });

    private sealed record Played(MatchScript Script, int Home, int Away, List<MatchEvent> Events, List<Moment> Moments, MatchSnapshot Final);

    private static Played Play(string id)
    {
        var script = MatchCatalog.Build(id, 7);
        var sim = new MatchSimulator(script);
        var analyzer = new MatchAnalyzer(script.Metadata);
        var events = new List<MatchEvent>();
        var moments = new List<Moment>();
        while (!sim.Finished)
        {
            var step = sim.Step();
            analyzer.IngestFrame(step.Frame);
            foreach (var e in step.Events)
            {
                events.Add(e);
                moments.AddRange(analyzer.Ingest(e));
            }

            moments.AddRange(analyzer.Tick(step.Frame.MatchSeconds));
        }

        return new Played(script, sim.HomeScore, sim.AwayScore, events, moments, analyzer.Snapshot(sim.Clock));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Each_fixture_ends_with_its_scripted_score(string id)
    {
        var played = Play(id);
        Assert.Equal(MatchCatalog.ById(id).ExpectedScore, $"{played.Home}-{played.Away}");
        Assert.Equal(played.Home + played.Away, played.Moments.Count(m => m.Kind == MomentKind.Goal));
        Assert.Contains(played.Moments, m => m.Kind == MomentKind.FullTime);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Each_fixture_has_realistic_numbers(string id)
    {
        var s = Play(id).Final;
        Assert.InRange(s.Home.PossessionPct + s.Away.PossessionPct, 99, 101);
        Assert.InRange(s.Home.Shots + s.Away.Shots, 8, 50);
        Assert.All(s.Players.Where(p => p.Position != "GK"), p => Assert.InRange(p.DistanceKm, 7, 18));
    }

    [Fact]
    public void Fixtures_use_distinct_clubs_and_unique_player_names()
    {
        var metas = MatchCatalog.All.Select(s => s.Build(7).Metadata).ToArray();
        var clubs = metas.SelectMany(m => new[] { m.Home.Name, m.Away.Name }).ToArray();
        Assert.Equal(clubs.Length, clubs.Distinct().Count());
        Assert.All(metas, m => Assert.Equal(22, m.Players.Select(p => p.Name).Distinct().Count()));
        Assert.All(metas, m => Assert.Equal(22, m.Players.Select(p => p.ShortName).Distinct().Count()));
    }

    private sealed class Sink : INewsroomSink
    {
        public List<AgentTrace> Traces { get; } = [];
        public List<OverlayCue> Cues { get; } = [];
        public void Trace(AgentTrace trace) { lock (this) Traces.Add(trace); }
        public void Publish(OverlayCue cue) { lock (this) Cues.Add(cue); }
    }

    private sealed class Profiles(IReadOnlyCollection<ViewerProfile> profiles) : IProfileSource
    {
        public IReadOnlyCollection<ViewerProfile> ActiveProfiles() => profiles;
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Each_fixture_publishes_only_verified_cards_in_every_language(string id)
    {
        var played = Play(id);
        var meta = played.Script.Metadata;
        var focus = meta.Players.First(p => p.Team == TeamSide.Home && p.Position == "ST").Id;
        var sink = new Sink();
        var options = new AgentOptions();
        var steps = new NewsroomSteps(new TemplateAnalystAgent(), new DirectorAgent(options), new TemplateNarratorAgent(),
            new TemplateLocalizationAgent(), new TemplateRecapAgent(), new ClaimVerifier(), sink,
            new Profiles(
            [
                new ViewerProfile(Persona.Analyst, "en", null, null),
                new ViewerProfile(Persona.Casual, "ur", TeamSide.Away, null),
                new ViewerProfile(Persona.PlayerFocus, "es", null, focus),
                new ViewerProfile(Persona.AudioDescription, "ar", TeamSide.Home, null)
            ]), options, NullLogger<NewsroomSteps>.Instance);
        var pipeline = new NewsroomPipeline(steps) { MomentsSoFar = () => played.Moments };
        var byId = played.Events.ToDictionary(e => e.Id);

        foreach (var m in played.Moments)
        {
            var ctx = new MomentContext(m, played.Final, meta, m.EvidenceEventIds.Select(e => byId[e]).ToArray());
            await pipeline.ProcessAsync(ctx, m.MatchSeconds, CancellationToken.None);
        }

        Assert.NotEmpty(sink.Cues);
        Assert.All(sink.Cues, c => Assert.True(c.Verified));
        Assert.DoesNotContain(sink.Traces, t => t.Detail.StartsWith("Fallback issue"));
        Assert.Contains(sink.Cues, c => c.Type == CueType.Recap && c.MomentKind == MomentKind.FullTime);
    }
}
