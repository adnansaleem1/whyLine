using WhyLine.Core.Domain;

namespace WhyLine.Core.Simulation;

/// <summary>Output of one simulation tick: the tracking frame plus any events that completed during the tick.</summary>
public sealed record SimStep(TrackingFrame Frame, IReadOnlyList<MatchEvent> Events);

/// <summary>
/// Deterministic, agent-based synthetic match. Produces 10 Hz tracking frames (positions only) and an event feed.
/// Behaviour is driven by each team's <see cref="TacticalProfile"/> and by scripted <see cref="StorylineBeat"/>s.
/// </summary>
public sealed class MatchSimulator
{
    public const double Dt = 0.1;
    public const double HalfLength = 45 * 60;
    public const double FullLength = 90 * 60;

    private const double SprintThreshold = 7.6;

    private readonly MatchScript _script;
    private readonly Random _rng;
    private readonly PlayerState[] _players;
    private readonly TacticalProfile[] _profiles = new TacticalProfile[2];
    private readonly HashSet<int> _pressured = [];
    private readonly List<MatchEvent> _emitted = [];

    private int _eventCounter;
    private int _nextBeat;
    private bool _started;

    private Vec2 _ball = Pitch.Centre;
    private int? _owner;
    private double _ownerSince;
    private double _nextDecisionAt;
    private BallFlight? _flight;
    private double? _restartAt;
    private TeamSide _restartTeam;
    private ActiveChance? _chance;

    public MatchSimulator(MatchScript script)
    {
        _script = script;
        _rng = new Random(script.Metadata.Seed);
        _profiles[0] = script.Metadata.Home.Profile;
        _profiles[1] = script.Metadata.Away.Profile;
        _players = script.Metadata.Players
            .OrderBy(p => p.Id)
            .Select((p, i) => new PlayerState(p, i, SquadFactory.Formation433[i % 11].Prog, SquadFactory.Formation433[i % 11].Width))
            .ToArray();
        foreach (var ps in _players) ps.Pos = SlotPosition(ps, kickOff: true);
    }

    public MatchMetadata Metadata => _script.Metadata;
    public double Clock { get; private set; }
    public int Period { get; private set; } = 1;
    public int HomeScore { get; private set; }
    public int AwayScore { get; private set; }
    public bool Finished { get; private set; }
    public TeamSide Possession { get; private set; } = TeamSide.Home;

    public TacticalProfile ProfileOf(TeamSide side) => _profiles[(int)side];

    /// <summary>Distance covered so far by each player, in metres (indexed by player index).</summary>
    public IReadOnlyList<double> DistanceCovered => _players.Select(p => p.Distance).ToArray();

    public SimStep Step()
    {
        _emitted.Clear();
        if (Finished) return new SimStep(Frame(), []);

        if (!_started)
        {
            _started = true;
            KickOff(TeamSide.Home);
        }

        ProcessBeats();
        UpdateRestart();
        UpdateFlight();
        MakeDecision();
        MovePlayers();
        ResolveContact();

        Clock = Math.Round(Clock + Dt, 3);

        if (Period == 1 && Clock >= HalfLength)
        {
            Emit(new MatchEvent { Id = NextId(), Type = MatchEventType.HalfTime, MatchSeconds = Clock, Period = 1, Start = Pitch.Centre });
            Period = 2;
            KickOff(TeamSide.Away);
        }
        else if (Clock >= FullLength)
        {
            Emit(new MatchEvent { Id = NextId(), Type = MatchEventType.FullTime, MatchSeconds = Clock, Period = 2, Start = Pitch.Centre });
            Finished = true;
        }

        return new SimStep(Frame(), _emitted.ToArray());
    }

    // ───────────────────────────── storyline ─────────────────────────────

    private void ProcessBeats()
    {
        while (_nextBeat < _script.Beats.Count && Clock >= _script.Beats[_nextBeat].AtMinute * 60)
        {
            var beat = _script.Beats[_nextBeat++];
            switch (beat.Kind)
            {
                case BeatKind.ProfileChange when beat.NewProfile is not null:
                    _profiles[(int)beat.Team] = beat.NewProfile;
                    Emit(new MatchEvent
                    {
                        Id = NextId(), Type = MatchEventType.TacticalChange, MatchSeconds = Clock, Period = Period,
                        Team = beat.Team, Start = Pitch.Centre, Note = beat.Note ?? $"Switch to {beat.NewProfile.Name}"
                    });
                    break;
                case BeatKind.Chance when beat.PlayerId is not null:
                    _chance = new ActiveChance(beat, beat.PlayerId.Value - 1, Clock);
                    break;
            }
        }
    }

    // ───────────────────────────── ball ─────────────────────────────

    private void UpdateRestart()
    {
        if (_restartAt is { } at && Clock >= at)
        {
            _restartAt = null;
            KickOff(_restartTeam);
        }
    }

    private void UpdateFlight()
    {
        if (_flight is null)
        {
            if (_owner is { } o)
            {
                var carrier = _players[o];
                var dir = (Pitch.GoalAttackedBy(carrier.Team) - carrier.Pos).Normalized();
                _ball = (carrier.Pos + dir * 0.5).Clamp();
            }
            return;
        }

        var f = _flight;
        var t = Math.Clamp((Clock - f.StartTime) / f.Duration, 0, 1);
        _ball = f.Start + (f.End - f.Start) * t;
        if (t < 1) return;

        _flight = null;
        if (f.Kind == FlightKind.Pass) ResolvePass(f);
        else ResolveShot(f);
    }

    private void ResolvePass(BallFlight f)
    {
        var passer = _players[f.FromIdx];
        Emit(new MatchEvent
        {
            Id = NextId(), Type = MatchEventType.Pass, MatchSeconds = Clock, Period = Period,
            Team = passer.Team, PlayerId = passer.Player.Id, TargetPlayerId = _players[f.IntendedIdx].Player.Id,
            Start = f.Start, End = f.End, Success = f.Success, Speed = f.Speed, Distance = f.Start.DistanceTo(f.End),
            PressureMeters = f.PressureMeters, ReceiverPressureMeters = f.ReceiverPressureMeters, OpponentsBypassed = f.Bypassed
        });

        var receiver = _players[f.ReceiverIdx];
        if (!f.Success)
        {
            Emit(new MatchEvent
            {
                Id = NextId(), Type = MatchEventType.Interception, MatchSeconds = Clock, Period = Period,
                Team = receiver.Team, PlayerId = receiver.Player.Id, Start = f.End, Success = true
            });
            ChangePossession(receiver, "interception");
        }
        else
        {
            GiveBall(receiver);
        }
    }

    private void ResolveShot(BallFlight f)
    {
        var shooter = _players[f.FromIdx];
        Emit(new MatchEvent
        {
            Id = NextId(), Type = MatchEventType.Shot, MatchSeconds = Clock, Period = Period,
            Team = shooter.Team, PlayerId = shooter.Player.Id, Start = f.Start, End = f.End,
            Success = f.Success, Speed = f.Speed, Distance = f.Start.DistanceTo(f.End),
            PressureMeters = f.PressureMeters, Xg = f.Xg, Note = f.Success ? "goal" : f.Note
        });

        if (f.Success)
        {
            if (shooter.Team == TeamSide.Home) HomeScore++; else AwayScore++;
            Emit(new MatchEvent
            {
                Id = NextId(), Type = MatchEventType.Goal, MatchSeconds = Clock, Period = Period,
                Team = shooter.Team, PlayerId = shooter.Player.Id, Start = f.Start, End = f.End, Xg = f.Xg,
                Note = $"{HomeScore}-{AwayScore}"
            });
            _owner = null;
            _restartAt = Clock + 5;
            _restartTeam = shooter.Team.Opponent();
            return;
        }

        var keeper = _players.First(p => p.Team == shooter.Team.Opponent() && p.Player.Position == "GK");
        ChangePossession(keeper, f.Note ?? "save");
    }

    // ───────────────────────────── decisions ─────────────────────────────

    private void MakeDecision()
    {
        if (_owner is not { } o || _flight is not null || _restartAt is not null || Clock < _nextDecisionAt) return;

        var carrier = _players[o];
        var team = carrier.Team;
        var goal = Pitch.GoalAttackedBy(team);
        var distGoal = carrier.Pos.DistanceTo(goal);

        if (_chance is { } chance && chance.Beat.Team == team)
        {
            if (o == chance.PlayerIdx)
            {
                if (distGoal < 21) Shoot(carrier, forced: chance);
                else _nextDecisionAt = Clock + 0.4;
            }
            else
            {
                Pass(carrier, forcedTarget: chance.PlayerIdx);
            }
            return;
        }

        if (_chance is { } c && c.Beat.Team != team)
        {
            // The scripted team wins the ball back: the next pass is intercepted.
            Pass(carrier, forceFailFor: c.Beat.Team);
            return;
        }

        // Shoot when clearly through; from further out, usually keep the ball and look for a better pass.
        if (distGoal < 24 && carrier.Player.Position != "GK" && (distGoal < 11 || _rng.NextDouble() < 0.04 + (24 - distGoal) / 70))
        {
            Shoot(carrier, forced: null);
            return;
        }

        if (_rng.NextDouble() < 0.82 || Pitch.Progress(team, carrier.Pos) > 0.95) Pass(carrier);
        else _nextDecisionAt = Clock + 0.8;
    }

    private void Pass(PlayerState carrier, int? forcedTarget = null, TeamSide? forceFailFor = null)
    {
        var team = carrier.Team;
        var profile = ProfileOf(team);
        var carrierProg = Pitch.Progress(team, carrier.Pos);

        PlayerState target;
        if (forcedTarget is { } ft)
        {
            target = _players[ft];
        }
        else
        {
            var candidates = _players.Where(p => p.Team == team && p.Index != carrier.Index &&
                                                 (p.Player.Position != "GK" || carrierProg < 0.35)).ToArray();
            var weights = candidates.Select(p =>
            {
                var gain = Pitch.Progress(team, p.Pos) - carrierProg;
                var w = 1 + profile.Directness * Math.Max(gain, -0.2) * 4 + (gain > 0 ? 0.3 : -0.1) * (1 - profile.Directness);
                var openness = Math.Min(NearestOpponentDistance(p.Pos, team), 8) / 8;
                var d = carrier.Pos.DistanceTo(p.Pos);
                var distFactor = d < 4 ? 0.3 : Math.Exp(-Math.Max(0, d - 22) / (10 + 25 * profile.Directness));
                return Math.Max(0.02, w) * (0.25 + openness) * distFactor * (0.7 + _rng.NextDouble() * 0.6);
            }).ToArray();
            target = candidates[Roulette(weights)];
        }

        var start = _ball;
        var lead = (Pitch.GoalAttackedBy(team) - target.Pos).Normalized() * 1.5;
        var intendedEnd = (target.Pos + lead).Clamp();
        var distance = start.DistanceTo(intendedEnd);
        var pressure = NearestOpponentDistance(start, team);
        var receiverPressure = NearestOpponentDistance(target.Pos, team);
        var bypassed = CountBypassed(team, carrierProg, Pitch.Progress(team, intendedEnd));

        var pSuccess = profile.PassingQuality * 0.95 + 0.16 - distance * 0.004
                       - (pressure < 2 ? 0.12 : pressure < 4 ? 0.05 : 0)
                       - (receiverPressure < 2 ? 0.1 : receiverPressure < 4 ? 0.04 : 0)
                       - bypassed * 0.015 + carrier.Player.Technique * 0.05;
        pSuccess = Math.Clamp(pSuccess, 0.25, 0.97);

        bool success;
        if (forcedTarget is not null) success = true;
        else if (forceFailFor is not null) success = false;
        else success = _rng.NextDouble() < pSuccess;

        var speed = 11 + distance * 0.28 + _rng.NextDouble() * 3;
        int receiverIdx;
        Vec2 end;
        if (success)
        {
            receiverIdx = target.Index;
            end = intendedEnd;
        }
        else
        {
            var interceptPoint = start + (intendedEnd - start) * 0.7;
            var interceptor = _players
                .Where(p => p.Team != team && (p.Player.Position != "GK" || Pitch.Progress(team, interceptPoint) > 0.85))
                .OrderBy(p => p.Pos.DistanceTo(interceptPoint))
                .First();
            receiverIdx = interceptor.Index;
            end = interceptor.Pos;
        }

        var flightDistance = Math.Max(start.DistanceTo(end), 1);
        _flight = new BallFlight(FlightKind.Pass, carrier.Index, target.Index, receiverIdx, start, end, speed, Clock - Dt,
            flightDistance / speed, success, pressure, receiverPressure, bypassed, null, null);
        _owner = null;
    }

    private void Shoot(PlayerState shooter, ActiveChance? forced)
    {
        var team = shooter.Team;
        var goal = Pitch.GoalAttackedBy(team);
        var pressureAtShot = NearestOpponentDistance(shooter.Pos, team);
        var xg = forced?.Beat.ChanceXg ??
                 Math.Round(ExpectedGoals(team, shooter.Pos) * (pressureAtShot < 1.5 ? 0.45 : pressureAtShot < 3 ? 0.7 : 1), 2);
        bool scores;
        string note;
        if (forced is not null)
        {
            scores = forced.Beat.Scores;
            note = scores ? "goal" : "saved";
            _chance = null;
        }
        else
        {
            scores = _script.AllowUnscriptedGoals && _rng.NextDouble() < xg;
            note = scores ? "goal" : _rng.NextDouble() < 0.45 ? "saved" : "wide";
        }

        var yOffset = note switch
        {
            "goal" => (_rng.NextDouble() - 0.5) * 6,
            "saved" => (_rng.NextDouble() - 0.5) * 2,
            _ => (_rng.NextDouble() < 0.5 ? -1 : 1) * (4.5 + _rng.NextDouble() * 3)
        };
        var end = new Vec2(goal.X, goal.Y + yOffset);
        var speed = 18 + shooter.Player.Technique * 8 + _rng.NextDouble() * 5;
        var pressure = NearestOpponentDistance(shooter.Pos, team);
        _flight = new BallFlight(FlightKind.Shot, shooter.Index, shooter.Index, shooter.Index, _ball, end, speed, Clock - Dt,
            shooter.Pos.DistanceTo(end) / speed, scores, pressure, 0, 0, xg, note);
        _owner = null;
    }

    public static double ExpectedGoals(TeamSide team, Vec2 pos)
    {
        var goal = Pitch.GoalAttackedBy(team);
        var dx = Math.Abs(goal.X - pos.X);
        var dy = Math.Abs(goal.Y - pos.Y);
        var d = Math.Sqrt(dx * dx + dy * dy);
        const double halfGoal = 3.66;
        var angle = Math.Atan2(2 * halfGoal * dx, dx * dx + dy * dy - halfGoal * halfGoal);
        if (angle < 0) angle += Math.PI;
        var z = -1.2 + 1.6 * angle - 0.09 * d;
        return Math.Clamp(1 / (1 + Math.Exp(-z)), 0.01, 0.85);
    }

    // ───────────────────────────── movement ─────────────────────────────

    private void MovePlayers()
    {
        var ballProgHome = Pitch.Progress(TeamSide.Home, _ball);
        var pressers = SelectPressers();

        foreach (var p in _players)
        {
            var (target, desired) = TargetFor(p, pressers);

            // Simple acceleration model: speed can rise by 3.5 m/s² and fall instantly.
            var speed = Math.Min(desired, p.Speed + 3.5 * Dt);
            var before = p.Pos;
            p.Pos = p.Pos.MoveTowards(target, speed * Dt).Clamp();
            var moved = before.DistanceTo(p.Pos);
            p.Speed = moved / Dt;
            p.Distance += moved;

            MaybeStartRun(p, ballProgHome);
        }
    }

    private (Vec2 Target, double Speed) TargetFor(PlayerState p, HashSet<int> pressers)
    {
        var profile = ProfileOf(p.Team);
        var goal = Pitch.GoalAttackedBy(p.Team);

        if (_owner == p.Index)
        {
            var dir = (goal - p.Pos).Normalized();
            var nearest = NearestOpponent(p.Pos, p.Team);
            var avoid = nearest is null ? new Vec2(0, 0) : (p.Pos - nearest.Pos).Normalized();
            var isChance = _chance is { } c && c.PlayerIdx == p.Index;
            return ((p.Pos + dir * 5 + avoid * 2).Clamp(), (isChance ? 5.2 : 3.0) + p.Player.Pace * 2.2);
        }

        if (_flight is { } f && f.ReceiverIdx == p.Index && f.Kind == FlightKind.Pass)
            return (f.End, 6.5);

        if (_chance is { } chance && chance.PlayerIdx == p.Index && chance.Beat.Team == Possession)
        {
            var box = new Vec2(goal.X - Math.Sign(goal.X - Pitch.Centre.X) * 13, Pitch.Centre.Y + (p.Index % 2 == 0 ? 4 : -4));
            return (box, 7.0 + p.Player.Pace * 1.8);
        }

        if (pressers.Contains(p.Index))
            return (_ball + (Pitch.GoalAttackedBy(p.Team.Opponent()) - _ball).Normalized() * 1.1, 4.5 + profile.PressIntensity * 3.2 * (0.6 + p.Player.Pace * 0.5));

        if (p.RunUntil > Clock)
            return (p.RunTarget, 7.4 + p.Player.Pace * 1.9);

        var slot = SlotPosition(p, kickOff: false);
        var gap = p.Pos.DistanceTo(slot);
        return (slot, gap > 10 ? 4.6 : gap > 3 ? 2.6 : 1.0);
    }

    private HashSet<int> SelectPressers()
    {
        var set = new HashSet<int>();
        if (_restartAt is not null) return set;
        var defending = Possession.Opponent();
        var profile = ProfileOf(defending);
        var attackerProgress = Pitch.Progress(Possession, _ball);
        var engage = attackerProgress > 1 - profile.PressHeight - 0.1;

        var ordered = _players.Where(p => p.Team == defending && p.Player.Position != "GK")
            .OrderBy(p => p.Pos.DistanceTo(_ball)).ToArray();
        if (ordered.Length == 0) return set;

        if (engage || ordered[0].Pos.DistanceTo(_ball) < 6) set.Add(ordered[0].Index);
        if (engage && profile.PressIntensity > 0.7 && ordered.Length > 1) set.Add(ordered[1].Index);
        if (_chance is { } c && c.Beat.Team == defending && ordered.Length > 1) set.Add(ordered[1].Index);
        return set;
    }

    private void MaybeStartRun(PlayerState p, double ballProgHome)
    {
        if (p.Team != Possession || _owner == p.Index || p.RunUntil > Clock) return;
        if (p.Player.Position is not ("LW" or "RW" or "ST" or "CM")) return;
        var ballProg = p.Team == TeamSide.Home ? ballProgHome : 1 - ballProgHome;
        if (ballProg < 0.3) return;
        if (_rng.NextDouble() > 0.0025 * (ProfileOf(p.Team).Tempo + 0.5)) return;

        var forward = (Pitch.GoalAttackedBy(p.Team) - p.Pos).Normalized();
        p.RunTarget = (p.Pos + forward * (18 + _rng.NextDouble() * 10) + new Vec2(0, (_rng.NextDouble() - 0.5) * 10)).Clamp();
        p.RunUntil = Clock + 2.5 + _rng.NextDouble() * 1.5;
    }

    // ───────────────────────────── duels ─────────────────────────────

    private void ResolveContact()
    {
        if (_owner is not { } o || _flight is not null) return;
        var carrier = _players[o];
        var defenders = _players.Where(p => p.Team != carrier.Team).ToArray();

        foreach (var d in defenders)
        {
            var dist = d.Pos.DistanceTo(carrier.Pos);
            if (dist < 3 && _pressured.Count == 0 && _pressured.Add(d.Index))
            {
                Emit(new MatchEvent
                {
                    Id = NextId(), Type = MatchEventType.Pressure, MatchSeconds = Clock, Period = Period,
                    Team = d.Team, PlayerId = d.Player.Id, TargetPlayerId = carrier.Player.Id,
                    Start = carrier.Pos, PressureMeters = Math.Round(dist, 1)
                });
            }
        }

        if (Clock - _ownerSince < 0.4) return;
        foreach (var d in defenders.Where(d => d.Pos.DistanceTo(carrier.Pos) < 1.6))
        {
            var profile = ProfileOf(d.Team);
            var p = 0.014 * (0.5 + profile.PressIntensity) * (1.3 - carrier.Player.Technique * 0.6);
            if (_chance is { } c)
            {
                if (c.Beat.Team == carrier.Team) p = 0;
                else if (c.Beat.Team == d.Team) p *= 3;
            }

            if (_rng.NextDouble() >= p) continue;
            Emit(new MatchEvent
            {
                Id = NextId(), Type = MatchEventType.Tackle, MatchSeconds = Clock, Period = Period,
                Team = d.Team, PlayerId = d.Player.Id, TargetPlayerId = carrier.Player.Id, Start = carrier.Pos, Success = true
            });
            ChangePossession(d, "tackle");
            return;
        }
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private void KickOff(TeamSide team)
    {
        foreach (var p in _players)
        {
            p.Pos = SlotPosition(p, kickOff: true);
            p.RunUntil = 0;
            p.Speed = 0;
        }

        _flight = null;
        _ball = Pitch.Centre;
        var striker = _players.First(p => p.Team == team && p.Player.Position == "ST");
        striker.Pos = Pitch.Centre;
        Possession = team;
        _owner = striker.Index;
        _ownerSince = Clock;
        _nextDecisionAt = Clock + 1.0;
        _pressured.Clear();
        Emit(new MatchEvent
        {
            Id = NextId(), Type = MatchEventType.KickOff, MatchSeconds = Clock, Period = Period,
            Team = team, PlayerId = striker.Player.Id, Start = Pitch.Centre
        });
    }

    private void GiveBall(PlayerState p)
    {
        _owner = p.Index;
        _ownerSince = Clock;
        _pressured.Clear();
        var tempo = ProfileOf(p.Team).Tempo;
        var hold = 1.0 + (1 - tempo) * 2.4 + _rng.NextDouble() * 1.0;
        if (NearestOpponentDistance(p.Pos, p.Team) < 3) hold *= 0.5;
        _nextDecisionAt = Clock + hold;
    }

    private void ChangePossession(PlayerState newOwner, string reason)
    {
        Possession = newOwner.Team;
        Emit(new MatchEvent
        {
            Id = NextId(), Type = MatchEventType.PossessionChange, MatchSeconds = Clock, Period = Period,
            Team = newOwner.Team, PlayerId = newOwner.Player.Id, Start = newOwner.Pos, Note = reason
        });
        GiveBall(newOwner);
    }

    private Vec2 SlotPosition(PlayerState p, bool kickOff)
    {
        double prog;
        if (kickOff)
        {
            prog = Math.Min(p.SlotProg * 0.75, 0.47);
        }
        else
        {
            var ballProg = Pitch.Progress(p.Team, _ball);
            var inPossession = Possession == p.Team;
            if (p.Player.Position == "GK")
            {
                prog = 0.03 + 0.1 * ballProg;
            }
            else if (inPossession)
            {
                prog = p.SlotProg * 0.6 + ballProg * 0.4 + 0.06;
            }
            else
            {
                var block = ProfileOf(p.Team).PressHeight;
                prog = p.SlotProg * 0.5 + ballProg * 0.35 + (block - 0.5) * 0.2 - 0.04;
            }

            prog = Math.Clamp(prog, 0.02, 0.93);
        }

        var x = p.Team == TeamSide.Home ? prog * Pitch.Length : (1 - prog) * Pitch.Length;
        var y = 0.05 * Pitch.Width + p.SlotWidth * Pitch.Width * 0.9;
        if (!kickOff)
        {
            y += (_ball.Y - Pitch.Width / 2) * 0.25;
            y += Math.Sin(Clock * 0.2 + p.Index) * 0.8;
        }

        return new Vec2(x, y).Clamp();
    }

    private PlayerState? NearestOpponent(Vec2 pos, TeamSide team) =>
        _players.Where(p => p.Team != team).MinBy(p => p.Pos.DistanceTo(pos));

    private double NearestOpponentDistance(Vec2 pos, TeamSide team) =>
        Math.Round(NearestOpponent(pos, team)?.Pos.DistanceTo(pos) ?? 50, 1);

    private int CountBypassed(TeamSide team, double fromProg, double toProg)
    {
        if (toProg <= fromProg) return 0;
        return _players.Count(p => p.Team != team && p.Player.Position != "GK" &&
                                   Pitch.Progress(team, p.Pos) is var prog && prog > fromProg && prog < toProg);
    }

    private int Roulette(double[] weights)
    {
        var total = weights.Sum();
        var r = _rng.NextDouble() * total;
        for (var i = 0; i < weights.Length; i++)
        {
            r -= weights[i];
            if (r <= 0) return i;
        }

        return weights.Length - 1;
    }

    private TrackingFrame Frame() =>
        new(Clock, _ball, _players.Select(p => p.Pos).ToArray(), _owner);

    private string NextId() => $"e{++_eventCounter:00000}";

    private void Emit(MatchEvent e) => _emitted.Add(e);

    // ───────────────────────────── state types ─────────────────────────────

    private sealed class PlayerState(Player player, int index, double slotProg, double slotWidth)
    {
        public Player Player { get; } = player;
        public int Index { get; } = index;
        public TeamSide Team => Player.Team;
        public double SlotProg { get; } = slotProg;
        public double SlotWidth { get; } = slotWidth;
        public Vec2 Pos { get; set; }
        public double Speed { get; set; }
        public double Distance { get; set; }
        public double RunUntil { get; set; }
        public Vec2 RunTarget { get; set; }
    }

    private enum FlightKind { Pass, Shot }

    private sealed record BallFlight(
        FlightKind Kind,
        int FromIdx,
        int IntendedIdx,
        int ReceiverIdx,
        Vec2 Start,
        Vec2 End,
        double Speed,
        double StartTime,
        double Duration,
        bool Success,
        double PressureMeters,
        double ReceiverPressureMeters,
        int Bypassed,
        double? Xg,
        string? Note);

    private sealed record ActiveChance(StorylineBeat Beat, int PlayerIdx, double StartedAt);

    /// <summary>Speed (m/s) above which a run counts as a sprint.</summary>
    public static double SprintSpeed => SprintThreshold;
}
