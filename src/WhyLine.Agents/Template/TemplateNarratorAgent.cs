using WhyLine.Agents.Abstractions;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Template;

/// <summary>Offline Narrator: one voice per persona. Also the last-resort fallback when an AI draft fails verification.</summary>
public sealed class TemplateNarratorAgent : INarratorAgent
{
    public Task<NarrativeDraft> NarrateAsync(NarrationRequest request, string? verifierFeedback, CancellationToken ct) =>
        Task.FromResult(Narrate(request));

    public static NarrativeDraft Narrate(NarrationRequest r)
    {
        var m = r.Context.Moment;
        var meta = r.Context.Meta;
        var f = new FactReader(r.AllFacts);
        var brief = r.Brief;
        var firstWhy = FirstSentence(brief.Why);

        switch (r.Profile.Persona)
        {
            case Persona.Analyst:
            {
                var numbers = f.Numeric.Where(x => x.Key is not ("home_score" or "away_score") && !x.Key.StartsWith("focus_") && !x.Key.StartsWith("live_"))
                    .Take(4).Select(x => $"{x.Label}: {x.Display}");
                return new NarrativeDraft(brief.Headline,
                    new[] { brief.Why, $"Key numbers: {string.Join("; ", numbers)}.", brief.WatchNext }
                        .Where(s => !string.IsNullOrWhiteSpace(s) && s != "Key numbers: .").ToArray(),
                    "template-narrator/analyst");
            }

            case Persona.PlayerFocus when r.Profile.FocusPlayerId is { } focusId && meta.PlayerById(focusId) is { } focus:
            {
                var involved = m.PlayerId == focusId || m.SecondaryPlayerId == focusId;
                var line = f.Has("focus_touches")
                    ? $"{focus.ShortName} so far: {f.Num("focus_touches")} touches, {f.Num("focus_passes_completed")} completed passes, {f.Num("focus_distance_km")} km covered."
                    : "";
                var sentences = involved
                    ? new[] { firstWhy, line }
                    : new[] { $"Meanwhile: {brief.Headline}.", line };
                return new NarrativeDraft(involved ? $"{focus.ShortName}: {brief.Headline}" : brief.Headline,
                    sentences.Where(s => s.Length > 0).ToArray(), "template-narrator/player-focus");
            }

            case Persona.AudioDescription:
            {
                var where = r.Context.Evidence.LastOrDefault(e => e.Type != MatchEventType.Pressure) is { } ev && m.Team is { } t
                    ? $"The action was {PitchWords.Where(ev.End ?? ev.Start, t)}, {meta.TeamOf(t).Name} attacking."
                    : "";
                var score = f.Has("live_score") ? $"The score is {meta.Home.Name} {f["live_score"]} {meta.Away.Name}, minute {f.Num("live_minute")}." : "";
                return new NarrativeDraft(brief.Headline, new[] { where, firstWhy, score }.Where(s => s.Length > 0).ToArray(),
                    "template-narrator/audio-description");
            }

            default:
            {
                var opener = CasualOpener(m, r.Profile, f, meta);
                return new NarrativeDraft(m.Kind == MomentKind.Goal ? $"GOAL! {f["scorer"]}" : brief.Headline,
                    new[] { opener, firstWhy }.Where(s => s.Length > 0).ToArray(), "template-narrator/casual");
            }
        }
    }

    private static string CasualOpener(Moment m, ViewerProfile profile, FactReader f, MatchMetadata meta)
    {
        if (m.Team is not { } team) return "";
        var name = meta.TeamOf(team).Name;
        var fav = profile.FavouriteTeam;
        return m.Kind switch
        {
            MomentKind.Goal when fav == team => $"Get in! {name} score and it's {f["score"]}.",
            MomentKind.Goal when fav is not null => $"Bad news: {name} score and it's {f["score"]}.",
            MomentKind.Goal => $"{name} score! It's {f["score"]}.",
            MomentKind.BigChance when fav == team => $"So close for {name}!",
            MomentKind.BigChance => $"What a chance for {name}!",
            MomentKind.MomentumSwing when fav == team => $"{name} are on top now.",
            MomentKind.MomentumSwing when fav is not null => $"Your team are under pressure.",
            MomentKind.MomentumSwing => $"The game has turned towards {name}.",
            _ => ""
        };
    }

    public static string FirstSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var idx = text.IndexOf(". ", StringComparison.Ordinal);
        return idx < 0 ? text : text[..(idx + 1)];
    }
}
