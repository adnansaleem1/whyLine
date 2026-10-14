using WhyLine.Core.Domain;

namespace WhyLine.Core.Engine;

/// <summary>
/// Deterministic stats engine. Ingests events and tracking frames, maintains match state, and detects candidate
/// moments with their evidence. No AI here: every number an agent may use comes from this class.
/// </summary>
public sealed class MatchAnalyzer
{
    public const double SprintKmh = 30;
    private const double MomentumDecay = 0.65;

    private readonly MatchMetadata _meta;
    private readonly List<MatchEvent> _events = [];
    private readonly List<Moment> _moments = [];
    private readonly Dictionary<int, PlayerAgg> _players;
    private readonly TeamAgg[] _teams = [new(), new()];
    private readonly List<double[]> _momentumBuckets = [];   // per minute: [home, away] raw threat
    private readonly List<double[]> _possessionBuckets = []; // per minute: seconds [home, away]
    private readonly List<double> _momentumSmoothed = [];   // per minute: share -1..1, Home positive
    private readonly double[] _threatEwma = new double[2];

    private int _momentCounter;
    private int _homeScore, _awayScore, _period = 1;
    private TeamSide? _possessionTeam;
    private double _possessionSince;
    private double _lastClock;
    private int _lastEvaluatedMinute;
    private TeamSide? _dominant;
    private double _lastSwingAt = -999, _lastDifficultPassAt = -999, _lastSprintBadgeAt = -999;
    private readonly double[] _lastPressSurgeAt = [-999, -999];
    private readonly double[] _lastControlAt = [-999, -999];
    private MatchEvent? _lastPossessionWon;
    private int _sequencePasses;
    private TrackingFrame? _prevFrame;

    public MatchAnalyzer(MatchMetadata meta)
    {
        _meta = meta;
        _players = meta.Players.ToDictionary(p => p.Id, p => new PlayerAgg(p));
    }

    public MatchMetadata Metadata => _meta;
    public IReadOnlyList<MatchEvent> Events => _events;
    public IReadOnlyList<Moment> Moments => _moments;
    public (int Home, int Away) Score => (_homeScore, _awayScore);

    // ───────────────────────────── events ─────────────────────────────

    public IReadOnlyList<Moment> Ingest(MatchEvent e)
    {
        var found = new List<Moment>();
        AdvanceClock(e.MatchSeconds);
        _events.Add(e);
        _period = e.Period;
        var team = e.Team is { } t ? _teams[(int)t] : null;
        var actor = e.PlayerId is { } pid ? _players[pid] : null;

        switch (e.Type)
        {
            case MatchEventType.KickOff:
                SetPossession(e.Team!.Value, e.MatchSeconds);
                _sequencePasses = 0;
                break;

            case MatchEventType.Pass:
                OnPass(e, team!, actor!, found);
                break;

            case MatchEventType.Shot:
                team!.Shots++;
                actor!.Shots++;
                actor.Touch(e.MatchSeconds);
                team.Xg += e.Xg ?? 0;
                if (e.Success == true || e.Note == "saved") team.ShotsOnTarget++;
                AddThreat(e.Team!.Value, e.MatchSeconds, e.Xg ?? 0);
                if (e.Success != true && (e.Xg ?? 0) >= 0.3) found.Add(BigChance(e));
                else if (e.Success != true && (e.Speed ?? 0) * 3.6 >= 100) found.Add(ShotSpeedMoment(e));
                break;

            case MatchEventType.Goal:
                if (e.Team == TeamSide.Home) _homeScore++; else _awayScore++;
                actor!.Goals++;
                found.Add(GoalMoment(e));
                break;

            case MatchEventType.Tackle:
                team!.Tackles++;
                actor!.Tackles++;
                actor.Touch(e.MatchSeconds);
                if (Pitch.Progress(e.Team!.Value, e.Start) > 0.6) AddThreat(e.Team.Value, e.MatchSeconds, 0.03);
                break;

            case MatchEventType.Interception:
                team!.Interceptions++;
                actor!.Interceptions++;
                actor.Touch(e.MatchSeconds);
                if (Pitch.Progress(e.Team!.Value, e.Start) > 0.6) AddThreat(e.Team.Value, e.MatchSeconds, 0.03);
                break;

            case MatchEventType.Pressure:
                team!.Pressures++;
                actor!.Pressures++;
                AddThreat(e.Team!.Value, e.MatchSeconds, 0.002);
                break;

            case MatchEventType.PossessionChange:
                SetPossession(e.Team!.Value, e.MatchSeconds);
                _lastPossessionWon = e;
                _sequencePasses = 0;
                break;

            case MatchEventType.TacticalChange:
                found.Add(TacticalShiftMoment(e));
                break;

            case MatchEventType.HalfTime:
            case MatchEventType.FullTime:
                found.Add(PeriodEndMoment(e));
                break;
        }

        _moments.AddRange(found);
        return found;
    }

    private void OnPass(MatchEvent e, TeamAgg team, PlayerAgg passer, List<Moment> found)
    {
        team.Passes++;
        passer.Passes++;
        passer.Touch(e.MatchSeconds);
        var difficulty = PassQuality.Difficulty(e);

        if (e.Success == true)
        {
            team.PassesCompleted++;
            passer.PassesCompleted++;
            passer.DifficultySum += difficulty;
            passer.Streak++;
            passer.BestStreak = Math.Max(passer.BestStreak, passer.Streak);
            _sequencePasses++;
            if (e.TargetPlayerId is { } rid) _players[rid].Touch(e.MatchSeconds);

            var gain = e.End is { } end
                ? Math.Max(0, PassQuality.ThreatAt(e.Team!.Value, end) - PassQuality.ThreatAt(e.Team.Value, e.Start))
                : 0;
            AddThreat(e.Team!.Value, e.MatchSeconds, gain + 0.002);

            if (difficulty >= 0.72 && e.MatchSeconds - _lastDifficultPassAt > 180)
            {
                _lastDifficultPassAt = e.MatchSeconds;
                found.Add(DifficultPassMoment(e, difficulty));
            }

            if (passer.Streak is 15 or 25 or 35) found.Add(PassStreakMoment(e, passer));
        }
        else
        {
            passer.Streak = 0;
        }
    }

    // ───────────────────────────── time-based detection ─────────────────────────────

    /// <summary>Call regularly with the match clock; evaluates minute-level patterns.</summary>
    public IReadOnlyList<Moment> Tick(double matchSeconds)
    {
        AdvanceClock(matchSeconds);
        var found = new List<Moment>();
        var minute = (int)(matchSeconds / 60);
        while (_lastEvaluatedMinute < minute)
        {
            _lastEvaluatedMinute++;
            EvaluateMinute(_lastEvaluatedMinute, found);
        }

        _moments.AddRange(found);
        return found;
    }

    private void EvaluateMinute(int completedMinutes, List<Moment> found)
    {
        var t = completedMinutes * 60.0;
        EnsureBucket(completedMinutes - 1);
        var raw = _momentumBuckets[completedMinutes - 1];
        _threatEwma[0] = _threatEwma[0] * MomentumDecay + raw[0];
        _threatEwma[1] = _threatEwma[1] * MomentumDecay + raw[1];
        var total = _threatEwma[0] + _threatEwma[1];
        var normalized = total <= 1e-9 ? 0 : (_threatEwma[0] - _threatEwma[1]) / total;
        while (_momentumSmoothed.Count < completedMinutes) _momentumSmoothed.Add(0);
        _momentumSmoothed[completedMinutes - 1] = normalized;

        TeamSide? dominant = normalized > 0.25 ? TeamSide.Home : normalized < -0.25 ? TeamSide.Away : _dominant;
        if (_dominant is { } before && dominant is { } after && before != after && t - _lastSwingAt > 300 && completedMinutes > 5)
        {
            _lastSwingAt = t;
            found.Add(MomentumSwingMoment(after, t));
        }

        _dominant = dominant;

        foreach (var side in new[] { TeamSide.Home, TeamSide.Away })
        {
            var recent = PressuresPerMinute(side, t - 300, t);
            var earlier = PressuresPerMinute(side, t - 900, t - 300);
            if (completedMinutes >= 15 && recent >= 1.5 && recent >= earlier * 1.6 && t - _lastPressSurgeAt[(int)side] > 600)
            {
                _lastPressSurgeAt[(int)side] = t;
                found.Add(PressSurgeMoment(side, t, recent, earlier));
            }

            var poss = PossessionPct(side, t - 300, t);
            var (completion, _) = PassCompletion(side, t - 300, t);
            if (completedMinutes >= 8 && poss >= 64 && completion >= 85 && t - _lastControlAt[(int)side] > 720)
            {
                _lastControlAt[(int)side] = t;
                found.Add(ControlSpellMoment(side, t, poss, completion));
            }
        }

        if (completedMinutes >= 20)
        {
            foreach (var p in _players.Values.Where(p => p.Player.Position is "ST" or "LW" or "RW"))
            {
                var since = t - (p.LastTouch ?? 0);
                if (since >= 15 * 60 && !p.QuietFlagged)
                {
                    p.QuietFlagged = true;
                    found.Add(QuietPlayerMoment(p, t, since));
                }
            }
        }
    }

    // ───────────────────────────── tracking ─────────────────────────────

    /// <summary>Derives speed and distance from positions alone and returns threshold crossings.</summary>
    public IReadOnlyList<ThresholdCrossing> IngestFrame(TrackingFrame frame)
    {
        var crossings = new List<ThresholdCrossing>();
        if (_prevFrame is { } prev && frame.MatchSeconds > prev.MatchSeconds)
        {
            var dt = frame.MatchSeconds - prev.MatchSeconds;
            var ids = _meta.Players.OrderBy(p => p.Id).Select(p => p.Id).ToArray();
            for (var i = 0; i < frame.Players.Length && i < ids.Length; i++)
            {
                var step = frame.Players[i].DistanceTo(prev.Players[i]);
                if (step > 2.5) continue; // repositioning at kick-off, not running
                var agg = _players[ids[i]];
                var before = agg.DistanceM;
                agg.DistanceM += step;
                var kmh = step / dt * 3.6;
                agg.SmoothedKmh = agg.SmoothedKmh * 0.6 + kmh * 0.4;
                agg.TopSpeedKmh = Math.Max(agg.TopSpeedKmh, agg.SmoothedKmh);

                if (agg.SmoothedKmh >= SprintKmh && frame.MatchSeconds - agg.LastSprintBadge > 240 &&
                    frame.MatchSeconds - _lastSprintBadgeAt > 25)
                {
                    agg.LastSprintBadge = frame.MatchSeconds;
                    _lastSprintBadgeAt = frame.MatchSeconds;
                    crossings.Add(new ThresholdCrossing(CueType.SpeedBadge, agg.Player.Id, Math.Round(agg.SmoothedKmh, 1),
                        frame.MatchSeconds, frame.Players[i]));
                }

                var milestone = Math.Floor(agg.DistanceM / 2000);
                if (milestone > Math.Floor(before / 2000) && milestone > 0)
                    crossings.Add(new ThresholdCrossing(CueType.DistanceBadge, agg.Player.Id, milestone * 2, frame.MatchSeconds,
                        frame.Players[i]));
            }
        }

        _prevFrame = frame;
        return crossings;
    }

    // ───────────────────────────── snapshot ─────────────────────────────

    public MatchSnapshot Snapshot(double matchSeconds)
    {
        AdvanceClock(matchSeconds);
        return new MatchSnapshot(
            _meta.MatchId, matchSeconds, _period, _homeScore, _awayScore,
            TeamSnap(TeamSide.Home, matchSeconds), TeamSnap(TeamSide.Away, matchSeconds),
            _players.Values.OrderBy(p => p.Player.Id).Select(p => p.Snapshot()).ToArray(),
            MomentumSeries(),
            ControlIndex(matchSeconds - 300, matchSeconds, out var label), label,
            Math.Round(PassesIn(null, matchSeconds - 300, matchSeconds) / 5.0, 1));
    }

    public IReadOnlyList<MomentumPoint> MomentumSeries() =>
        _momentumSmoothed.Select((v, i) => new MomentumPoint(i + 1, Math.Round(v, 3))).ToArray();

    public double ControlIndex(double from, double to, out string label)
    {
        var turnovers = _events.Count(e => e.Type == MatchEventType.PossessionChange && e.MatchSeconds > from && e.MatchSeconds <= to);
        var minutes = Math.Max(1, (Math.Min(to, _lastClock) - Math.Max(0, from)) / 60);
        var passes = _events.Where(e => e.Type == MatchEventType.Pass && e.MatchSeconds > from && e.MatchSeconds <= to).ToArray();
        var completion = passes.Length == 0 ? 0.75 : passes.Count(p => p.Success == true) / (double)passes.Length;

        var chaos = Math.Clamp(turnovers / minutes / 12.0, 0, 1);
        var control = Math.Clamp((completion - 0.6) / 0.35, 0, 1);
        var index = Math.Round(Math.Clamp(0.5 + (control - chaos) / 2, 0, 1), 2);
        label = index >= 0.6 ? "Control" : index <= 0.4 ? "Chaos" : "Balanced";
        return index;
    }

    // ───────────────────────────── moment builders ─────────────────────────────

    private Moment GoalMoment(MatchEvent goal)
    {
        var side = goal.Team!.Value;
        var scorer = _meta.PlayerById(goal.PlayerId)!;
        var shot = _events.LastOrDefault(e => e.Type == MatchEventType.Shot && e.PlayerId == goal.PlayerId);
        var assist = _events.LastOrDefault(e => e.Type == MatchEventType.Pass && e.Success == true && e.TargetPlayerId == goal.PlayerId &&
                                                e.MatchSeconds > goal.MatchSeconds - 15);
        var won = _lastPossessionWon is { } w && w.Team == side && goal.MatchSeconds - w.MatchSeconds < 25 ? w : null;
        var leader = _homeScore == _awayScore ? "level" : (_homeScore > _awayScore ? _meta.Home.Name : _meta.Away.Name);

        var facts = new List<Fact>
        {
            new("scorer", "Scorer", Text: scorer.Name),
            new("team", "Team", Text: _meta.TeamOf(side).Name),
            new("minute", "Minute", goal.Minute),
            new("score", "Score", Text: $"{_homeScore}-{_awayScore}"),
            new("home_score", "Home goals", _homeScore),
            new("away_score", "Away goals", _awayScore),
            new("state", "Match state", Text: leader == "level" ? "level" : $"{leader} lead"),
            new("xg", "Chance quality (xG)", Math.Round(goal.Xg ?? 0, 2), Unit: "xG")
        };
        if (_sequencePasses > 0) facts.Add(new("build_up_passes", "Passes in the move", _sequencePasses));
        if (shot?.Speed is { } s) facts.Add(new("shot_speed", "Shot speed", Math.Round(s * 3.6), Unit: "km/h"));
        if (shot?.Distance is { } d) facts.Add(new("shot_distance", "Shot distance", Math.Round(d), Unit: "m"));
        if (assist is not null) facts.Add(new("assist", "Assist", Text: _meta.PlayerById(assist.PlayerId)!.Name));
        if (won is not null)
        {
            facts.Add(new("won_by", "Possession won by", Text: _meta.PlayerById(won.PlayerId)?.Name ?? "a teammate"));
            facts.Add(new("won_how", "How possession was won", Text: won.Note ?? "regain"));
            if (won.PlayerId == goal.PlayerId) facts.Add(new("won_by_scorer", "Scorer won the ball himself", Text: "yes"));
            facts.Add(new("won_seconds_before", "Seconds from regain to goal", Math.Round(goal.MatchSeconds - won.MatchSeconds), Unit: "s"));
            facts.Add(new("won_third", "Regain zone", Text: Pitch.Third(side, won.Start) == 3 ? "final third" : Pitch.Third(side, won.Start) == 2 ? "middle third" : "own third"));
        }

        var late = goal.MatchSeconds > 80 * 60 ? 0.1 : 0;
        return NewMoment(MomentKind.Goal, goal.MatchSeconds, side, goal.PlayerId, assist?.PlayerId, Math.Min(1, 0.9 + late),
            Evidence(goal, shot, assist, won), facts);
    }

    private Moment BigChance(MatchEvent shot)
    {
        var p = _meta.PlayerById(shot.PlayerId)!;
        return NewMoment(MomentKind.BigChance, shot.MatchSeconds, shot.Team, shot.PlayerId, null, Math.Min(0.85, 0.5 + (shot.Xg ?? 0)),
            Evidence(shot),
            [
                new("player", "Player", Text: p.Name),
                new("team", "Team", Text: _meta.TeamOf(shot.Team!.Value).Name),
                new("minute", "Minute", shot.Minute),
                new("xg", "Chance quality (xG)", Math.Round(shot.Xg ?? 0, 2), Unit: "xG"),
                new("outcome", "Outcome", Text: shot.Note ?? "missed"),
                new("shot_distance", "Shot distance", Math.Round(shot.Distance ?? 0), Unit: "m"),
                new("score", "Score", Text: $"{_homeScore}-{_awayScore}")
            ]);
    }

    private Moment ShotSpeedMoment(MatchEvent shot)
    {
        var p = _meta.PlayerById(shot.PlayerId)!;
        return NewMoment(MomentKind.ShotSpeed, shot.MatchSeconds, shot.Team, shot.PlayerId, null, 0.25, Evidence(shot),
        [
            new("player", "Player", Text: p.Name),
            new("minute", "Minute", shot.Minute),
            new("shot_speed", "Shot speed", Math.Round((shot.Speed ?? 0) * 3.6), Unit: "km/h"),
            new("outcome", "Outcome", Text: shot.Note ?? "missed")
        ]);
    }

    private Moment DifficultPassMoment(MatchEvent pass, double difficulty)
    {
        var p = _meta.PlayerById(pass.PlayerId)!;
        var r = _meta.PlayerById(pass.TargetPlayerId);
        return NewMoment(MomentKind.DifficultPass, pass.MatchSeconds, pass.Team, pass.PlayerId, pass.TargetPlayerId, 0.3 + difficulty * 0.2,
            Evidence(pass),
            [
                new("player", "Passer", Text: p.Name),
                new("receiver", "Receiver", Text: r?.Name ?? "a teammate"),
                new("minute", "Minute", pass.Minute),
                new("difficulty", "Pass difficulty (out of a hundred)", Math.Round(difficulty * 100), Unit: "/100"),
                new("difficulty_label", "Difficulty band", Text: PassQuality.Label(difficulty)),
                new("distance", "Pass distance", Math.Round(pass.Distance ?? 0), Unit: "m"),
                new("bypassed", "Opponents bypassed", pass.OpponentsBypassed ?? 0),
                new("pressure", "Nearest opponent at release", Math.Round(pass.PressureMeters ?? 0, 1), Unit: "m"),
                new("ball_speed", "Ball speed", Math.Round((pass.Speed ?? 0) * 3.6), Unit: "km/h")
            ]);
    }

    private Moment PassStreakMoment(MatchEvent pass, PlayerAgg p)
    {
        var recent = _events.Where(e => e.Type == MatchEventType.Pass && e.PlayerId == p.Player.Id && e.Success == true)
            .TakeLast(p.Streak).ToArray();
        var avgDiff = recent.Length == 0 ? 0 : recent.Average(PassQuality.Difficulty);
        var underPressure = recent.Count(e => (e.PressureMeters ?? 10) < 4);
        return NewMoment(MomentKind.PassStreak, pass.MatchSeconds, pass.Team, p.Player.Id, null, Math.Min(0.5, 0.2 + 0.025 * underPressure),
            recent.Select(e => e.Id).ToArray(),
            [
                new("player", "Player", Text: p.Player.Name),
                new("minute", "Minute", pass.Minute),
                new("streak", "Consecutive completed passes", p.Streak),
                new("under_pressure", "Of those, under pressure (opponent within four metres)", underPressure),
                new("avg_difficulty", "Average difficulty (out of a hundred)", Math.Round(avgDiff * 100), Unit: "/100")
            ]);
    }

    private Moment TacticalShiftMoment(MatchEvent e)
    {
        var side = e.Team!.Value;
        return NewMoment(MomentKind.TacticalShift, e.MatchSeconds, side, null, null, 0.55, [e.Id],
        [
            new("team", "Team", Text: _meta.TeamOf(side).Name),
            new("minute", "Minute", e.Minute),
            new("change", "Change", Text: e.Note ?? "tactical change"),
            new("pressures_before", "Pressures per minute (previous ten minutes)", Math.Round(PressuresPerMinute(side, e.MatchSeconds - 600, e.MatchSeconds), 1), Unit: "/min"),
            new("possession_before", "Possession (previous ten minutes)", Math.Round(PossessionPct(side, e.MatchSeconds - 600, e.MatchSeconds)), Unit: "%")
        ]);
    }

    private Moment MomentumSwingMoment(TeamSide now, double t)
    {
        var before = (from: t - 780, to: t - 300);
        var since = (from: t - 300, to: t);
        var turnovers = _events.Where(e => e.Type is MatchEventType.Tackle or MatchEventType.Interception && e.Team == now &&
                                           e.MatchSeconds > since.from && e.MatchSeconds <= since.to &&
                                           Pitch.Progress(now, e.Start) > 0.55).ToArray();
        var trigger = _events.LastOrDefault(e => e.Type == MatchEventType.TacticalChange && e.MatchSeconds > t - 900 && e.MatchSeconds <= t);

        var facts = new List<Fact>
        {
            new("team", "Team now on top", Text: _meta.TeamOf(now).Name),
            new("opponent", "Team pushed back", Text: _meta.TeamOf(now.Opponent()).Name),
            new("minute", "Minute", (int)(t / 60)),
            new("possession_before", "Possession before", Math.Round(PossessionPct(now, before.from, before.to)), Unit: "%"),
            new("possession_now", "Possession (last five minutes)", Math.Round(PossessionPct(now, since.from, since.to)), Unit: "%"),
            new("pressures_before", "Pressures per minute before", Math.Round(PressuresPerMinute(now, before.from, before.to), 1), Unit: "/min"),
            new("pressures_now", "Pressures per minute (last five minutes)", Math.Round(PressuresPerMinute(now, since.from, since.to), 1), Unit: "/min"),
            new("high_regains", "Ball regains in advanced areas (last five minutes)", turnovers.Length),
            new("final_third_passes", "Final-third passes (last five minutes)", FinalThirdPasses(now, since.from, since.to)),
            new("score", "Score", Text: $"{_homeScore}-{_awayScore}")
        };
        if (trigger is not null && trigger.Team == now) facts.Add(new("trigger", "Trigger", Text: trigger.Note ?? "tactical change"));

        var evidence = turnovers.Select(e => e.Id).ToList();
        if (trigger is not null) evidence.Insert(0, trigger.Id);
        return NewMoment(MomentKind.MomentumSwing, t, now, null, null, 0.7, evidence.Take(12).ToArray(), facts);
    }

    private Moment PressSurgeMoment(TeamSide side, double t, double recent, double earlier)
    {
        var evidence = _events.Where(e => e.Type == MatchEventType.Pressure && e.Team == side && e.MatchSeconds > t - 300)
            .Select(e => e.Id).TakeLast(12).ToArray();
        return NewMoment(MomentKind.PressSurge, t, side, null, null, 0.5, evidence,
        [
            new("team", "Team", Text: _meta.TeamOf(side).Name),
            new("minute", "Minute", (int)(t / 60)),
            new("pressures_now", "Pressures per minute (last five minutes)", Math.Round(recent, 1), Unit: "/min"),
            new("pressures_before", "Pressures per minute (previous ten minutes)", Math.Round(earlier, 1), Unit: "/min"),
            new("opponent_completion", "Opponent pass completion (last five minutes)", Math.Round(PassCompletion(side.Opponent(), t - 300, t).Pct), Unit: "%")
        ]);
    }

    private Moment ControlSpellMoment(TeamSide side, double t, double poss, double completion)
    {
        var evidence = _events.Where(e => e.Type == MatchEventType.Pass && e.Team == side && e.Success == true && e.MatchSeconds > t - 300)
            .Select(e => e.Id).TakeLast(12).ToArray();
        var index = ControlIndex(t - 300, t, out var label);
        return NewMoment(MomentKind.ControlSpell, t, side, null, null, 0.4, evidence,
        [
            new("team", "Team", Text: _meta.TeamOf(side).Name),
            new("minute", "Minute", (int)(t / 60)),
            new("possession", "Possession (last five minutes)", Math.Round(poss), Unit: "%"),
            new("completion", "Pass completion (last five minutes)", Math.Round(completion), Unit: "%"),
            new("passes", "Passes (last five minutes)", PassesIn(side, t - 300, t)),
            new("control_index", "Control index (out of a hundred)", Math.Round(index * 100), Unit: "/100"),
            new("control_label", "Game state", Text: label)
        ]);
    }

    private Moment QuietPlayerMoment(PlayerAgg p, double t, double since)
    {
        return NewMoment(MomentKind.QuietPlayer, t, p.Player.Team, p.Player.Id, null, 0.25, [],
        [
            new("player", "Player", Text: p.Player.Name),
            new("minute", "Minute", (int)(t / 60)),
            new("minutes_without_touch", "Minutes without a touch", Math.Floor(since / 60), Unit: "min"),
            new("touches", "Touches so far", p.Touches),
            new("team_possession", "Team possession (last fifteen minutes)", Math.Round(PossessionPct(p.Player.Team, t - 900, t)), Unit: "%")
        ]);
    }

    private Moment PeriodEndMoment(MatchEvent e)
    {
        var snap = Snapshot(e.MatchSeconds);
        var kind = e.Type == MatchEventType.HalfTime ? MomentKind.HalfTime : MomentKind.FullTime;
        var evidence = _moments.Where(m => m.Kind is MomentKind.Goal or MomentKind.MomentumSwing or MomentKind.BigChance)
            .SelectMany(m => m.EvidenceEventIds.Take(2)).Take(12).ToArray();
        return NewMoment(kind, e.MatchSeconds, null, null, null, 0.8, evidence,
        [
            new("score", "Score", Text: $"{_homeScore}-{_awayScore}"),
            new("home_score", "Home goals", _homeScore),
            new("away_score", "Away goals", _awayScore),
            new("home", "Home team", Text: _meta.Home.Name),
            new("away", "Away team", Text: _meta.Away.Name),
            new("home_possession", "Home possession", Math.Round(snap.Home.PossessionPct), Unit: "%"),
            new("away_possession", "Away possession", Math.Round(snap.Away.PossessionPct), Unit: "%"),
            new("home_xg", "Home xG", Math.Round(snap.Home.Xg, 2), Unit: "xG"),
            new("away_xg", "Away xG", Math.Round(snap.Away.Xg, 2), Unit: "xG"),
            new("home_shots", "Home shots", snap.Home.Shots),
            new("away_shots", "Away shots", snap.Away.Shots),
            new("home_pass_acc", "Home pass accuracy", Math.Round(snap.Home.PassAccuracyPct), Unit: "%"),
            new("away_pass_acc", "Away pass accuracy", Math.Round(snap.Away.PassAccuracyPct), Unit: "%")
        ]);
    }

    private Moment NewMoment(MomentKind kind, double t, TeamSide? team, int? player, int? secondary, double importance,
        IReadOnlyList<string> evidence, IReadOnlyList<Fact> facts) => new()
    {
        Id = $"m{++_momentCounter:000}",
        Kind = kind,
        MatchSeconds = t,
        Team = team,
        PlayerId = player,
        SecondaryPlayerId = secondary,
        Importance = Math.Round(importance, 2),
        EvidenceEventIds = evidence,
        Facts = facts
    };

    private static string[] Evidence(params MatchEvent?[] events) =>
        events.Where(e => e is not null).Select(e => e!.Id).Distinct().ToArray();

    // ───────────────────────────── aggregates ─────────────────────────────

    private void AdvanceClock(double t)
    {
        if (t < _lastClock) return;
        if (_possessionTeam is { } team) AccumulatePossession(team, _lastClock, t);
        _lastClock = t;
    }

    private void SetPossession(TeamSide team, double t)
    {
        _possessionTeam = team;
        _possessionSince = t;
    }

    private void AccumulatePossession(TeamSide team, double from, double to)
    {
        var cursor = from;
        while (cursor < to)
        {
            var minute = (int)(cursor / 60);
            var end = Math.Min(to, (minute + 1) * 60.0);
            EnsureBucket(minute);
            _possessionBuckets[minute][(int)team] += end - cursor;
            cursor = end;
        }
    }

    private void AddThreat(TeamSide team, double t, double value)
    {
        var minute = (int)(t / 60);
        EnsureBucket(minute);
        _momentumBuckets[minute][(int)team] += value;
    }

    private void EnsureBucket(int minute)
    {
        while (_momentumBuckets.Count <= minute) _momentumBuckets.Add(new double[2]);
        while (_possessionBuckets.Count <= minute) _possessionBuckets.Add(new double[2]);
    }

    public double PossessionPct(TeamSide side, double from, double to)
    {
        double mine = 0, total = 0;
        var startMin = Math.Max(0, (int)(from / 60));
        var endMin = Math.Min(_possessionBuckets.Count - 1, (int)Math.Ceiling(to / 60) - 1);
        for (var m = startMin; m <= endMin; m++)
        {
            mine += _possessionBuckets[m][(int)side];
            total += _possessionBuckets[m][0] + _possessionBuckets[m][1];
        }

        return total <= 0 ? 50 : mine / total * 100;
    }

    public double PressuresPerMinute(TeamSide side, double from, double to)
    {
        var start = Math.Max(0, from);
        var minutes = Math.Max(1, (to - start) / 60);
        return _events.Count(e => e.Type == MatchEventType.Pressure && e.Team == side && e.MatchSeconds > start && e.MatchSeconds <= to) / minutes;
    }

    public (double Pct, int Count) PassCompletion(TeamSide side, double from, double to)
    {
        var passes = _events.Where(e => e.Type == MatchEventType.Pass && e.Team == side && e.MatchSeconds > from && e.MatchSeconds <= to).ToArray();
        return passes.Length == 0 ? (0, 0) : (passes.Count(p => p.Success == true) * 100.0 / passes.Length, passes.Length);
    }

    private int PassesIn(TeamSide? side, double from, double to) =>
        _events.Count(e => e.Type == MatchEventType.Pass && (side is null || e.Team == side) && e.MatchSeconds > from && e.MatchSeconds <= to);

    private int FinalThirdPasses(TeamSide side, double from, double to) =>
        _events.Count(e => e.Type == MatchEventType.Pass && e.Team == side && e.Success == true && e.End is { } end &&
                           Pitch.Third(side, end) == 3 && e.MatchSeconds > from && e.MatchSeconds <= to);

    private TeamSnapshot TeamSnap(TeamSide side, double t)
    {
        var a = _teams[(int)side];
        var team = _meta.TeamOf(side);
        return new TeamSnapshot(side, team.Name, team.ShortName, team.PrimaryColor, team.Profile.Name,
            Math.Round(PossessionPct(side, 0, t), 1), a.Passes, a.PassesCompleted,
            a.Passes == 0 ? 0 : Math.Round(a.PassesCompleted * 100.0 / a.Passes, 1),
            a.Shots, a.ShotsOnTarget, Math.Round(a.Xg, 2), a.Tackles, a.Interceptions, a.Pressures,
            Math.Round(PressuresPerMinute(side, t - 300, t), 1));
    }

    private sealed class TeamAgg
    {
        public int Passes, PassesCompleted, Shots, ShotsOnTarget, Tackles, Interceptions, Pressures;
        public double Xg;
    }

    private sealed class PlayerAgg(Player player)
    {
        public Player Player { get; } = player;
        public int Touches, Passes, PassesCompleted, Shots, Goals, Tackles, Interceptions, Pressures, Streak, BestStreak;
        public double DifficultySum, DistanceM, TopSpeedKmh, SmoothedKmh;
        public double LastSprintBadge = -999;
        public double? LastTouch;
        public bool QuietFlagged;

        public void Touch(double t)
        {
            Touches++;
            LastTouch = t;
            QuietFlagged = false;
        }

        public PlayerSnapshot Snapshot() => new(Player.Id, Player.Name, Player.Number, Player.Team, Player.Position, Touches, Passes,
            PassesCompleted, PassesCompleted == 0 ? 0 : Math.Round(DifficultySum / PassesCompleted * 100), Shots, Goals, Tackles,
            Interceptions, Pressures, Math.Round(DistanceM / 1000, 2), Math.Round(TopSpeedKmh, 1), BestStreak, LastTouch);
    }
}
