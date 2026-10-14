using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Template;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Pipeline;

/// <summary>The newsroom as the rest of the app sees it, whichever orchestrator runs it.</summary>
public interface INewsroom
{
    /// <summary>"Workflow" (Microsoft Agent Framework) or "In-process".</summary>
    string Orchestration { get; }

    Func<IReadOnlyList<Moment>> MomentsSoFar { get; set; }

    /// <summary>Runs one detected moment through the whole newsroom and broadcasts every verified cue.</summary>
    Task ProcessAsync(MomentContext ctx, double liveClock, CancellationToken ct);

    /// <summary>Builds one viewer's recap (catch me up). Returned to the caller, not broadcast.</summary>
    Task<OverlayCue> BuildRecapAsync(RecapRequest request, Moment? anchor, CancellationToken ct);
}

/// <summary>
/// In-process orchestrator. Walks exactly the same graph as the Agent Framework workflow, using the same
/// <see cref="NewsroomSteps"/>, so it serves offline builds and the test suite:
/// Director → Analyst → (per viewer, in parallel) Narrator → Verifier ⟲ → Localizer → Verifier ⟲ → Publisher,
/// with Fallback when a model is unavailable or retries run out.
/// </summary>
public sealed class NewsroomPipeline(NewsroomSteps steps) : INewsroom
{
    public string Orchestration => "In-process";

    public Func<IReadOnlyList<Moment>> MomentsSoFar
    {
        get => steps.MomentsSoFar;
        set => steps.MomentsSoFar = value;
    }

    public async Task ProcessAsync(MomentContext ctx, double liveClock, CancellationToken ct)
    {
        using var span = NewsroomTelemetry.StartMoment(ctx.Moment, Orchestration);
        var tasks = new List<AuthorTask>();
        foreach (var next in steps.Direct(new MomentWork(ctx, liveClock)))
        {
            if (next is DirectedMoment directed) tasks.AddRange(await steps.AnalyzeAsync(directed, ct));
            else tasks.Add((AuthorTask)next);
        }

        await Task.WhenAll(tasks.Select(async task =>
        {
            if (await RunAsync(task, ct) is { } cue) steps.Broadcast(cue);
        }));
    }

    public async Task<OverlayCue> BuildRecapAsync(RecapRequest request, Moment? anchor, CancellationToken ct) =>
        await RunAsync(steps.RecapTask(request, anchor), ct) ?? Unverified(request, anchor);

    /// <summary>Follows messages through the graph until a cue is published or the work is dropped.</summary>
    private async Task<OverlayCue?> RunAsync(object message, CancellationToken ct)
    {
        while (true)
        {
            switch (message)
            {
                case AuthorTask task:
                    message = await steps.AuthorAsync(task, ct);
                    break;
                case FallbackTask fallback:
                    message = steps.Fallback(fallback);
                    break;
                case DraftForReview review:
                    var next = steps.Review(review);
                    if (next is null) return null;
                    message = next;
                    break;
                case ApprovedDraft approved:
                    return steps.Publish(approved);
                default:
                    throw new InvalidOperationException($"Unexpected newsroom message {message.GetType().Name}.");
            }
        }
    }

    /// <summary>Only reached if even the template recap fails verification; the card is marked unverified.</summary>
    public static OverlayCue Unverified(RecapRequest request, Moment? anchor)
    {
        var draft = TemplateRecapAgent.Recap(request);
        return new OverlayCue
        {
            Id = $"u{Guid.NewGuid():N}"[..9],
            Type = CueType.Recap,
            MatchSeconds = request.Snapshot.MatchSeconds,
            ProfileKey = request.Profile.Key,
            Language = request.Profile.Language,
            Title = draft.Title,
            Body = string.Join(" ", draft.Sentences),
            Citations = draft.Sentences.Select(s => new Citation(s, [])).ToArray(),
            MomentId = anchor?.Id,
            Source = "agents",
            Verified = false
        };
    }
}
