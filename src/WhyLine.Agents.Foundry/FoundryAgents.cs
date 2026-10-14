using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Extensions.AI;
using OpenAI;
using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Pipeline;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Foundry;

/// <summary>
/// Creates the newsroom's agents on Microsoft Foundry using Microsoft Agent Framework.
/// Each role is a separate agent with its own instructions and model deployment.
/// Authentication: Microsoft Entra ID through the Foundry project endpoint (recommended), or an API key through the
/// resource's OpenAI v1 endpoint when <see cref="FoundryOptions.ApiKey"/> is set.
/// </summary>
public sealed class FoundryAgentFactory
{
    private readonly FoundryOptions _options;
    private readonly AIProjectClient? _project;
    private readonly OpenAIClient? _openAi;

    public FoundryAgentFactory(FoundryOptions options)
    {
        _options = options;
        var useKey = options.Auth.ToLowerInvariant() switch
        {
            "apikey" => true,
            "entraid" => false,
            _ => !string.IsNullOrWhiteSpace(options.ApiKey)
        };
        if (useKey && string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("Agents:Foundry:Auth is ApiKey but Agents:Foundry:ApiKey is not set.");

        if (useKey)
        {
            var endpoint = options.ResolveOpenAIEndpoint()
                ?? throw new InvalidOperationException("Set Agents:Foundry:ProjectEndpoint or Agents:Foundry:OpenAIEndpoint to use an API key.");
            _openAi = new OpenAIClient(new ApiKeyCredential(options.ApiKey), new OpenAIClientOptions { Endpoint = new Uri(endpoint) });
            AuthMode = $"API key ({new Uri(endpoint).Host})";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(options.ProjectEndpoint))
                throw new InvalidOperationException("Agents:Foundry:ProjectEndpoint is not configured.");
            // Locally: your az / Visual Studio sign-in. In Azure: the app's managed identity (AZURE_CLIENT_ID).
            _project = new AIProjectClient(new Uri(options.ProjectEndpoint), new DefaultAzureCredential());
            AuthMode = "Microsoft Entra ID";
        }
    }

    public string AuthMode { get; }

    public FoundryAgent Analyst() => Create(AgentCatalog.Analyst);
    public FoundryAgent Narrator() => Create(AgentCatalog.Narrator);
    public FoundryAgent Localizer() => Create(AgentCatalog.Localizer);
    public FoundryAgent Recap() => Create(AgentCatalog.Recap);

    /// <summary>Creates a Foundry agent from its catalog definition: name, model tier, reasoning effort and system message.</summary>
    private FoundryAgent Create(AgentDefinition definition)
    {
        if (definition.Kind != AgentKind.Model)
            throw new InvalidOperationException($"{definition.Name} is deterministic and runs in code, not on Foundry.");
        var reasoningTier = definition.ModelTier == "Reasoning";
        var deployment = reasoningTier ? _options.ReasoningModel : _options.FastModel;

        AIAgent agent = _openAi is not null
            ? _openAi.GetChatClient(deployment).AsIChatClient().AsAIAgent(instructions: definition.Instructions, name: definition.Name)
            : _project!.AsAIAgent(model: deployment, name: definition.Name, instructions: definition.Instructions);

        // GenAI-convention spans (invoke_agent, model, tokens) for every call, under the WhyLine.Agents source.
        agent = agent.AsBuilder()
            .UseOpenTelemetry(sourceName: NewsroomTelemetry.AgentsSourceName,
                configure: telemetry => telemetry.EnableSensitiveData = _options.RecordContentInTraces)
            .Build();

        var chatOptions = new ChatOptions();
        if (ParseEffort(reasoningTier ? _options.ReasoningEffort : _options.FastReasoningEffort) is { } effort)
            chatOptions.Reasoning = new ReasoningOptions { Effort = effort };
        return new FoundryAgent(agent, new ChatClientAgentRunOptions(chatOptions));
    }

    /// <summary>
    /// A plain chat client for a deployment, used as the judge model by the evaluation tool. Reasoning models reject
    /// sampling parameters, so those are cleared and reasoning effort is set from the Reasoning tier.
    /// </summary>
    public IChatClient CreateChatClient(string deployment)
    {
        if (_openAi is null)
            throw new InvalidOperationException("The evaluation judge uses the API-key endpoint. Set Agents:Foundry:ApiKey.");
        var effort = ParseEffort(_options.ReasoningEffort);
        return _openAi.GetChatClient(deployment).AsIChatClient()
            .AsBuilder()
            .ConfigureOptions(o =>
            {
                o.Temperature = null;
                o.TopP = null;
                o.PresencePenalty = null;
                o.FrequencyPenalty = null;
                if (effort is { } e) o.Reasoning ??= new ReasoningOptions { Effort = e };
            })
            .Build();
    }

    private static Microsoft.Extensions.AI.ReasoningEffort? ParseEffort(string? value) =>
        Enum.TryParse<Microsoft.Extensions.AI.ReasoningEffort>(value, ignoreCase: true, out var effort) ? effort : null;
}

/// <summary>A Foundry agent plus the run options (reasoning effort) used on every call.</summary>
public sealed record FoundryAgent(AIAgent Agent, AgentRunOptions RunOptions);

internal static class AgentCalls
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    public static async Task<JsonObject> AskJsonAsync(FoundryAgent agent, string prompt, CancellationToken ct)
    {
        var response = await agent.Agent.RunAsync(prompt, options: agent.RunOptions, cancellationToken: ct);
        var text = response.ToString().Trim();
        if (text.StartsWith("```"))
        {
            var first = text.IndexOf('\n');
            var last = text.LastIndexOf("```", StringComparison.Ordinal);
            text = last > first ? text[(first + 1)..last] : text;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new FormatException("Agent did not return JSON.");
        return JsonNode.Parse(text[start..(end + 1)])?.AsObject() ?? throw new FormatException("Empty JSON.");
    }

    public static NarrativeDraft ToDraft(JsonObject json, string author)
    {
        var title = json["title"]?.GetValue<string>() ?? throw new FormatException("Missing title.");
        var sentences = json["sentences"]?.AsArray().Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray() ?? [];
        return new NarrativeDraft(title, sentences, author);
    }

    public static object FactsPayload(IEnumerable<Fact> facts) =>
        facts.Select(f => new { key = f.Key, label = f.Label, value = f.Number is null ? f.Text : f.Display }).ToArray();
}

public sealed class FoundryAnalystAgent(FoundryAgentFactory factory) : IAnalystAgent
{
    private readonly FoundryAgent _agent = factory.Analyst();

    public async Task<AnalystBrief> BriefAsync(MomentContext context, CancellationToken ct)
    {
        var m = context.Moment;
        var prompt = AgentCalls.Serialize(new
        {
            moment = m.Kind.ToString(),
            minute = m.Minute,
            score = context.Snapshot.ScoreLine,
            gameState = context.Snapshot.ControlLabel,
            facts = AgentCalls.FactsPayload(m.Facts)
        });
        var json = await AgentCalls.AskJsonAsync(_agent, prompt, ct);
        return new AnalystBrief(
            json["headline"]?.GetValue<string>() ?? m.Kind.ToString(),
            json["why"]?.GetValue<string>() ?? "",
            json["watchNext"]?.GetValue<string>() ?? "",
            m.Facts,
            "foundry-analyst");
    }
}

public sealed class FoundryNarratorAgent(FoundryAgentFactory factory) : INarratorAgent
{
    private readonly FoundryAgent _agent = factory.Narrator();

    public async Task<NarrativeDraft> NarrateAsync(NarrationRequest request, string? verifierFeedback, CancellationToken ct)
    {
        var meta = request.Context.Meta;
        var p = request.Profile;
        var focus = meta.PlayerById(p.FocusPlayerId);
        var prompt = AgentCalls.Serialize(new
        {
            persona = p.Persona.ToString(),
            personaGuide = Prompts.PersonaGuide(p.Persona),
            favouriteTeam = p.FavouriteTeam is { } fav ? meta.TeamOf(fav).Name : null,
            focusPlayer = focus?.Name,
            moment = request.Context.Moment.Kind.ToString(),
            analyst = new { request.Brief.Headline, request.Brief.Why, request.Brief.WatchNext },
            facts = AgentCalls.FactsPayload(request.AllFacts),
            evidence = request.Context.Evidence.TakeLast(4).Select(e => new
            {
                type = e.Type.ToString(),
                team = e.Team is { } t ? meta.TeamOf(t).Name : null,
                x = Math.Round(e.Start.X),
                y = Math.Round(e.Start.Y)
            }),
            verifierFeedback
        });
        var json = await AgentCalls.AskJsonAsync(_agent, prompt, ct);
        return AgentCalls.ToDraft(json, $"foundry-narrator/{p.Persona}");
    }
}

public sealed class FoundryLocalizationAgent(FoundryAgentFactory factory) : ILocalizationAgent
{
    private readonly FoundryAgent _agent = factory.Localizer();

    public async Task<NarrativeDraft> LocalizeAsync(NarrativeDraft draft, NarrationRequest request, string? verifierFeedback, CancellationToken ct)
    {
        var lang = request.Profile.Language;
        var prompt = AgentCalls.Serialize(new
        {
            targetLanguage = Languages.Supported.GetValueOrDefault(lang, lang),
            commentaryStyle = Prompts.CommentaryStyle(lang),
            persona = request.Profile.Persona.ToString(),
            title = draft.Title,
            sentences = draft.Sentences,
            verifierFeedback
        });
        var json = await AgentCalls.AskJsonAsync(_agent, prompt, ct);
        return AgentCalls.ToDraft(json, $"foundry-localizer/{lang}");
    }
}

public sealed class FoundryRecapAgent(FoundryAgentFactory factory) : IRecapAgent
{
    private readonly FoundryAgent _agent = factory.Recap();

    public async Task<NarrativeDraft> RecapAsync(RecapRequest request, string? verifierFeedback, CancellationToken ct)
    {
        var key = Template.TemplateRecapAgent.KeyMoments(request, 6);
        var s = request.Snapshot;
        var meta = request.Meta;
        var prompt = AgentCalls.Serialize(new
        {
            kind = request.Kind.ToString(),
            language = Languages.Supported.GetValueOrDefault(request.Profile.Language, request.Profile.Language),
            commentaryStyle = Prompts.CommentaryStyle(request.Profile.Language),
            persona = request.Profile.Persona.ToString(),
            personaGuide = Prompts.PersonaGuide(request.Profile.Persona),
            favouriteTeam = request.Profile.FavouriteTeam is { } fav ? meta.TeamOf(fav).Name : null,
            focusPlayer = meta.PlayerById(request.Profile.FocusPlayerId)?.Name,
            score = $"{meta.Home.Name} {s.HomeScore}-{s.AwayScore} {meta.Away.Name}",
            minute = s.Minute,
            gameState = s.ControlLabel,
            keyMoments = key.Select(m => new { minute = m.Minute, kind = m.Kind.ToString(), facts = AgentCalls.FactsPayload(m.Facts) }),
            matchFacts = AgentCalls.FactsPayload(Template.TemplateRecapAgent.RecapFacts(request).Take(11)),
            verifierFeedback
        });
        var json = await AgentCalls.AskJsonAsync(_agent, prompt, ct);
        return AgentCalls.ToDraft(json, "foundry-recap");
    }
}
