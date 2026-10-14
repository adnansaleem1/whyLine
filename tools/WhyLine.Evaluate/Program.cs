// WhyLine evaluation: plays fixtures through the Foundry newsroom, then scores every published card.
//
//   dotnet run --project tools/WhyLine.Evaluate -- [--matches comeback,thriller] [--moments 3] [--safety] [--judge gpt-5.5] [--out eval]
//
// Quality metrics come from Microsoft.Extensions.AI.Evaluation (LLM-as-judge on your Foundry deployment). With --safety,
// Foundry's content-safety evaluation service scores the cards too (needs Microsoft Entra sign-in: az login).
// Results land in eval/: summary.md, results.json, and a store you can turn into an HTML report with `aieval report`.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using Microsoft.Extensions.AI.Evaluation.Safety;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Foundry;
using WhyLine.Agents.Pipeline;
using WhyLine.Agents.Template;
using WhyLine.Core.Domain;
using WhyLine.Core.Engine;
using WhyLine.Core.Simulation;
using WhyLine.Evaluate;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var cli = Cli.Parse(args);
var root = Cli.FindRepoRoot();
var web = Path.Combine(root, "src", "WhyLine.Web");

var config = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(web, "appsettings.json"), optional: false)
    .AddJsonFile(Path.Combine(web, "appsettings.Local.json"), optional: true)
    .AddUserSecrets(typeof(Cli).Assembly, optional: true)
    .AddEnvironmentVariables()
    .Build();

var agentOptions = config.GetSection(AgentOptions.Section).Get<AgentOptions>() ?? new AgentOptions();
agentOptions.ScreenBudgetSeconds = 0; // evaluate every selected moment; the screen-time budget is a live-play concern
agentOptions.AgentTimeoutSeconds = Math.Max(agentOptions.AgentTimeoutSeconds, 45);
var factory = new FoundryAgentFactory(agentOptions.Foundry);
var judgeModel = cli.Judge ?? agentOptions.Foundry.ReasoningModel;
var judge = factory.CreateChatClient(judgeModel);
var outDir = Path.GetFullPath(Path.Combine(root, cli.Out));
Directory.CreateDirectory(outDir);

Console.WriteLine($"WhyLine evaluation · agents {agentOptions.Foundry.ReasoningModel} / {agentOptions.Foundry.FastModel} ({factory.AuthMode}) · judge {judgeModel}");

ViewerProfile[] profiles =
[
    new(Persona.Analyst, "en", null, null),
    new(Persona.Casual, "en", TeamSide.Home, null),
    new(Persona.Casual, "ur", TeamSide.Away, null)
];

// ── 1. Generate: play each fixture and run the selected moments through the Foundry newsroom ──
var cards = new List<Card>();
var scenarios = cli.Matches.Count == 0 ? MatchCatalog.All : cli.Matches.Select(MatchCatalog.ById).Distinct().ToList();
foreach (var scenario in scenarios)
{
    var sw = Stopwatch.StartNew();
    var played = Played.Run(scenario, seed: 7);
    var selected = played.Pick(cli.Moments);
    var sink = new CaptureSink();
    var steps = new NewsroomSteps(new FoundryAnalystAgent(factory), new DirectorAgent(agentOptions), new FoundryNarratorAgent(factory),
        new FoundryLocalizationAgent(factory), new FoundryRecapAgent(factory), new ClaimVerifier(), sink, new FixedProfiles(profiles),
        agentOptions, NullLogger<NewsroomSteps>.Instance) { MomentsSoFar = () => played.Moments.Select(x => x.Moment).ToArray() };
    var newsroom = new NewsroomPipeline(steps);

    foreach (var (moment, snapshot) in selected)
        await newsroom.ProcessAsync(played.Context(moment, snapshot), moment.MatchSeconds, CancellationToken.None);

    var added = 0;
    foreach (var cue in sink.Cues.Where(c => c.Source == "agents"))
    {
        var profile = profiles.First(p => p.Key == cue.ProfileKey);
        var (moment, snapshot) = selected.First(x => x.Moment.Id == cue.MomentId);
        cards.Add(Card.From(scenario, played, moment, snapshot, profile, cue, sink.Traces));
        added++;
    }

    Console.WriteLine($"  {scenario.Id,-15} {selected.Count} moments → {added} verified cards in {sw.Elapsed.TotalSeconds:0}s");
}

// ── 2. Evaluate ──
var factChecks = new ConcurrentDictionary<string, Card>(cards.GroupBy(c => c.ResponseText).ToDictionary(g => g.Key, g => g.First()));
var evaluators = new List<IEvaluator>
{
    new FactTraceEvaluator(new ClaimVerifier(), factChecks),
    new GroundednessEvaluator(),
    new RelevanceEvaluator(),
    new CoherenceEvaluator(),
    new FluencyEvaluator()
};

ChatConfiguration chat = new(judge);
if (cli.Safety)
{
    var safety = new ContentSafetyServiceConfiguration(new DefaultAzureCredential(), new Uri(agentOptions.Foundry.ProjectEndpoint));
    chat = safety.ToChatConfiguration(originalChatClient: judge);
    evaluators.AddRange([new ViolenceEvaluator(), new HateAndUnfairnessEvaluator(), new ProtectedMaterialEvaluator()]);
}

var execution = DateTime.Now.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture);
var storePath = Path.Combine(outDir, "store");
var reporting = DiskBasedReportingConfiguration.Create(
    storageRootPath: storePath,
    evaluators: evaluators,
    chatConfiguration: chat,
    enableResponseCaching: true,
    executionName: execution);

Console.WriteLine($"Evaluating {cards.Count} cards with {evaluators.Count} evaluators…");
var rows = new ConcurrentBag<Row>();
using var gate = new SemaphoreSlim(4);
await Task.WhenAll(cards.Select(async card =>
{
    await gate.WaitAsync();
    try
    {
        await using var run = await reporting.CreateScenarioRunAsync(card.ScenarioName, card.IterationName);
        var result = await run.EvaluateAsync(card.Messages, card.Response,
            additionalContext: [new GroundednessEvaluatorContext(card.GroundingText)]);
        rows.Add(Row.From(card, result));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ! {card.ScenarioName}/{card.IterationName}: {ex.Message}");
    }
    finally
    {
        gate.Release();
    }
}));

// ── 3. Report ──
var ordered = rows.OrderBy(r => r.Scenario).ThenBy(r => r.Minute).ThenBy(r => r.Viewer).ToList();
var summary = Report.Markdown(ordered, cards, agentOptions, judgeModel, execution, cli.Safety);
await File.WriteAllTextAsync(Path.Combine(outDir, "summary.md"), summary);
await File.WriteAllTextAsync(Path.Combine(outDir, "results.json"),
    JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine();
Console.WriteLine(Report.Console(ordered, cards));
Console.WriteLine($"Written: {Path.Combine(cli.Out, "summary.md")}, {Path.Combine(cli.Out, "results.json")}");
Console.WriteLine($"HTML report: dotnet tool run aieval report --path {Path.Combine(cli.Out, "store")} --output {Path.Combine(cli.Out, "report.html")}");

namespace WhyLine.Evaluate
{
    internal sealed record Cli(IReadOnlyList<string> Matches, int Moments, bool Safety, string? Judge, string Out)
    {
        public static Cli Parse(string[] args)
        {
            string? Value(string name)
            {
                var i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }

            var matches = Value("--matches") is { } m && m != "all" ? m.Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
            return new Cli(matches, int.TryParse(Value("--moments"), out var n) ? Math.Clamp(n, 1, 12) : 3, args.Contains("--safety"),
                Value("--judge"), Value("--out") ?? "eval");
        }

        public static string FindRepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "WhyLine.slnx"))) return dir.FullName;
            return Directory.GetCurrentDirectory();
        }
    }

    internal sealed class FixedProfiles(IReadOnlyCollection<ViewerProfile> profiles) : IProfileSource
    {
        public IReadOnlyCollection<ViewerProfile> ActiveProfiles() => profiles;
    }

    internal sealed class CaptureSink : INewsroomSink
    {
        public List<AgentTrace> Traces { get; } = [];
        public List<OverlayCue> Cues { get; } = [];
        public void Trace(AgentTrace trace) { lock (this) Traces.Add(trace); }
        public void Publish(OverlayCue cue) { lock (this) Cues.Add(cue); }
    }

    /// <summary>A fixture played to full time, with the match state captured at each moment.</summary>
    internal sealed class Played
    {
        public required MatchScript Script { get; init; }
        public required List<(Moment Moment, MatchSnapshot Snapshot)> Moments { get; init; }
        public required Dictionary<string, MatchEvent> Events { get; init; }

        public static Played Run(MatchScenario scenario, int seed)
        {
            var script = scenario.Build(seed);
            var sim = new MatchSimulator(script);
            var analyzer = new MatchAnalyzer(script.Metadata);
            var moments = new List<(Moment, MatchSnapshot)>();
            var events = new Dictionary<string, MatchEvent>();
            while (!sim.Finished)
            {
                var step = sim.Step();
                analyzer.IngestFrame(step.Frame);
                var found = new List<Moment>();
                foreach (var e in step.Events)
                {
                    events[e.Id] = e;
                    found.AddRange(analyzer.Ingest(e));
                }

                found.AddRange(analyzer.Tick(step.Frame.MatchSeconds));
                if (found.Count > 0)
                {
                    var snapshot = analyzer.Snapshot(step.Frame.MatchSeconds);
                    moments.AddRange(found.Select(m => (m, snapshot)));
                }
            }

            return new Played { Script = script, Moments = moments, Events = events };
        }

        /// <summary>Every goal first, then the most important other moments, plus full time (a recap).</summary>
        public List<(Moment Moment, MatchSnapshot Snapshot)> Pick(int count)
        {
            var story = Moments.Where(x => x.Moment.Kind is not (MomentKind.HalfTime or MomentKind.FullTime))
                .OrderByDescending(x => x.Moment.Kind == MomentKind.Goal)
                .ThenByDescending(x => x.Moment.Importance)
                .Take(count);
            return story.Concat(Moments.Where(x => x.Moment.Kind == MomentKind.FullTime))
                .OrderBy(x => x.Moment.MatchSeconds).ToList();
        }

        public MomentContext Context(Moment m, MatchSnapshot snapshot) =>
            new(m, snapshot, Script.Metadata, m.EvidenceEventIds.Select(id => Events[id]).ToArray());
    }

    /// <summary>One published card, with everything an evaluator needs to judge it.</summary>
    internal sealed class Card
    {
        public required string Scenario { get; init; }
        public required string ScenarioName { get; init; }
        public required string IterationName { get; init; }
        public required Moment Moment { get; init; }
        public required ViewerProfile Profile { get; init; }
        public required OverlayCue Cue { get; init; }
        public required NarrativeDraft Draft { get; init; }
        public required IReadOnlyList<Fact> Facts { get; init; }
        public required IReadOnlyList<int?> PlayerIds { get; init; }
        public required MatchMetadata Meta { get; init; }
        public required string GroundingText { get; init; }
        public required int Rejections { get; init; }
        public required bool Fallback { get; init; }

        public string ResponseText => $"{Cue.Title}\n{Cue.Body}";
        public IList<ChatMessage> Messages => [new(ChatRole.User, Query)];
        public ChatResponse Response => new(new ChatMessage(ChatRole.Assistant, ResponseText));

        public string Query
        {
            get
            {
                var language = Languages.Supported.GetValueOrDefault(Profile.Language, Profile.Language);
                var viewer = Profile.Persona switch
                {
                    Persona.Analyst => "a tactically minded football viewer",
                    Persona.Casual => "a casual football fan",
                    Persona.PlayerFocus => "a viewer following one player",
                    _ => "a blind or partially sighted football viewer"
                };
                return Moment.Kind == MomentKind.FullTime
                    ? $"Write a short full-time recap of {Meta.Home.Name} v {Meta.Away.Name}, in {language}, for {viewer}: what happened and why it went that way."
                    : $"Write a short on-screen card, in {language}, for {viewer}, about the {Kind(Moment.Kind)} that just happened in " +
                      $"{Meta.Home.Name} v {Meta.Away.Name}: what happened and why it matters. The card is shown next to the match clock.";
            }
        }

        private static string Kind(MomentKind kind) => kind switch
        {
            MomentKind.BigChance => "big chance",
            MomentKind.MomentumSwing => "momentum swing",
            MomentKind.PressSurge => "pressing surge",
            MomentKind.ControlSpell => "spell of control",
            MomentKind.TacticalShift => "tactical change",
            MomentKind.DifficultPass => "difficult pass",
            MomentKind.PassStreak => "passing move",
            MomentKind.ShotSpeed => "powerful shot",
            _ => kind.ToString().ToLowerInvariant()
        };

        public static Card From(MatchScenario scenario, Played played, Moment moment, MatchSnapshot snapshot, ViewerProfile profile,
            OverlayCue cue, IReadOnlyList<AgentTrace> traces)
        {
            var meta = played.Script.Metadata;
            IReadOnlyList<Fact> facts;
            IReadOnlyList<int?> players;
            if (moment.Kind == MomentKind.FullTime)
            {
                var request = new RecapRequest(RecapKind.FullTime, profile, meta, snapshot, played.Moments.Select(x => x.Moment).ToArray());
                facts = TemplateRecapAgent.RecapFacts(request);
                players = TemplateRecapAgent.KeyMoments(request).SelectMany(m => new[] { m.PlayerId, m.SecondaryPlayerId }).ToArray();
            }
            else
            {
                facts = moment.Facts.Concat(NewsroomSteps.ProfileFacts(played.Context(moment, snapshot), profile)).ToArray();
                players = [moment.PlayerId, moment.SecondaryPlayerId, profile.FocusPlayerId];
            }

            var mine = traces.Where(t => t.MomentId == moment.Id && (t.ProfileKey == profile.Key || t.ProfileKey is null)).ToArray();
            var viewer = $"{profile.Persona}-{profile.Language}" + (profile.FavouriteTeam is { } fav ? $"-{fav}" : "");
            return new Card
            {
                Scenario = scenario.Id,
                ScenarioName = $"WhyLine.{scenario.Id}.{viewer}",
                IterationName = $"{moment.Minute:00}m-{moment.Kind}",
                Moment = moment,
                Profile = profile,
                Cue = cue,
                Draft = new NarrativeDraft(cue.Title, cue.Citations.Select(c => c.Text).ToArray(), "published"),
                Facts = facts,
                PlayerIds = players,
                Meta = meta,
                GroundingText = Grounding(meta, snapshot, moment, facts, players),
                Rejections = mine.Count(t => t.Agent == "Verifier" && t.Status == TraceStatus.Rejected && t.ProfileKey == profile.Key),
                Fallback = mine.Any(t => t.Status == TraceStatus.Fallback)
            };
        }

        /// <summary>The source of truth a card may draw on: the stats engine's facts, the clubs and the players involved.</summary>
        private static string Grounding(MatchMetadata meta, MatchSnapshot s, Moment m, IReadOnlyList<Fact> facts, IReadOnlyList<int?> players)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Match: {meta.Home.Name} (home) v {meta.Away.Name} (away), {meta.Venue}.");
            sb.AppendLine($"Score at {m.Minute}': {meta.Home.Name} {s.HomeScore}-{s.AwayScore} {meta.Away.Name}. Game state: {s.ControlLabel}.");
            if (m.Team is { } team) sb.AppendLine($"Moment: {m.Kind} for {meta.TeamOf(team).Name}.");
            var names = players.Select(meta.PlayerById).OfType<Player>().Distinct()
                .Select(p => $"{p.Name} ({meta.TeamOf(p.Team).Name}, {p.Position}, #{p.Number})").ToArray();
            if (names.Length > 0) sb.AppendLine($"Players involved: {string.Join("; ", names)}.");
            sb.AppendLine("Computed facts:");
            foreach (var f in facts) sb.AppendLine($"- {f.Label}: {(f.Number is null ? f.Text : f.Display)}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// WhyLine's own deterministic check, run as an evaluator: every number and player name in the card must trace to a
    /// computed fact. Published cards have already passed it live; this proves it independently in the report.
    /// </summary>
    internal sealed class FactTraceEvaluator(ClaimVerifier verifier, IReadOnlyDictionary<string, Card> cards) : IEvaluator
    {
        public const string MetricName = "Fact trace";

        public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

        public ValueTask<EvaluationResult> EvaluateAsync(IEnumerable<ChatMessage> messages, ChatResponse modelResponse,
            ChatConfiguration? chatConfiguration = null, IEnumerable<EvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default)
        {
            BooleanMetric metric;
            if (!cards.TryGetValue(modelResponse.Text, out var card))
            {
                metric = new BooleanMetric(MetricName, value: null, reason: "No facts were recorded for this card.");
                metric.Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Unknown, failed: true, reason: metric.Reason);
            }
            else
            {
                var result = verifier.Verify(card.Draft, card.Facts, card.Meta, card.PlayerIds, card.Profile.Language == "en" ? null : card.Profile.Language);
                metric = new BooleanMetric(MetricName, value: result.Passed,
                    reason: result.Passed ? "Every number and player name traces to a computed fact." : result.Feedback);
                metric.Interpretation = result.Passed
                    ? new EvaluationMetricInterpretation(EvaluationRating.Exceptional, failed: false, reason: metric.Reason)
                    : new EvaluationMetricInterpretation(EvaluationRating.Unacceptable, failed: true, reason: metric.Reason);
            }

            return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
        }
    }

    internal sealed record Metric(string Name, double? Value, bool Failed, string? Reason);

    internal sealed record Row(string Scenario, int Minute, string Moment, string Viewer, string Language, string Title, string Body,
        int VerifierRejections, bool Fallback, IReadOnlyList<Metric> Metrics)
    {
        public static Row From(Card card, EvaluationResult result) => new(
            card.Scenario, card.Moment.Minute, card.Moment.Kind.ToString(), card.ScenarioName.Split('.')[^1], card.Profile.Language,
            card.Cue.Title, card.Cue.Body, card.Rejections, card.Fallback,
            result.Metrics.Values.Select(m => new Metric(m.Name, m switch
            {
                NumericMetric n => n.Value,
                BooleanMetric b => b.Value is null ? null : b.Value.Value ? 1 : 0,
                _ => null
            }, m.Interpretation?.Failed ?? false, m.Reason)).ToArray());
    }

    internal static class Report
    {
        private static readonly string[] Quality = ["Fact trace", "Groundedness", "Relevance", "Coherence", "Fluency"];

        private static IEnumerable<(string Name, double? Mean, int Failed, int Count)> Aggregate(IEnumerable<Row> rows) =>
            rows.SelectMany(r => r.Metrics).GroupBy(m => m.Name)
                .OrderBy(g => Array.IndexOf(Quality, g.Key) is var i && i >= 0 ? i : 99).ThenBy(g => g.Key)
                .Select(g => (g.Key, g.Any(m => m.Value is not null) ? g.Where(m => m.Value is not null).Average(m => m.Value!.Value) : (double?)null,
                    g.Count(m => m.Failed), g.Count()));

        private static string Score(string name, double? mean) => mean is null ? "–"
            : name == "Fact trace" ? $"{mean * 100:0}% pass"
            : $"{mean:0.00}";

        public static string Console(IReadOnlyList<Row> rows, IReadOnlyList<Card> cards)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{"Metric",-24} {"Score",10} {"Failed",8}");
            foreach (var (name, mean, failed, count) in Aggregate(rows))
                sb.AppendLine($"{name,-24} {Score(name, mean),10} {$"{failed}/{count}",8}");
            sb.AppendLine($"First-draft acceptance: {FirstDraft(cards)}  ·  Fallbacks: {cards.Count(c => c.Fallback)}");
            return sb.ToString();
        }

        private static string FirstDraft(IReadOnlyList<Card> cards) =>
            cards.Count == 0 ? "–" : $"{cards.Count(c => c.Rejections == 0) * 100.0 / cards.Count:0}% ({cards.Sum(c => c.Rejections)} drafts sent back by the Verifier)";

        public static string Markdown(IReadOnlyList<Row> rows, IReadOnlyList<Card> cards, AgentOptions agents, string judge, string execution,
            bool safety)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# WhyLine evaluation");
            sb.AppendLine();
            sb.AppendLine($"Run `{execution}`. Agents: `{agents.Foundry.ReasoningModel}` (Analyst, Recap) and `{agents.Foundry.FastModel}` " +
                          $"(Narrator, Localizer) on Microsoft Foundry. Judge: `{judge}`. {rows.Count} published cards across " +
                          $"{rows.Select(r => r.Scenario).Distinct().Count()} fixtures, written for an English analyst, an English casual fan " +
                          "and an Urdu casual fan.");
            sb.AppendLine();
            sb.AppendLine("Quality scores are 1 (poor) to 5 (excellent) from Microsoft.Extensions.AI.Evaluation's LLM-as-judge evaluators. " +
                          "Groundedness is judged against the stats engine's computed facts only, not the Analyst's prose. Fact trace is " +
                          "WhyLine's deterministic Verifier run again independently." +
                          (safety ? " Safety metrics come from the Microsoft Foundry evaluation service (0 is best)." : ""));
            sb.AppendLine();
            sb.AppendLine("## Overall");
            sb.AppendLine();
            sb.AppendLine("| Metric | Score | Failed |");
            sb.AppendLine("|---|---|---|");
            foreach (var (name, mean, failed, count) in Aggregate(rows))
                sb.AppendLine($"| {name} | {Score(name, mean)} | {failed}/{count} |");
            sb.AppendLine();
            sb.AppendLine($"First-draft acceptance: {FirstDraft(cards)}. Template fallbacks: {cards.Count(c => c.Fallback)}.");
            sb.AppendLine();

            var names = Aggregate(rows).Select(a => a.Name).ToArray();
            void Breakdown(string title, Func<Row, string> key)
            {
                sb.AppendLine($"## By {title}");
                sb.AppendLine();
                sb.AppendLine($"| {title} | Cards | {string.Join(" | ", names)} |");
                sb.AppendLine($"|---|---|{string.Concat(names.Select(_ => "---|"))}");
                foreach (var g in rows.GroupBy(key).OrderBy(g => g.Key))
                {
                    var agg = Aggregate(g).ToDictionary(a => a.Name);
                    sb.AppendLine($"| {g.Key} | {g.Count()} | {string.Join(" | ", names.Select(n => agg.TryGetValue(n, out var a) ? Score(n, a.Mean) : "–"))} |");
                }

                sb.AppendLine();
            }

            Breakdown("fixture", r => r.Scenario);
            Breakdown("viewer", r => r.Viewer);

            var flagged = rows.SelectMany(r => r.Metrics.Where(m => m.Failed).Select(m => (r, m))).ToList();
            sb.AppendLine("## Flagged cards");
            sb.AppendLine();
            if (flagged.Count == 0) sb.AppendLine("None.");
            foreach (var (r, m) in flagged)
                sb.AppendLine($"- **{r.Scenario} {r.Minute}' {r.Moment}, {r.Viewer}**, {m.Name} {m.Value?.ToString("0.#") ?? "?"}: " +
                              $"\"{r.Title}\" — {Trim(m.Reason)}");
            sb.AppendLine();
            sb.AppendLine("## Sample cards");
            sb.AppendLine();
            foreach (var r in rows.Where(r => r.Moment is "Goal" or "FullTime").GroupBy(r => r.Scenario).Select(g => g.First()))
                sb.AppendLine($"- **{r.Scenario} {r.Minute}' {r.Moment}, {r.Viewer}**: **{r.Title}** {r.Body}");
            return sb.ToString();
        }

        private static string Trim(string? s) => s is null ? "" : s.Length > 240 ? s[..240] + "…" : s.ReplaceLineEndings(" ");
    }
}
