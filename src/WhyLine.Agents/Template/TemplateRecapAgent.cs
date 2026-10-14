using WhyLine.Agents.Abstractions;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Template;

/// <summary>Offline Recap agent: half-time, full-time and "catch me up" summaries from the verified moment log.</summary>
public sealed class TemplateRecapAgent : IRecapAgent
{
    public Task<NarrativeDraft> RecapAsync(RecapRequest request, string? verifierFeedback, CancellationToken ct) =>
        Task.FromResult(Recap(request));

    public static IReadOnlyList<Moment> KeyMoments(RecapRequest r, int max = 5) => r.Moments
        .Where(m => m.MatchSeconds <= r.Snapshot.MatchSeconds && m.Kind is not (MomentKind.HalfTime or MomentKind.FullTime))
        .Select(m => (Moment: m, Score: m.Importance + Pipeline.DirectorAgent.Relevance(m, r.Profile)))
        .Where(x => x.Score >= 0.5)
        .OrderByDescending(x => x.Score).Take(max)
        .Select(x => x.Moment).OrderBy(m => m.MatchSeconds).ToArray();

    public static NarrativeDraft Recap(RecapRequest r)
    {
        var snap = r.Snapshot;
        var score = $"{snap.HomeScore}-{snap.AwayScore}";
        var minute = snap.Minute.ToString();
        var key = KeyMoments(r);

        var sentences = new List<string>();
        foreach (var m in key)
        {
            var brief = TemplateAnalystAgent.Brief(new MomentContext(m, snap, r.Meta, []));
            if (r.Profile.Language != "en" && Localized(r.Profile.Language, m) is { } local)
                sentences.Add($"{m.Minute}' {local}");
            else
                sentences.Add($"{m.Minute}': {brief.Headline}.");
        }

        if (r.Profile.Language == "en")
        {
            sentences.Add($"Possession {Math.Round(snap.Home.PossessionPct)}% to {Math.Round(snap.Away.PossessionPct)}%, shots {snap.Home.Shots} to {snap.Away.Shots}, xG {snap.Home.Xg:0.00} to {snap.Away.Xg:0.00}.");
            sentences.Add(snap.ControlLabel switch
            {
                "Control" => "Right now the game is controlled and patient.",
                "Chaos" => "Right now the game is chaotic, with the ball changing hands constantly.",
                _ => "Right now the game is evenly balanced."
            });
        }

        var title = r.Kind switch
        {
            RecapKind.HalfTime => $"Half-time recap: {snap.Home.Name} {score} {snap.Away.Name}",
            RecapKind.FullTime => $"Full-time recap: {snap.Home.Name} {score} {snap.Away.Name}",
            _ => Template.TemplateLocalizationAgent.CatchMeUpTitle(r.Profile.Language, score, minute)
        };
        if (sentences.Count == 0) sentences.Add(r.Profile.Language == "en" ? "No major moments yet." : "—");
        return new NarrativeDraft(title, sentences, "template-recap");
    }

    private static string? Localized(string lang, Moment m) =>
        TemplateLocalizationAgent.Line(lang, m.Kind, new FactReader(m.Facts))?.Title;

    /// <summary>Facts a recap may use: every key moment's facts plus the live snapshot numbers.</summary>
    public static IReadOnlyList<Fact> RecapFacts(RecapRequest r)
    {
        var s = r.Snapshot;
        var facts = new List<Fact>
        {
            new("live_minute", "Minute", s.Minute),
            new("home_score", "Home goals", s.HomeScore),
            new("away_score", "Away goals", s.AwayScore),
            new("home_possession", "Home possession", Math.Round(s.Home.PossessionPct), Unit: "%"),
            new("away_possession", "Away possession", Math.Round(s.Away.PossessionPct), Unit: "%"),
            new("home_shots", "Home shots", s.Home.Shots),
            new("away_shots", "Away shots", s.Away.Shots),
            new("home_xg", "Home xG", Math.Round(s.Home.Xg, 2), Unit: "xG"),
            new("away_xg", "Away xG", Math.Round(s.Away.Xg, 2), Unit: "xG"),
            new("home_pass_acc", "Home pass accuracy", Math.Round(s.Home.PassAccuracyPct), Unit: "%"),
            new("away_pass_acc", "Away pass accuracy", Math.Round(s.Away.PassAccuracyPct), Unit: "%")
        };
        foreach (var m in r.Moments.Where(m => m.MatchSeconds <= s.MatchSeconds))
        {
            facts.Add(new Fact($"{m.Id}_minute", "Minute", m.Minute));
            facts.AddRange(m.Facts);
        }

        return facts;
    }
}
