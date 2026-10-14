using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Pipeline;
using WhyLine.Agents.Template;
using WhyLine.Core.Domain;

namespace WhyLine.Tests;

/// <summary>Each moment must become one trace with a span per agent step, so Foundry tracing shows the whole newsroom.</summary>
[Collection("match")]
public sealed class TelemetryTests(MatchFixture match)
{
    private sealed class Sink : INewsroomSink
    {
        public void Trace(AgentTrace trace) { }
        public void Publish(OverlayCue cue) { }
    }

    private sealed class Profiles(IReadOnlyCollection<ViewerProfile> profiles) : IProfileSource
    {
        public IReadOnlyCollection<ViewerProfile> ActiveProfiles() => profiles;
    }

    [Fact]
    public async Task A_moment_becomes_one_trace_with_a_span_per_agent()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name.StartsWith(NewsroomTelemetry.Prefix),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); }
        };
        ActivitySource.AddActivityListener(listener);

        var options = new AgentOptions();
        var viewer = new ViewerProfile(Persona.Casual, "ur", TeamSide.Home, null);
        var steps = new NewsroomSteps(new TemplateAnalystAgent(), new DirectorAgent(options), new TemplateNarratorAgent(),
            new TemplateLocalizationAgent(), new TemplateRecapAgent(), new ClaimVerifier(), new Sink(), new Profiles([viewer]), options,
            NullLogger<NewsroomSteps>.Instance);
        var goal = match.Moments.First(m => m.Kind == MomentKind.Goal);
        var byId = match.Events.ToDictionary(e => e.Id);
        var ctx = new MomentContext(goal, match.Final, match.Script.Metadata, goal.EvidenceEventIds.Select(id => byId[id]).ToArray());

        await new NewsroomPipeline(steps).ProcessAsync(ctx, goal.MatchSeconds, CancellationToken.None);

        List<Activity> mine;
        lock (spans) mine = spans.Where(s => Equals(s.GetTagItem("whyline.moment.id"), goal.Id)).ToList();
        var root = Assert.Single(mine, s => s.OperationName == "newsroom Goal");
        Assert.All(mine, s => Assert.Equal(root.TraceId, s.TraceId));
        var agents = mine.Select(s => s.GetTagItem("gen_ai.agent.name") as string).ToHashSet();
        Assert.Contains(AgentCatalog.DirectorName, agents);
        Assert.Contains(AgentCatalog.AnalystName, agents);
        Assert.Contains(AgentCatalog.NarratorName, agents);
        Assert.Contains(AgentCatalog.LocalizerName, agents);
        Assert.Contains(AgentCatalog.VerifierName, agents);
        Assert.Contains("whyline-publisher", agents);
        Assert.All(mine.Where(s => s.GetTagItem("gen_ai.agent.name") as string == AgentCatalog.VerifierName),
            s => Assert.Equal(true, s.GetTagItem("whyline.verifier.passed")));
    }
}
