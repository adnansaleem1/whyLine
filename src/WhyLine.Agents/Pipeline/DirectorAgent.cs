using WhyLine.Agents.Abstractions;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Pipeline;

public sealed record DirectorDecision(bool Approved, string Reason, IReadOnlyList<ViewerProfile> Audience, int Priority, double DurationSeconds);

/// <summary>
/// The producer. Decides what is worth screen time, for whom, and for how long. Deterministic on purpose: the
/// screen-time budget must hold under load, and judges can read the rules.
/// </summary>
public sealed class DirectorAgent(AgentOptions options)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _lastCardAt = new();
    private readonly List<Moment> _approved = [];

    public DirectorDecision Decide(Moment moment, IReadOnlyCollection<ViewerProfile> profiles, double liveClock)
    {
        lock (_gate)
        {
            if (profiles.Count == 0)
                return new DirectorDecision(false, "No viewers connected.", [], 0, 0);

            var lag = liveClock - moment.MatchSeconds;
            if (lag > 150 && moment.Importance < 0.8)
                return new DirectorDecision(false, $"Stale: {lag:0}s behind live and not important enough.", [], 0, 0);

            var duplicate = _approved.LastOrDefault(m =>
                m.Team == moment.Team && moment.MatchSeconds - m.MatchSeconds < 240 &&
                ((m.Kind == MomentKind.MomentumSwing && moment.Kind == MomentKind.PressSurge) ||
                 (m.Kind == moment.Kind && moment.Kind is MomentKind.ControlSpell or MomentKind.PressSurge)));
            if (duplicate is not null)
                return new DirectorDecision(false, $"Already told this story ({duplicate.Kind} at {duplicate.Minute}').", [], 0, 0);

            var audience = new List<ViewerProfile>();
            foreach (var profile in profiles)
            {
                var score = moment.Importance + Relevance(moment, profile);
                var threshold = profile.Persona switch
                {
                    Persona.Analyst => 0.3,
                    Persona.Casual => 0.5,
                    Persona.PlayerFocus => 0.75,
                    Persona.AudioDescription => 0.45,
                    _ => 0.5
                };

                if (moment.Kind is MomentKind.HalfTime or MomentKind.FullTime) score = 1;
                if (score < threshold) continue;

                // Screen-time budget per profile; big moments pre-empt.
                var last = _lastCardAt.GetValueOrDefault(profile.Key, -999);
                if (moment.MatchSeconds - last < options.ScreenBudgetSeconds && moment.Importance < 0.85) continue;

                audience.Add(profile);
            }

            if (audience.Count == 0)
                return new DirectorDecision(false, $"Importance {moment.Importance:0.00} below every viewer's threshold or budget.", [], 0, 0);

            foreach (var p in audience) _lastCardAt[p.Key] = moment.MatchSeconds;
            _approved.Add(moment);
            var priority = moment.Importance >= 0.85 ? 3 : moment.Importance >= 0.5 ? 2 : 1;
            var duration = moment.Kind is MomentKind.HalfTime or MomentKind.FullTime ? 30 : 10 + priority * 4;
            return new DirectorDecision(true,
                $"Approved for {audience.Count} of {profiles.Count} viewer profile(s), priority {priority}.", audience, priority, duration);
        }
    }

    /// <summary>How much more this moment matters to a particular viewer.</summary>
    public static double Relevance(Moment moment, ViewerProfile profile)
    {
        var bonus = 0.0;
        if (profile.FavouriteTeam is { } fav && moment.Team == fav) bonus += 0.1;
        if (profile.FocusPlayerId is { } focus && (moment.PlayerId == focus || moment.SecondaryPlayerId == focus)) bonus += 0.6;
        if (profile.Persona == Persona.Analyst && moment.Kind is MomentKind.MomentumSwing or MomentKind.PressSurge
                or MomentKind.ControlSpell or MomentKind.DifficultPass or MomentKind.TacticalShift) bonus += 0.15;
        if (profile.Persona == Persona.Casual && moment.Kind is MomentKind.DifficultPass or MomentKind.PassStreak or MomentKind.ControlSpell)
            bonus -= 0.1;
        return bonus;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _lastCardAt.Clear();
            _approved.Clear();
        }
    }
}
