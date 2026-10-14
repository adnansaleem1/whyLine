using System.Diagnostics;
using WhyLine.Agents.Abstractions;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Pipeline;

/// <summary>
/// Messages that flow between newsroom agents. They are the explicit interface contracts between agents, and the edge
/// types of the Agent Framework workflow graph (see WhyLine.Agents.Workflows).
/// </summary>
public enum DraftStage { Narrate, Localize, Recap }

/// <summary>Entry message: a moment detected by the stats engine, plus the live clock when it was queued.</summary>
public sealed record MomentWork(MomentContext Context, double LiveClock);

/// <summary>Director → Analyst: a moment approved for air, with its audience. Only approved moments reach a model.</summary>
public sealed record DirectedMoment(MomentContext Context, DirectorDecision Decision, long StartedTimestamp);

/// <summary>One viewer's slice of the work: everything an author agent needs for that viewer.</summary>
public sealed record ViewerWork(
    ViewerProfile Profile,
    double MatchSeconds,
    Moment? Moment,
    MomentContext? Context,
    NarrationRequest? Narration,
    RecapRequest? Recap,
    DirectorDecision? Decision,
    long StartedTimestamp)
{
    public string MomentId => Moment?.Id ?? "recap";
    public double ElapsedMs => Stopwatch.GetElapsedTime(StartedTimestamp).TotalMilliseconds;
}

/// <summary>Director or Verifier → an author agent (Narrator, Localizer or Recap). Carries Verifier feedback on retries.</summary>
public sealed record AuthorTask(ViewerWork Work, DraftStage Stage, int Attempt, string? Feedback, NarrativeDraft? Source);

/// <summary>Author agent → Verifier.</summary>
public sealed record DraftForReview(AuthorTask Task, NarrativeDraft Draft, bool IsFallback);

/// <summary>Author or Verifier → Fallback: the model was unavailable or retries ran out.</summary>
public sealed record FallbackTask(AuthorTask Task, string Reason);

/// <summary>Verifier → Publisher: a draft that passed every check.</summary>
public sealed record ApprovedDraft(ViewerWork Work, DraftStage Stage, NarrativeDraft Draft);
