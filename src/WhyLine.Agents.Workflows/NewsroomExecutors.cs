using Microsoft.Agents.AI.Workflows;
using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Pipeline;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Workflows;

// One executor per newsroom agent. Executor ids match the agent names in AgentCatalog, so workflow events, traces
// and Foundry agents all line up. Each executor only translates messages; the logic lives in NewsroomSteps.

/// <summary>
/// Director: MomentWork → DirectedMoment for the Analyst, Recap AuthorTasks at half-time and full-time, or nothing when
/// rejected. It runs first so moments that will never air never cost a model call.
/// </summary>
[SendsMessage(typeof(DirectedMoment))]
[SendsMessage(typeof(AuthorTask))]
internal sealed partial class DirectorExecutor(NewsroomSteps steps) : Executor<MomentWork>(AgentCatalog.DirectorName)
{
    public override async ValueTask HandleAsync(MomentWork message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        foreach (var next in steps.Direct(message))
            await context.SendMessageAsync(next, cancellationToken);
    }
}

/// <summary>Analyst: DirectedMoment → one Narrate AuthorTask per approved viewer profile (fan-out).</summary>
[SendsMessage(typeof(AuthorTask))]
internal sealed partial class AnalystExecutor(NewsroomSteps steps) : Executor<DirectedMoment>(AgentCatalog.AnalystName)
{
    public override async ValueTask HandleAsync(DirectedMoment message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        foreach (var task in await steps.AnalyzeAsync(message, cancellationToken))
            await context.SendMessageAsync(task, cancellationToken);
    }
}

/// <summary>Narrator: AuthorTask (Narrate) → DraftForReview, or FallbackTask if the model is unavailable.</summary>
[SendsMessage(typeof(DraftForReview))]
[SendsMessage(typeof(FallbackTask))]
internal sealed partial class NarratorExecutor(NewsroomSteps steps) : Executor<AuthorTask>(AgentCatalog.NarratorName)
{
    public override async ValueTask HandleAsync(AuthorTask message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        await context.SendMessageAsync(await steps.AuthorAsync(message, cancellationToken), cancellationToken);
}

/// <summary>Localizer: AuthorTask (Localize) → DraftForReview, or FallbackTask.</summary>
[SendsMessage(typeof(DraftForReview))]
[SendsMessage(typeof(FallbackTask))]
internal sealed partial class LocalizerExecutor(NewsroomSteps steps) : Executor<AuthorTask>(AgentCatalog.LocalizerName)
{
    public override async ValueTask HandleAsync(AuthorTask message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        await context.SendMessageAsync(await steps.AuthorAsync(message, cancellationToken), cancellationToken);
}

/// <summary>Recap: AuthorTask (Recap) → DraftForReview, or FallbackTask.</summary>
[SendsMessage(typeof(DraftForReview))]
[SendsMessage(typeof(FallbackTask))]
internal sealed partial class RecapExecutor(NewsroomSteps steps) : Executor<AuthorTask>(AgentCatalog.RecapName)
{
    public override async ValueTask HandleAsync(AuthorTask message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        await context.SendMessageAsync(await steps.AuthorAsync(message, cancellationToken), cancellationToken);
}

/// <summary>Template fallback: FallbackTask → DraftForReview written in the deterministic template voice.</summary>
[SendsMessage(typeof(DraftForReview))]
internal sealed partial class FallbackExecutor(NewsroomSteps steps) : Executor<FallbackTask>(NewsroomWorkflow.FallbackId)
{
    public override async ValueTask HandleAsync(FallbackTask message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        await context.SendMessageAsync(steps.Fallback(message), cancellationToken);
}

/// <summary>
/// Verifier: DraftForReview → ApprovedDraft (publish), AuthorTask (retry with feedback, or localise), FallbackTask
/// (retries used up), or nothing (an unverifiable fallback is dropped).
/// </summary>
[SendsMessage(typeof(ApprovedDraft))]
[SendsMessage(typeof(AuthorTask))]
[SendsMessage(typeof(FallbackTask))]
internal sealed partial class VerifierExecutor(NewsroomSteps steps) : Executor<DraftForReview>(AgentCatalog.VerifierName)
{
    public override async ValueTask HandleAsync(DraftForReview message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (steps.Review(message) is { } next)
            await context.SendMessageAsync(next, cancellationToken);
    }
}

/// <summary>Publisher: ApprovedDraft → OverlayCue, yielded as the workflow's output.</summary>
[YieldsOutput(typeof(OverlayCue))]
internal sealed partial class PublisherExecutor(NewsroomSteps steps) : Executor<ApprovedDraft>(NewsroomWorkflow.PublisherId)
{
    public override async ValueTask HandleAsync(ApprovedDraft message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        await context.YieldOutputAsync(steps.Publish(message), cancellationToken);
}
