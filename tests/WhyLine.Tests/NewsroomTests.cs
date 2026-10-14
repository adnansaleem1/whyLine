using Microsoft.Extensions.Logging.Abstractions;
using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Pipeline;
using WhyLine.Agents.Template;
using WhyLine.Core.Domain;
using WhyLine.Core.Engine;

namespace WhyLine.Tests;

[Collection("match")]
public sealed class NewsroomTests(MatchFixture match)
{
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

    private (NewsroomPipeline Pipeline, Sink Sink) Build(params ViewerProfile[] profiles)
    {
        var sink = new Sink();
        var options = new AgentOptions();
        var steps = new NewsroomSteps(new TemplateAnalystAgent(), new DirectorAgent(options), new TemplateNarratorAgent(),
            new TemplateLocalizationAgent(), new TemplateRecapAgent(), new ClaimVerifier(), sink, new Profiles(profiles), options,
            NullLogger<NewsroomSteps>.Instance);
        var pipeline = new NewsroomPipeline(steps) { MomentsSoFar = () => match.Moments };
        return (pipeline, sink);
    }

    private MomentContext Context(Moment m)
    {
        var byId = match.Events.ToDictionary(e => e.Id);
        return new MomentContext(m, match.Final, match.Script.Metadata, m.EvidenceEventIds.Select(id => byId[id]).ToArray());
    }

    [Fact]
    public async Task Every_persona_and_language_publishes_only_verified_narratives()
    {
        var focus = match.Script.Metadata.Players.First(p => p.Team == TeamSide.Away && p.Position == "ST").Id;
        var (pipeline, sink) = Build(
            new ViewerProfile(Persona.Analyst, "en", null, null),
            new ViewerProfile(Persona.Casual, "ur", TeamSide.Away, null),
            new ViewerProfile(Persona.PlayerFocus, "es", null, focus),
            new ViewerProfile(Persona.AudioDescription, "ar", TeamSide.Home, null));

        foreach (var m in match.Moments)
            await pipeline.ProcessAsync(Context(m), m.MatchSeconds, CancellationToken.None);

        Assert.NotEmpty(sink.Cues);
        Assert.All(sink.Cues, c => Assert.True(c.Verified));
        Assert.DoesNotContain(sink.Traces, t => t.Agent == "Verifier" && t.Status == TraceStatus.Rejected);
        Assert.Equal(4, sink.Cues.Select(c => c.ProfileKey).Distinct().Count());
        Assert.Contains(sink.Cues, c => c.Language == "ur" && c.MomentKind == MomentKind.Goal);
    }

    [Fact]
    public async Task Same_goal_is_told_differently_to_different_fans()
    {
        var home = new ViewerProfile(Persona.Casual, "en", TeamSide.Home, null);
        var away = new ViewerProfile(Persona.Casual, "en", TeamSide.Away, null);
        var (pipeline, sink) = Build(home, away);
        var goal = match.Moments.Last(m => m.Kind == MomentKind.Goal);

        await pipeline.ProcessAsync(Context(goal), goal.MatchSeconds, CancellationToken.None);

        var forHome = Assert.Single(sink.Cues, c => c.ProfileKey == home.Key);
        var forAway = Assert.Single(sink.Cues, c => c.ProfileKey == away.Key);
        Assert.NotEqual(forHome.Body, forAway.Body);
    }

    private sealed class FlakyNarrator(int badDrafts) : INarratorAgent
    {
        public int Calls;
        public List<string?> Feedback { get; } = [];

        public Task<NarrativeDraft> NarrateAsync(NarrationRequest request, string? verifierFeedback, CancellationToken ct)
        {
            Calls++;
            Feedback.Add(verifierFeedback);
            return Task.FromResult(Calls <= badDrafts
                ? new NarrativeDraft("Invented", ["That was his 99th goal of the season."], "flaky")
                : TemplateNarratorAgent.Narrate(request));
        }
    }

    [Theory]
    [InlineData(1, true)]   // one bad draft: rewritten with Verifier feedback, then published
    [InlineData(5, false)]  // keeps inventing: retries run out, template fallback is published
    public async Task Verifier_sends_bad_drafts_back_with_feedback_and_falls_back_when_retries_run_out(int badDrafts, bool modelVoicePublished)
    {
        var viewer = new ViewerProfile(Persona.Casual, "en", null, null);
        var sink = new Sink();
        var options = new AgentOptions { MaxVerifierRetries = 2 };
        var narrator = new FlakyNarrator(badDrafts);
        var steps = new NewsroomSteps(new TemplateAnalystAgent(), new DirectorAgent(options), narrator, new TemplateLocalizationAgent(),
            new TemplateRecapAgent(), new ClaimVerifier(), sink, new Profiles([viewer]), options, NullLogger<NewsroomSteps>.Instance);
        var goal = match.Moments.First(m => m.Kind == MomentKind.Goal);

        await new NewsroomPipeline(steps).ProcessAsync(Context(goal), goal.MatchSeconds, CancellationToken.None);

        var cue = Assert.Single(sink.Cues);
        Assert.DoesNotContain("99", cue.Body);
        Assert.NotNull(narrator.Feedback[1]);
        Assert.Contains("99", narrator.Feedback[1]!);
        Assert.Equal(modelVoicePublished ? 2 : 3, narrator.Calls);
        Assert.Equal(!modelVoicePublished, sink.Traces.Any(t => t.Detail.Contains("deterministic fallback verified")));
    }

    [Fact]
    public void Director_enforces_the_screen_time_budget_but_lets_goals_through()
    {
        var director = new DirectorAgent(new AgentOptions { ScreenBudgetSeconds = 60 });
        ViewerProfile[] viewers = [new(Persona.Analyst, "en", null, null)];
        Moment At(double t, MomentKind kind, double importance) =>
            new() { Id = $"x{t}", Kind = kind, MatchSeconds = t, Importance = importance, Team = TeamSide.Home };

        Assert.True(director.Decide(At(100, MomentKind.DifficultPass, 0.5), viewers, 100).Approved);
        Assert.False(director.Decide(At(120, MomentKind.BigChance, 0.6), viewers, 120).Approved);
        Assert.True(director.Decide(At(130, MomentKind.Goal, 0.9), viewers, 130).Approved);
    }

    [Fact]
    public async Task Catch_me_up_only_uses_what_the_viewer_has_already_seen()
    {
        var viewer = new ViewerProfile(Persona.Casual, "en", null, null);
        var (pipeline, _) = Build(viewer);
        var firstGoal = match.Moments.First(m => m.Kind == MomentKind.Goal);
        var cutoff = firstGoal.MatchSeconds + 60;
        var seen = match.Moments.Where(m => m.MatchSeconds <= cutoff).ToArray();
        var snapshot = match.Final with { MatchSeconds = cutoff, HomeScore = 1, AwayScore = 0 };

        var cue = await pipeline.BuildRecapAsync(new RecapRequest(RecapKind.CatchMeUp, viewer, match.Script.Metadata, snapshot, seen), null,
            CancellationToken.None);

        Assert.Contains("1-0", cue.Title);
        Assert.DoesNotContain(match.Moments.Where(m => m.Kind == MomentKind.Goal).Skip(1),
            later => cue.Body.Contains(later.FactOf("scorer")!.Text!));
    }

    [Fact]
    public void Pass_difficulty_rewards_line_breaking_passes_under_pressure()
    {
        var easy = new MatchEvent
        {
            Id = "a", Type = MatchEventType.Pass, MatchSeconds = 1, Team = TeamSide.Home, Start = new Vec2(30, 34), End = new Vec2(36, 34),
            Distance = 6, PressureMeters = 10, ReceiverPressureMeters = 10, OpponentsBypassed = 0, Success = true
        };
        var hard = easy with { End = new Vec2(70, 20), Distance = 43, PressureMeters = 1, ReceiverPressureMeters = 1.5, OpponentsBypassed = 4 };

        Assert.True(PassQuality.Difficulty(hard) > PassQuality.Difficulty(easy));
        Assert.InRange(PassQuality.Difficulty(hard), 0.75, 1);
        Assert.InRange(PassQuality.Difficulty(easy), 0, 0.2);
    }
}
