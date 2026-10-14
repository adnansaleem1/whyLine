using Microsoft.Extensions.Logging.Abstractions;
using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Pipeline;
using WhyLine.Agents.Template;
using WhyLine.Agents.Workflows;
using WhyLine.Core.Domain;

namespace WhyLine.Tests;

/// <summary>Runs the newsroom as a real Microsoft Agent Framework workflow graph, end to end.</summary>
[Collection("match")]
public sealed class WorkflowTests(MatchFixture match)
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

    private sealed class InventingNarrator : INarratorAgent
    {
        public int Calls;

        public Task<NarrativeDraft> NarrateAsync(NarrationRequest request, string? verifierFeedback, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(verifierFeedback is null
                ? new NarrativeDraft("Invented", ["That was his 99th goal of the season."], "test")
                : TemplateNarratorAgent.Narrate(request));
        }
    }

    private (NewsroomWorkflow Workflow, Sink Sink) Build(INarratorAgent narrator, params ViewerProfile[] profiles)
    {
        var sink = new Sink();
        var options = new AgentOptions();
        var steps = new NewsroomSteps(new TemplateAnalystAgent(), new DirectorAgent(options), narrator, new TemplateLocalizationAgent(),
            new TemplateRecapAgent(), new ClaimVerifier(), sink, new Profiles(profiles), options, NullLogger<NewsroomSteps>.Instance)
        {
            MomentsSoFar = () => match.Moments
        };
        return (new NewsroomWorkflow(steps, NullLogger<NewsroomWorkflow>.Instance), sink);
    }

    private MomentContext Context(Moment m)
    {
        var byId = match.Events.ToDictionary(e => e.Id);
        return new MomentContext(m, match.Final, match.Script.Metadata, m.EvidenceEventIds.Select(id => byId[id]).ToArray());
    }

    [Fact]
    public void Workflow_graphs_build()
    {
        var (workflow, _) = Build(new TemplateNarratorAgent(), ViewerProfile.Default);
        Assert.NotNull(workflow.BuildMomentWorkflow());
        Assert.NotNull(workflow.BuildRecapWorkflow());
    }

    [Fact]
    public async Task A_goal_flows_through_the_graph_to_every_viewer_including_localisation()
    {
        var english = new ViewerProfile(Persona.Casual, "en", TeamSide.Home, null);
        var urdu = new ViewerProfile(Persona.Casual, "ur", TeamSide.Away, null);
        var (workflow, sink) = Build(new TemplateNarratorAgent(), english, urdu);
        var goal = match.Moments.First(m => m.Kind == MomentKind.Goal);

        await workflow.ProcessAsync(Context(goal), goal.MatchSeconds, CancellationToken.None);

        Assert.Equal(2, sink.Cues.Count);
        Assert.All(sink.Cues, c => Assert.True(c.Verified));
        Assert.Contains(sink.Cues, c => c.Language == "ur");
        Assert.Contains(sink.Traces, t => t.Agent.StartsWith("Localizer"));
    }

    [Fact]
    public async Task Verifier_edge_loops_back_to_the_narrator_with_feedback()
    {
        var viewer = new ViewerProfile(Persona.Casual, "en", null, null);
        var narrator = new InventingNarrator();
        var (workflow, sink) = Build(narrator, viewer);
        var goal = match.Moments.First(m => m.Kind == MomentKind.Goal);

        await workflow.ProcessAsync(Context(goal), goal.MatchSeconds, CancellationToken.None);

        Assert.Equal(2, narrator.Calls);
        var cue = Assert.Single(sink.Cues);
        Assert.DoesNotContain("99", cue.Body);
        Assert.Contains(sink.Traces, t => t.Agent == "Verifier" && t.Status == TraceStatus.Rejected);
    }

    [Fact]
    public async Task Catch_me_up_runs_on_the_recap_graph()
    {
        var viewer = new ViewerProfile(Persona.Casual, "en", null, null);
        var (workflow, _) = Build(new TemplateNarratorAgent(), viewer);
        var firstGoal = match.Moments.First(m => m.Kind == MomentKind.Goal);
        var cutoff = firstGoal.MatchSeconds + 60;
        var snapshot = match.Final with { MatchSeconds = cutoff, HomeScore = 1, AwayScore = 0 };

        var cue = await workflow.BuildRecapAsync(
            new RecapRequest(RecapKind.CatchMeUp, viewer, match.Script.Metadata, snapshot, match.Moments.Where(m => m.MatchSeconds <= cutoff).ToArray()),
            null, CancellationToken.None);

        Assert.True(cue.Verified);
        Assert.Contains("1-0", cue.Title);
    }
}
