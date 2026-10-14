using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Template;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Pipeline;

/// <summary>
/// The newsroom's steps, one per agent, each turning one message into the next. Both orchestrators call these:
/// <see cref="NewsroomPipeline"/> (in-process runner, used offline and in tests) and the Agent Framework workflow
/// (WhyLine.Agents.Workflows). Keeping one implementation means the workflow graph and the tests can never drift apart.
/// </summary>
public sealed class NewsroomSteps(
    IAnalystAgent analyst,
    DirectorAgent director,
    INarratorAgent narrator,
    ILocalizationAgent localizer,
    IRecapAgent recap,
    ClaimVerifier verifier,
    INewsroomSink sink,
    IProfileSource profiles,
    AgentOptions options,
    ILogger<NewsroomSteps> logger)
{
    private int _traceCounter;
    private int _cueCounter;

    /// <summary>Set by the host so half-time and full-time recaps can see the moment log.</summary>
    public Func<IReadOnlyList<Moment>> MomentsSoFar { get; set; } = () => [];

    public AgentOptions Options => options;

    // ───────────────────────────── Director ─────────────────────────────

    /// <summary>
    /// Runs first, before any model call: decides whether the moment gets air time and for whom. Returns a
    /// <see cref="DirectedMoment"/> for the Analyst, Recap <see cref="AuthorTask"/>s for half-time and full-time, or
    /// nothing when the moment is rejected (so rejected moments never cost a model call).
    /// </summary>
    public IReadOnlyList<object> Direct(MomentWork work)
    {
        var ctx = work.Context;
        var moment = ctx.Moment;
        var started = Stopwatch.GetTimestamp();
        using var span = NewsroomTelemetry.StartStep(AgentCatalog.DirectorName, "decide", moment.Id);
        var decision = director.Decide(moment, profiles.ActiveProfiles(), work.LiveClock);
        Trace(moment.MatchSeconds, "Director", decision.Approved ? TraceStatus.Approved : TraceStatus.Rejected, decision.Reason, moment.Id);
        span?.SetTag("whyline.director.approved", decision.Approved);
        span?.SetTag("whyline.director.reason", decision.Reason);
        span?.SetTag("whyline.director.audience", decision.Audience.Count);
        NewsroomTelemetry.MomentsDirected.Add(1, new KeyValuePair<string, object?>("approved", decision.Approved),
            new KeyValuePair<string, object?>("kind", moment.Kind.ToString()));
        if (!decision.Approved) return [];

        if (moment.Kind is MomentKind.HalfTime or MomentKind.FullTime)
        {
            var kind = moment.Kind == MomentKind.HalfTime ? RecapKind.HalfTime : RecapKind.FullTime;
            return decision.Audience.Select(profile =>
            {
                var request = new RecapRequest(kind, profile, ctx.Meta, ctx.Snapshot, MomentsSoFar());
                var recapWork = new ViewerWork(profile, moment.MatchSeconds, moment, ctx, null, request, decision, started);
                return (object)new AuthorTask(recapWork, DraftStage.Recap, 0, null, null);
            }).ToArray();
        }

        return [new DirectedMoment(ctx, decision, started)];
    }

    // ───────────────────────────── Analyst ─────────────────────────────

    /// <summary>Explains the moment once, then hands one Narrate task per approved viewer to the Narrator (fan-out).</summary>
    public async Task<IReadOnlyList<AuthorTask>> AnalyzeAsync(DirectedMoment directed, CancellationToken ct)
    {
        var ctx = directed.Context;
        var moment = ctx.Moment;
        var t = moment.MatchSeconds;
        using var span = NewsroomTelemetry.StartStep(AgentCatalog.AnalystName, "brief", moment.Id);
        Trace(t, "Analyst", TraceStatus.Working,
            $"Reviewing {moment.Kind} ({moment.EvidenceEventIds.Count} evidence events, importance {moment.Importance:0.00})", moment.Id);
        var sw = Stopwatch.StartNew();
        AnalystBrief brief;
        try
        {
            brief = await WithTimeout(c => analyst.BriefAsync(ctx, c), ct);
            Trace(t, "Analyst", TraceStatus.Approved, $"\"{brief.Headline}\"", moment.Id, null, sw.Elapsed.TotalMilliseconds);
            span?.SetTag("whyline.analyst.headline", brief.Headline);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Analyst failed for {Moment}", moment.Id);
            brief = TemplateAnalystAgent.Brief(ctx);
            span?.SetStatus(ActivityStatusCode.Error, Short(ex));
            span?.SetTag("whyline.fallback", true);
            NewsroomTelemetry.Fallbacks.Add(1, new KeyValuePair<string, object?>("agent", AgentCatalog.AnalystName));
            Trace(t, "Analyst", TraceStatus.Fallback, $"Model unavailable ({Short(ex)}); used rule-based brief.", moment.Id, null,
                sw.Elapsed.TotalMilliseconds);
        }

        return directed.Decision.Audience.Select(profile =>
        {
            var narration = new NarrationRequest(ctx, brief, profile, ProfileFacts(ctx, profile));
            var work = new ViewerWork(profile, moment.MatchSeconds, moment, ctx, narration, null, directed.Decision, directed.StartedTimestamp);
            return new AuthorTask(work, DraftStage.Narrate, 0, null, null);
        }).ToArray();
    }

    /// <summary>Entry task for an on-demand recap (catch me up), which skips the Analyst and Director.</summary>
    public AuthorTask RecapTask(RecapRequest request, Moment? anchor)
    {
        var work = new ViewerWork(request.Profile, request.Snapshot.MatchSeconds, anchor, null, null, request, null, Stopwatch.GetTimestamp());
        return new AuthorTask(work, DraftStage.Recap, 0, null, null);
    }

    // ───────────────────────────── Authors: Narrator, Localizer, Recap ─────────────────────────────

    /// <summary>Runs the author agent for the task's stage. Returns a <see cref="DraftForReview"/>, or a <see cref="FallbackTask"/> if the model is unavailable.</summary>
    public async Task<object> AuthorAsync(AuthorTask task, CancellationToken ct)
    {
        var w = task.Work;
        var agentName = AgentLabel(task);
        using var span = NewsroomTelemetry.StartStep(CatalogName(task.Stage), "write", w.MomentId, w.Profile);
        span?.SetTag("whyline.attempt", task.Attempt + 1);
        if (task.Attempt == 0)
        {
            var what = task.Stage switch
            {
                DraftStage.Recap => $"{w.Recap!.Kind} recap for {Describe(w.Profile)} from {TemplateRecapAgent.KeyMoments(w.Recap).Count} key moments.",
                DraftStage.Localize => $"Adapting for {Describe(w.Profile)}",
                _ => $"Drafting for {Describe(w.Profile)}"
            };
            Trace(w.MatchSeconds, agentName, TraceStatus.Working, what, w.MomentId, w.Profile.Key);
        }
        else
        {
            Trace(w.MatchSeconds, agentName, TraceStatus.Retry, $"Rewriting with Verifier feedback (attempt {task.Attempt + 1}).", w.MomentId, w.Profile.Key);
        }

        try
        {
            var draft = await WithTimeout(c => task.Stage switch
            {
                DraftStage.Narrate => narrator.NarrateAsync(w.Narration!, task.Feedback, c),
                DraftStage.Localize => localizer.LocalizeAsync(task.Source!, w.Narration!, task.Feedback, c),
                _ => recap.RecapAsync(w.Recap!, task.Feedback, c)
            }, ct);
            span?.SetTag("whyline.draft.title", draft.Title);
            return new DraftForReview(task, draft, IsFallback: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            span?.SetStatus(ActivityStatusCode.Error, Short(ex));
            span?.SetTag("whyline.fallback", true);
            NewsroomTelemetry.Fallbacks.Add(1, new KeyValuePair<string, object?>("agent", CatalogName(task.Stage)));
            Trace(w.MatchSeconds, agentName, TraceStatus.Fallback, $"Model unavailable ({Short(ex)}); using template voice.", w.MomentId, w.Profile.Key);
            return new FallbackTask(task, Short(ex));
        }
    }

    /// <summary>The deterministic template voice for the task's stage.</summary>
    public DraftForReview Fallback(FallbackTask fallback)
    {
        var task = fallback.Task;
        var w = task.Work;
        var draft = task.Stage switch
        {
            DraftStage.Narrate => TemplateNarratorAgent.Narrate(w.Narration!),
            DraftStage.Localize => TemplateLocalizationAgent.Localize(task.Source!, w.Moment!.Kind, w.Narration!.AllFacts, w.Profile.Language),
            _ => TemplateRecapAgent.Recap(w.Recap!)
        };
        return new DraftForReview(task, draft, IsFallback: true);
    }

    // ───────────────────────────── Verifier ─────────────────────────────

    /// <summary>
    /// Checks a draft and decides where it goes next: <see cref="ApprovedDraft"/> (publish), an <see cref="AuthorTask"/>
    /// (retry with feedback, or localise a verified English draft), a <see cref="FallbackTask"/> (retries used up), or
    /// null (a fallback that still fails is dropped; nothing unverified is ever published).
    /// </summary>
    public object? Review(DraftForReview review)
    {
        var task = review.Task;
        var w = task.Work;
        var agentName = AgentLabel(task);
        var (facts, players, meta) = Evidence(task);
        using var span = NewsroomTelemetry.StartStep(AgentCatalog.VerifierName, "verify", w.MomentId, w.Profile);
        var language = task.Stage == DraftStage.Narrate ? null : w.Profile.Language;
        var result = verifier.Verify(review.Draft, facts, meta, players, language);
        span?.SetTag("whyline.verifier.passed", result.Passed);
        span?.SetTag("whyline.verifier.author", agentName);
        span?.SetTag("whyline.verifier.fallback_draft", review.IsFallback);
        if (!result.Passed)
        {
            span?.SetTag("whyline.verifier.feedback", result.Feedback);
            NewsroomTelemetry.VerifierRejections.Add(1, new KeyValuePair<string, object?>("agent", CatalogName(task.Stage)));
        }

        if (result.Passed)
        {
            Trace(w.MatchSeconds, "Verifier", TraceStatus.Approved,
                review.IsFallback ? $"{agentName}: deterministic fallback verified." : $"{agentName} draft verified: every number traced to a fact.",
                w.MomentId, w.Profile.Key);

            if (task.Stage == DraftStage.Narrate && w.Profile.Language != "en")
                return new AuthorTask(w, DraftStage.Localize, 0, null, review.Draft);
            return new ApprovedDraft(w, task.Stage, review.Draft);
        }

        if (review.IsFallback)
        {
            Trace(w.MatchSeconds, "Verifier", TraceStatus.Rejected, $"Fallback issue, not published: {result.Feedback}", w.MomentId, w.Profile.Key);
            return null;
        }

        Trace(w.MatchSeconds, "Verifier", TraceStatus.Rejected, $"{agentName}: {result.Feedback}", w.MomentId, w.Profile.Key);
        return task.Attempt < options.MaxVerifierRetries
            ? task with { Attempt = task.Attempt + 1, Feedback = result.Feedback }
            : new FallbackTask(task, "Verifier retries used up");
    }

    // ───────────────────────────── Publisher ─────────────────────────────

    /// <summary>Turns an approved draft into a timed overlay cue. Does not broadcast; the orchestrator decides that.</summary>
    public OverlayCue Publish(ApprovedDraft approved)
    {
        var w = approved.Work;
        var draft = approved.Draft;
        OverlayCue cue;

        if (approved.Stage == DraftStage.Recap)
        {
            var key = TemplateRecapAgent.KeyMoments(w.Recap!);
            var evidence = key.SelectMany(m => m.EvidenceEventIds.Take(3)).Distinct().ToArray();
            cue = new OverlayCue
            {
                Id = NextCueId(),
                Type = CueType.Recap,
                MatchSeconds = w.MatchSeconds,
                DurationSeconds = w.Decision?.DurationSeconds ?? 30,
                Priority = w.Decision?.Priority ?? 3,
                ProfileKey = w.Profile.Key,
                Language = w.Profile.Language,
                Title = draft.Title,
                Body = string.Join(" ", draft.Sentences),
                Citations = draft.Sentences.Select((s, i) => new Citation(s, i < key.Count ? key[i].EvidenceEventIds : evidence)).ToArray(),
                EvidenceEventIds = evidence,
                MomentId = w.Moment?.Id,
                MomentKind = w.Moment?.Kind,
                Source = "agents",
                Verified = true,
                GenerationMs = Math.Round(w.ElapsedMs)
            };
        }
        else
        {
            var moment = w.Moment!;
            cue = new OverlayCue
            {
                Id = NextCueId(),
                Type = moment.Kind == MomentKind.MomentumSwing ? CueType.MomentumAnnotation : CueType.InsightCard,
                MatchSeconds = moment.MatchSeconds,
                DurationSeconds = w.Decision!.DurationSeconds,
                Priority = w.Decision.Priority,
                ProfileKey = w.Profile.Key,
                Language = w.Profile.Language,
                Title = draft.Title,
                Body = string.Join(" ", draft.Sentences),
                PlayerId = moment.PlayerId,
                Team = moment.Team,
                Anchor = w.Context!.Evidence.LastOrDefault()?.Start,
                Citations = draft.Sentences.Select(s => new Citation(s, moment.EvidenceEventIds)).ToArray(),
                EvidenceEventIds = moment.EvidenceEventIds,
                MomentId = moment.Id,
                MomentKind = moment.Kind,
                Source = "agents",
                Verified = true,
                GenerationMs = Math.Round(w.ElapsedMs)
            };
        }

        using (var span = NewsroomTelemetry.StartStep("whyline-publisher", "publish", w.MomentId, w.Profile))
        {
            span?.SetTag("whyline.card.title", cue.Title);
            span?.SetTag("whyline.card.latency_ms", Math.Round(w.ElapsedMs));
        }

        NewsroomTelemetry.CardsPublished.Add(1, new KeyValuePair<string, object?>("stage", approved.Stage.ToString()),
            new KeyValuePair<string, object?>("language", w.Profile.Language));
        NewsroomTelemetry.TimeToCard.Record(w.ElapsedMs, new KeyValuePair<string, object?>("stage", approved.Stage.ToString()));

        var held = w.Moment is { } m && approved.Stage != DraftStage.Recap ? $" (held until each viewer's stream reaches {m.Minute}')" : "";
        Trace(w.MatchSeconds, "Publisher", TraceStatus.Published, $"\"{cue.Title}\" → {Describe(w.Profile)}{held}", w.MomentId, w.Profile.Key, w.ElapsedMs);
        return cue;
    }

    /// <summary>Broadcasts a cue to viewers and rendering partners.</summary>
    public void Broadcast(OverlayCue cue) => sink.Publish(cue);

    // ───────────────────────────── helpers ─────────────────────────────

    private static (IReadOnlyList<Fact> Facts, IReadOnlyList<int?> Players, MatchMetadata Meta) Evidence(AuthorTask task)
    {
        var w = task.Work;
        if (task.Stage == DraftStage.Recap)
        {
            var key = TemplateRecapAgent.KeyMoments(w.Recap!);
            var players = key.SelectMany(m => new[] { m.PlayerId, m.SecondaryPlayerId }).Append(w.Profile.FocusPlayerId).ToArray();
            return (TemplateRecapAgent.RecapFacts(w.Recap!), players, w.Recap!.Meta);
        }

        var moment = w.Moment!;
        return (w.Narration!.AllFacts, [moment.PlayerId, moment.SecondaryPlayerId, w.Profile.FocusPlayerId], w.Context!.Meta);
    }

    /// <summary>Viewer-specific facts: live score and, in Player Focus mode, the focus player's numbers.</summary>
    public static IReadOnlyList<Fact> ProfileFacts(MomentContext ctx, ViewerProfile profile)
    {
        var s = ctx.Snapshot;
        var facts = new List<Fact>
        {
            new("live_score", "Live score", Text: $"{s.HomeScore}-{s.AwayScore}"),
            new("live_minute", "Live minute", ctx.Moment.Minute)
        };
        if (profile.FocusPlayerId is { } id && s.Players.FirstOrDefault(p => p.Id == id) is { } p)
        {
            facts.Add(new Fact("focus_touches", "Touches", p.Touches));
            facts.Add(new Fact("focus_passes_completed", "Completed passes", p.PassesCompleted));
            facts.Add(new Fact("focus_passes", "Passes attempted", p.Passes));
            facts.Add(new Fact("focus_distance_km", "Distance covered", p.DistanceKm, Unit: "km"));
            facts.Add(new Fact("focus_top_speed", "Top speed", p.TopSpeedKmh, Unit: "km/h"));
            facts.Add(new Fact("focus_goals", "Goals", p.Goals));
        }

        return facts;
    }

    private static string CatalogName(DraftStage stage) => stage switch
    {
        DraftStage.Narrate => AgentCatalog.NarratorName,
        DraftStage.Localize => AgentCatalog.LocalizerName,
        _ => AgentCatalog.RecapName
    };

    private static string AgentLabel(AuthorTask task) => task.Stage switch
    {
        DraftStage.Narrate => $"Narrator·{task.Work.Profile.Persona}",
        DraftStage.Localize => $"Localizer·{task.Work.Profile.Language}",
        _ => "Recap"
    };

    private async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.AgentTimeoutSeconds));
        return await work(cts.Token);
    }

    private string NextCueId() => $"c{Interlocked.Increment(ref _cueCounter):0000}";

    public static string Describe(ViewerProfile p) =>
        $"{p.Persona}/{Languages.Supported.GetValueOrDefault(p.Language, p.Language)}" +
        (p.FavouriteTeam is { } fav ? $"/{fav} fan" : "") + (p.FocusPlayerId is { } id ? $"/player #{id}" : "");

    private static string Short(Exception ex) => ex.Message.Length > 120 ? ex.Message[..120] + "…" : ex.Message;

    public void Trace(double t, string agent, TraceStatus status, string detail, string? momentId = null, string? profileKey = null, double? ms = null) =>
        sink.Trace(new AgentTrace($"t{Interlocked.Increment(ref _traceCounter):00000}", DateTimeOffset.UtcNow, t, agent, status, detail,
            momentId, profileKey, ms is null ? null : Math.Round(ms.Value)));
}
