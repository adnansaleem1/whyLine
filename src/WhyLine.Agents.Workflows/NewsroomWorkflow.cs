using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Pipeline;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Workflows;

/// <summary>
/// The match-day newsroom as a Microsoft Agent Framework workflow graph.
///
/// Moment workflow (start: Director):
///   Director ─(approved)→ Analyst ─(Narrate, one per viewer)→ Narrator ─┐
///            └(half-time / full-time)→ Recap ────────────────────────────┤
///   Narrator / Localizer / Recap ─(draft)→ Verifier, ─(model down)→ Fallback → Verifier
///   Verifier ─(approved)→ Publisher ⇒ output
///            ─(retry, feedback)→ the same author
///            ─(English verified, viewer wants another language)→ Localizer
///            ─(retries used up)→ Fallback
///
/// Catch-me-up workflow (start: Recap) reuses the Recap, Verifier, Fallback and Publisher executors.
/// </summary>
public sealed class NewsroomWorkflow(NewsroomSteps steps, ILogger<NewsroomWorkflow> logger) : INewsroom
{
    public const string FallbackId = "whyline-fallback";
    public const string PublisherId = "whyline-publisher";

    public string Orchestration => "Workflow";

    public Func<IReadOnlyList<Moment>> MomentsSoFar
    {
        get => steps.MomentsSoFar;
        set => steps.MomentsSoFar = value;
    }

    /// <summary>Builds a fresh workflow per run, so concurrent moments never share executor instances.</summary>
    public Workflow BuildMomentWorkflow()
    {
        var analyst = new AnalystExecutor(steps);
        var director = new DirectorExecutor(steps);
        var narrator = new NarratorExecutor(steps);
        var localizer = new LocalizerExecutor(steps);
        var recap = new RecapExecutor(steps);
        var fallback = new FallbackExecutor(steps);
        var verifier = new VerifierExecutor(steps);
        var publisher = new PublisherExecutor(steps);

        var builder = new WorkflowBuilder(director)
            .AddEdge(director, analyst, condition: Is<DirectedMoment>())
            .AddEdge(director, recap, condition: Stage(DraftStage.Recap))
            .AddEdge(analyst, narrator, condition: Stage(DraftStage.Narrate));

        AddReviewLoop(builder, narrator, localizer, recap, fallback, verifier, publisher);
        Instrument(builder);
        return builder.WithOutputFrom(publisher).Build();
    }

    /// <summary>Workflow, executor and message spans from Agent Framework, under the WhyLine.Workflow source.</summary>
    private static void Instrument(WorkflowBuilder builder) =>
        builder.WithOpenTelemetry(configure: null, activitySource: NewsroomTelemetry.WorkflowSource);

    /// <summary>A smaller graph for on-demand recaps: Recap → Verifier ⟲ → Publisher.</summary>
    public Workflow BuildRecapWorkflow()
    {
        var narrator = new NarratorExecutor(steps);
        var localizer = new LocalizerExecutor(steps);
        var recap = new RecapExecutor(steps);
        var fallback = new FallbackExecutor(steps);
        var verifier = new VerifierExecutor(steps);
        var publisher = new PublisherExecutor(steps);

        var builder = new WorkflowBuilder(recap);
        AddReviewLoop(builder, narrator, localizer, recap, fallback, verifier, publisher);
        Instrument(builder);
        return builder.WithOutputFrom(publisher).Build();
    }

    private static void AddReviewLoop(WorkflowBuilder builder, Executor narrator, Executor localizer, Executor recap,
        Executor fallback, Executor verifier, Executor publisher)
    {
        foreach (var author in new[] { narrator, localizer, recap })
        {
            builder.AddEdge(author, verifier, condition: Is<DraftForReview>());
            builder.AddEdge(author, fallback, condition: Is<FallbackTask>());
        }

        builder.AddEdge(fallback, verifier)
            .AddEdge(verifier, publisher, condition: Is<ApprovedDraft>())
            .AddEdge(verifier, fallback, condition: Is<FallbackTask>())
            .AddEdge(verifier, narrator, condition: Stage(DraftStage.Narrate))
            .AddEdge(verifier, localizer, condition: Stage(DraftStage.Localize))
            .AddEdge(verifier, recap, condition: Stage(DraftStage.Recap));
    }

    private static Func<object?, bool> Is<T>() => message => message is T;

    private static Func<object?, bool> Stage(DraftStage stage) => message => message is AuthorTask task && task.Stage == stage;

    public async Task ProcessAsync(MomentContext ctx, double liveClock, CancellationToken ct)
    {
        using var span = NewsroomTelemetry.StartMoment(ctx.Moment, Orchestration);
        foreach (var cue in await RunAsync(BuildMomentWorkflow(), new MomentWork(ctx, liveClock), ctx.Moment.Id))
            steps.Broadcast(cue);
    }

    public async Task<OverlayCue> BuildRecapAsync(RecapRequest request, Moment? anchor, CancellationToken ct)
    {
        var cues = await RunAsync(BuildRecapWorkflow(), steps.RecapTask(request, anchor), anchor?.Id ?? "recap");
        return cues.FirstOrDefault() ?? NewsroomPipeline.Unverified(request, anchor);
    }

    /// <summary>Runs a workflow in-process to completion and collects the published cues.</summary>
    private async Task<IReadOnlyList<OverlayCue>> RunAsync<TInput>(Workflow workflow, TInput input, string label) where TInput : notnull
    {
        var cues = new List<OverlayCue>();
        Run run = await InProcessExecution.RunAsync(workflow, input);
        foreach (WorkflowEvent evt in run.NewEvents)
        {
            switch (evt)
            {
                case WorkflowOutputEvent { Data: OverlayCue cue }:
                    cues.Add(cue);
                    break;
                case ExecutorFailedEvent failed:
                    logger.LogError("Newsroom workflow executor {Executor} failed on {Moment}: {Data}", failed.ExecutorId, label, failed.Data);
                    break;
                case WorkflowErrorEvent error:
                    logger.LogError("Newsroom workflow failed on {Moment}: {Data}", label, error.Data);
                    break;
            }
        }

        return cues;
    }
}
