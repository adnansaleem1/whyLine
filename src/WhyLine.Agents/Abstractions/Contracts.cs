using System.Text.Json.Serialization;
using WhyLine.Core.Domain;
using WhyLine.Core.Engine;

namespace WhyLine.Agents.Abstractions;

/// <summary>Everything an agent may know about a moment. Agents never see raw data they could misread.</summary>
public sealed record MomentContext(
    Moment Moment,
    MatchSnapshot Snapshot,
    MatchMetadata Meta,
    IReadOnlyList<MatchEvent> Evidence);

/// <summary>The Analyst's explanation of why a moment matters.</summary>
public sealed record AnalystBrief(
    string Headline,
    string Why,
    string WatchNext,
    IReadOnlyList<Fact> Facts,
    string Author);

public sealed record NarrationRequest(MomentContext Context, AnalystBrief Brief, ViewerProfile Profile, IReadOnlyList<Fact> ProfileFacts)
{
    /// <summary>Every fact the narrative may use: the Analyst's facts plus viewer-specific facts (e.g. the focus player's numbers).</summary>
    public IReadOnlyList<Fact> AllFacts => Brief.Facts.Concat(ProfileFacts).ToArray();
}

/// <summary>A narrative: a title and sentences. Each sentence becomes a citation linked to evidence.</summary>
public sealed record NarrativeDraft(string Title, IReadOnlyList<string> Sentences, string Author)
{
    public string FullText => string.Join(" ", Sentences.Prepend(Title));
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecapKind { HalfTime, FullTime, CatchMeUp }

public sealed record RecapRequest(
    RecapKind Kind,
    ViewerProfile Profile,
    MatchMetadata Meta,
    MatchSnapshot Snapshot,
    IReadOnlyList<Moment> Moments);

public interface IAnalystAgent
{
    Task<AnalystBrief> BriefAsync(MomentContext context, CancellationToken ct);
}

public interface INarratorAgent
{
    /// <param name="verifierFeedback">Issues raised by the Verifier on the previous draft, if any.</param>
    Task<NarrativeDraft> NarrateAsync(NarrationRequest request, string? verifierFeedback, CancellationToken ct);
}

public interface ILocalizationAgent
{
    Task<NarrativeDraft> LocalizeAsync(NarrativeDraft draft, NarrationRequest request, string? verifierFeedback, CancellationToken ct);
}

public interface IRecapAgent
{
    Task<NarrativeDraft> RecapAsync(RecapRequest request, string? verifierFeedback, CancellationToken ct);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TraceStatus { Info, Working, Approved, Rejected, Retry, Published, Fallback, Error }

/// <summary>One step in the newsroom, shown live in the Agent Trace panel.</summary>
public sealed record AgentTrace(
    string Id,
    DateTimeOffset At,
    double MatchSeconds,
    string Agent,
    TraceStatus Status,
    string Detail,
    string? MomentId = null,
    string? ProfileKey = null,
    double? DurationMs = null);

/// <summary>Where the newsroom sends its output (implemented by the web host).</summary>
public interface INewsroomSink
{
    void Trace(AgentTrace trace);
    void Publish(OverlayCue cue);
}

/// <summary>The viewer profiles currently watching. The newsroom writes one narrative per active profile.</summary>
public interface IProfileSource
{
    IReadOnlyCollection<ViewerProfile> ActiveProfiles();
}

public sealed class AgentOptions
{
    public const string Section = "Agents";

    /// <summary>"Template" (offline, deterministic) or "Foundry" (Microsoft Foundry models via Agent Framework).</summary>
    public string Mode { get; set; } = "Template";

    /// <summary>"Workflow" (Microsoft Agent Framework workflow graph, default) or "InProcess" (same steps, in-process runner).</summary>
    public string Orchestration { get; set; } = "Workflow";

    public int MaxVerifierRetries { get; set; } = 2;
    public int AgentTimeoutSeconds { get; set; } = 20;

    /// <summary>How many moments the newsroom works on at once, so one slow model call does not hold up the next moment.</summary>
    public int MaxConcurrentMoments { get; set; } = 4;

    /// <summary>Minimum match seconds between two insight cards (the Director's screen-time budget).</summary>
    public double ScreenBudgetSeconds { get; set; } = 40;

    public FoundryOptions Foundry { get; set; } = new();
}

public sealed class FoundryOptions
{
    /// <summary>Foundry project endpoint, e.g. https://my-resource.services.ai.azure.com/api/projects/my-project</summary>
    public string ProjectEndpoint { get; set; } = "";

    /// <summary>Stronger model deployment for the Analyst and Recap agents.</summary>
    public string ReasoningModel { get; set; } = "gpt-5.5";

    /// <summary>
    /// "Auto" (default: API key when one is set, otherwise Microsoft Entra ID), "ApiKey" or "EntraId". The Azure deployment
    /// sets EntraId so the app signs in with its managed identity and no key is stored in the cloud.
    /// </summary>
    public string Auth { get; set; } = "Auto";

    /// <summary>
    /// Optional API key for the Foundry resource. When set, agents call the resource's OpenAI v1 endpoint with the key
    /// instead of signing in with Microsoft Entra ID. Keep it in user secrets or appsettings.Local.json, never in source.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Optional OpenAI v1 endpoint for API-key access, e.g. https://my-resource.openai.azure.com/openai/v1/.
    /// When empty it is derived from <see cref="ProjectEndpoint"/>.
    /// </summary>
    public string? OpenAIEndpoint { get; set; }

    /// <summary>The OpenAI v1 endpoint to use with an API key.</summary>
    public string? ResolveOpenAIEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(OpenAIEndpoint)) return OpenAIEndpoint.TrimEnd('/') + "/";
        if (!Uri.TryCreate(ProjectEndpoint, UriKind.Absolute, out var project)) return null;
        var resource = project.Host.Split('.')[0];
        return $"https://{resource}.openai.azure.com/openai/v1/";
    }

    /// <summary>Fast model deployment for Narrators and Localization.</summary>
    public string FastModel { get; set; } = "gpt-5.4-mini";

    /// <summary>
    /// Reasoning effort for the Reasoning tier: None, Low, Medium, High or ExtraHigh (empty = the model's default).
    /// Low keeps the Analyst within a few seconds, which live play needs; the work is explanation, not deep reasoning.
    /// </summary>
    public string? ReasoningEffort { get; set; } = "Low";

    /// <summary>
    /// Record prompts and completions on the model-call spans (OpenTelemetry GenAI "sensitive data"). WhyLine only sends
    /// synthetic match data and viewer preferences, so this is on by default; it lets Foundry tracing show what each
    /// agent was asked and what it wrote.
    /// </summary>
    public bool RecordContentInTraces { get; set; } = true;

    /// <summary>Reasoning effort for the Fast tier (Narrators, Localizer). Same values as <see cref="ReasoningEffort"/>.</summary>
    public string? FastReasoningEffort { get; set; } = "Low";
}
