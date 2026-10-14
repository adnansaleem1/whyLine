using System.Diagnostics;
using System.Diagnostics.Metrics;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Pipeline;

/// <summary>
/// OpenTelemetry instrumentation for the newsroom. Every moment becomes one trace: a root span for the moment, a child
/// span per agent step (Director, Analyst, Narrator, Localizer, Recap, Verifier, Publisher), and the Agent Framework's
/// own model-call spans underneath. Exported to Application Insights, where the Foundry portal's Tracing view reads it.
/// </summary>
public static class NewsroomTelemetry
{
    /// <summary>All WhyLine sources and meters start with this prefix, so a host can subscribe with "WhyLine.*".</summary>
    public const string Prefix = "WhyLine";

    public const string NewsroomSourceName = "WhyLine.Newsroom";
    public const string AgentsSourceName = "WhyLine.Agents";
    public const string WorkflowSourceName = "WhyLine.Workflow";

    public static readonly ActivitySource Source = new(NewsroomSourceName, "1.0");
    public static readonly ActivitySource WorkflowSource = new(WorkflowSourceName, "1.0");

    private static readonly Meter Meter = new(NewsroomSourceName, "1.0");

    public static readonly Counter<long> MomentsDirected = Meter.CreateCounter<long>("whyline.moments.directed", "{moment}",
        "Moments the Director decided on, tagged approved or rejected.");

    public static readonly Counter<long> CardsPublished = Meter.CreateCounter<long>("whyline.cards.published", "{card}",
        "Verified cards published to viewers.");

    public static readonly Counter<long> VerifierRejections = Meter.CreateCounter<long>("whyline.verifier.rejections", "{draft}",
        "Drafts the Verifier sent back because a claim was not traceable to a fact.");

    public static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>("whyline.fallbacks", "{call}",
        "Model calls replaced by the deterministic template voice.");

    public static readonly Histogram<double> TimeToCard = Meter.CreateHistogram<double>("whyline.card.latency", "ms",
        "Time from the Director's decision to a published card.");

    /// <summary>Root span for one moment's trip through the newsroom.</summary>
    public static Activity? StartMoment(Moment moment, string orchestration)
    {
        var activity = Source.StartActivity($"newsroom {moment.Kind}", ActivityKind.Internal);
        if (activity is null) return null;
        activity.SetTag("whyline.moment.id", moment.Id);
        activity.SetTag("whyline.moment.kind", moment.Kind.ToString());
        activity.SetTag("whyline.moment.minute", moment.Minute);
        activity.SetTag("whyline.moment.importance", moment.Importance);
        activity.SetTag("whyline.orchestration", orchestration);
        return activity;
    }

    /// <summary>Span for one agent step. Uses the GenAI convention's agent name so trace viewers group spans by agent.</summary>
    public static Activity? StartStep(string agentName, string operation, string? momentId, ViewerProfile? profile = null)
    {
        var activity = Source.StartActivity($"{operation} {agentName}", ActivityKind.Internal);
        if (activity is null) return null;
        activity.SetTag("gen_ai.agent.name", agentName);
        activity.SetTag("whyline.moment.id", momentId);
        if (profile is not null)
        {
            activity.SetTag("whyline.viewer.persona", profile.Persona.ToString());
            activity.SetTag("whyline.viewer.language", profile.Language);
        }

        return activity;
    }
}
