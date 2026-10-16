using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using WhyLine.Agents.Abstractions;
using WhyLine.Core.Domain;
using WhyLine.Core.Engine;
using WhyLine.Core.Simulation;

namespace WhyLine.Web.Live;

public sealed class MatchSettings
{
    public const string Section = "Match";
    public int Seed { get; set; } = 7;

    /// <summary>Which fixture to play (see MatchCatalog): press-flip, comeback, stalemate, smash-and-grab or thriller.</summary>
    public string Scenario { get; set; } = MatchCatalog.DefaultId;

    public double Speed { get; set; } = 15;
    public bool AutoStart { get; set; } = true;
}

public sealed record LiveStatus(
    int Generation,
    double Clock,
    int Period,
    int HomeScore,
    int AwayScore,
    bool Running,
    bool Finished,
    double Speed,
    int Seed,
    string Scenario);

/// <summary>A moment handed to the newsroom, tagged with the match generation so restarts drop stale work.</summary>
public sealed record QueuedMoment(int Generation, MomentContext Context, double LiveClock);

/// <summary>
/// The live match: runs the synthetic feed in (accelerated) real time, feeds the stats engine and auto-eventing
/// detector, emits fast-path overlay cues, queues moments for the newsroom, and stores everything clients replay.
/// </summary>
public sealed class LiveMatch : BackgroundService, INewsroomSink
{
    private const int MaxTraces = 400;
    private const double SnapshotEvery = 10;

    private readonly object _gate = new();
    private readonly IHubContext<MatchHub> _hub;
    private readonly ILogger<LiveMatch> _logger;
    private readonly Channel<QueuedMoment> _moments = Channel.CreateUnbounded<QueuedMoment>();

    private MatchSimulator _sim = null!;
    private MatchAnalyzer _analyzer = null!;
    private TrackingEventDetector _detector = null!;
    private List<TrackingFrame> _frames = [];
    private List<OverlayCue> _cues = [];
    private List<AgentTrace> _traces = [];
    private List<MatchSnapshot> _snapshots = [];
    private Dictionary<string, MatchEvent> _eventsById = [];
    private int _cueCounter;
    private double _lastPassCueAt = -999;
    private double _nextSnapshotAt;
    private double _publishFrom;
    private MatchSnapshot? _latest;

    public LiveMatch(IHubContext<MatchHub> hub, MatchSettings settings, AgentOptions agents, ILogger<LiveMatch> logger)
    {
        _hub = hub;
        _logger = logger;
        AgentMode = agents.Mode;
        Speed = settings.Speed;
        Running = settings.AutoStart;
        Reset(settings.Seed, settings.Scenario);
    }

    public string AgentMode { get; }
    public double Speed { get; private set; }
    public bool Running { get; private set; }
    public int Seed { get; private set; }
    public MatchScenario Scenario { get; private set; } = MatchCatalog.All[0];
    public int Generation { get; private set; }
    public MatchMetadata Metadata { get; private set; } = null!;

    /// <summary>Demo mode: the points of this fixture the Studio can jump to.</summary>
    public IReadOnlyList<DemoChapter> Chapters { get; private set; } = [];
    public ChannelReader<QueuedMoment> MomentQueue => _moments.Reader;

    /// <summary>Raised (on a background thread) whenever a new cue is published.</summary>
    public event Action<OverlayCue>? CuePublished;

    // ───────────────────────────── control ─────────────────────────────

    /// <summary>Starts a fixture from kick-off. Pass a scenario id to switch fixtures, or null to replay the current one.</summary>
    public void Reset(int seed, string? scenarioId = null)
    {
        lock (_gate)
        {
            Seed = seed;
            if (scenarioId is not null) Scenario = MatchCatalog.ById(scenarioId);
            Generation++;
            var script = Scenario.Build(seed);
            Metadata = script.Metadata;
            Chapters = DemoChapters.For(script);
            _sim = new MatchSimulator(script);
            _analyzer = new MatchAnalyzer(script.Metadata);
            _detector = new TrackingEventDetector(script.Metadata);
            _frames = new List<TrackingFrame>(54_100);
            _cues = [];
            _traces = [];
            _snapshots = [];
            _eventsById = [];
            _lastPassCueAt = -999;
            _nextSnapshotAt = 0;
            _publishFrom = 0;
            _latest = _analyzer.Snapshot(0);
        }
    }

    /// <summary>
    /// Demo mode: moves the match to <paramref name="matchSeconds"/> (replaying from kick-off to go back). The skipped
    /// stretch is simulated and analysed as usual (stats, momentum, moments for recaps), but its moments are not sent to
    /// the agents, and cards still in progress from before the jump are dropped, so only fresh cards follow.
    /// </summary>
    public void JumpTo(double matchSeconds)
    {
        lock (_gate)
        {
            if (matchSeconds < _sim.Clock) Reset(Seed);
            else Generation++; // drops queued moments from before the jump

            var skipped = new List<OverlayCue>();
            while (_sim.Clock < matchSeconds && StepLocked(skipped, queueMoments: false)) { }
            _cues.AddRange(skipped);
            _latest = _analyzer.Snapshot(_sim.Clock);
            _publishFrom = _sim.Clock;
        }
    }

    public void SetRunning(bool running) => Running = running;
    public void SetSpeed(double speed) => Speed = Math.Clamp(speed, 0.5, 60);

    // ───────────────────────────── loop ─────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var sw = Stopwatch.StartNew();
        var last = sw.Elapsed.TotalSeconds;
        var accumulator = 0.0;
        var lastBroadcast = 0.0;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = sw.Elapsed.TotalSeconds;
            var realDt = now - last;
            last = now;
            if (!Running) continue;

            accumulator += realDt * Speed;
            var steps = Math.Min((int)(accumulator / MatchSimulator.Dt), 60);
            accumulator -= steps * MatchSimulator.Dt;

            try
            {
                for (var i = 0; i < steps; i++)
                    if (!StepOnce()) break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Simulation step failed");
                Running = false;
            }

            if (now - lastBroadcast > 0.5)
            {
                lastBroadcast = now;
                await _hub.Clients.All.SendAsync("status", Status(), stoppingToken);
            }
        }
    }

    private bool StepOnce()
    {
        var outgoing = new List<OverlayCue>();
        lock (_gate)
        {
            if (!StepLocked(outgoing, queueMoments: true)) return false;
            _cues.AddRange(outgoing);
        }

        foreach (var cue in outgoing) Announce(cue);
        return true;
    }

    /// <summary>One simulation step. Call under the lock; fast-path cues go to <paramref name="outgoing"/>.</summary>
    private bool StepLocked(List<OverlayCue> outgoing, bool queueMoments)
    {
        if (_sim.Finished)
        {
            Running = false;
            return false;
        }

        var step = _sim.Step();
        var frame = step.Frame;
        _frames.Add(frame);
        _detector.IngestFrame(frame);

        foreach (var crossing in _analyzer.IngestFrame(frame))
            outgoing.Add(FastCue(crossing));

        var moments = new List<Moment>();
        foreach (var e in step.Events)
        {
            _eventsById[e.Id] = e;
            _detector.IngestTruth(e);
            moments.AddRange(_analyzer.Ingest(e));
            if (FastCue(e) is { } cue) outgoing.Add(cue);
        }

        moments.AddRange(_analyzer.Tick(frame.MatchSeconds));

        if (frame.MatchSeconds >= _nextSnapshotAt || moments.Count > 0)
        {
            _latest = _analyzer.Snapshot(frame.MatchSeconds);
            if (frame.MatchSeconds >= _nextSnapshotAt)
            {
                _snapshots.Add(_latest);
                _nextSnapshotAt += SnapshotEvery;
            }
        }

        foreach (var m in queueMoments ? moments : [])
        {
            var evidence = m.EvidenceEventIds.Select(id => _eventsById.GetValueOrDefault(id)).OfType<MatchEvent>().ToArray();
            _moments.Writer.TryWrite(new QueuedMoment(Generation, new MomentContext(m, _latest!, Metadata, evidence), frame.MatchSeconds));
        }

        return true;
    }

    // ───────────────────────────── fast path ─────────────────────────────

    private OverlayCue FastCue(ThresholdCrossing c)
    {
        var p = Metadata.PlayerById(c.PlayerId)!;
        return new OverlayCue
        {
            Id = NextCueId(),
            Type = c.Kind,
            MatchSeconds = c.MatchSeconds,
            DurationSeconds = c.Kind == CueType.SpeedBadge ? 4 : 6,
            Title = p.ShortName,
            Body = c.Kind == CueType.SpeedBadge ? $"{c.Value.ToString("0.0", CultureInfo.InvariantCulture)} km/h" : $"{c.Value:0} km covered",
            PlayerId = p.Id,
            Team = p.Team,
            Anchor = c.Position,
            Verified = true
        };
    }

    private OverlayCue? FastCue(MatchEvent e)
    {
        var p = Metadata.PlayerById(e.PlayerId);
        switch (e.Type)
        {
            case MatchEventType.Shot when p is not null:
                return new OverlayCue
                {
                    Id = NextCueId(), Type = CueType.ShotSpeed, MatchSeconds = e.MatchSeconds, DurationSeconds = 5,
                    Title = p.ShortName,
                    Body = $"Shot {(e.Speed ?? 0) * 3.6:0} km/h, xG {(e.Xg ?? 0).ToString("0.00", CultureInfo.InvariantCulture)}, {e.Note}",
                    PlayerId = p.Id, Team = p.Team, Anchor = e.Start, EvidenceEventIds = [e.Id], Verified = true
                };

            case MatchEventType.Pass when p is not null && e.Success == true && e.MatchSeconds - _lastPassCueAt > 45:
            {
                var difficulty = PassQuality.Difficulty(e);
                if (difficulty < 0.6) return null;
                _lastPassCueAt = e.MatchSeconds;
                var receiver = Metadata.PlayerById(e.TargetPlayerId);
                return new OverlayCue
                {
                    Id = NextCueId(), Type = CueType.PassQuality, MatchSeconds = e.MatchSeconds, DurationSeconds = 5,
                    Title = $"{p.ShortName} → {receiver?.ShortName}",
                    Body = $"{PassQuality.Label(difficulty)} pass, difficulty {difficulty * 100:0}/100, {e.Distance:0} m, {(e.Speed ?? 0) * 3.6:0} km/h",
                    PlayerId = p.Id, Team = p.Team, Anchor = e.End ?? e.Start, EvidenceEventIds = [e.Id], Verified = true
                };
            }

            case MatchEventType.Goal when p is not null:
                return new OverlayCue
                {
                    Id = NextCueId(), Type = CueType.ScoreBug, MatchSeconds = e.MatchSeconds, DurationSeconds = 8, Priority = 3,
                    Title = "GOAL", Body = $"{p.Name}, {Metadata.Home.ShortName} {e.Note?.Replace("-", " - ")} {Metadata.Away.ShortName}",
                    PlayerId = p.Id, Team = p.Team, Anchor = e.Start, EvidenceEventIds = [e.Id], Verified = true
                };

            default:
                return null;
        }
    }

    private string NextCueId() => $"f{Interlocked.Increment(ref _cueCounter):00000}";

    // ───────────────────────────── INewsroomSink ─────────────────────────────

    public void Publish(OverlayCue cue)
    {
        lock (_gate)
        {
            if (cue.MatchSeconds < _publishFrom) return; // written for a stretch the demo jumped over
            _cues.Add(cue);
        }

        Announce(cue);
    }

    public void Trace(AgentTrace trace)
    {
        lock (_gate)
        {
            _traces.Add(trace);
            if (_traces.Count > MaxTraces) _traces.RemoveRange(0, _traces.Count - MaxTraces);
        }

        _ = _hub.Clients.All.SendAsync("trace", trace);
    }

    private void Announce(OverlayCue cue)
    {
        CuePublished?.Invoke(cue);
        _ = _hub.Clients.All.SendAsync("cue", cue);
    }

    // ───────────────────────────── queries ─────────────────────────────

    public LiveStatus Status()
    {
        lock (_gate)
            return new LiveStatus(Generation, _sim.Clock, _sim.Period, _sim.HomeScore, _sim.AwayScore, Running, _sim.Finished, Speed, Seed, Scenario.Id);
    }

    public double Clock
    {
        get { lock (_gate) return _sim.Clock; }
    }

    public TrackingFrame? FrameAt(double matchSeconds)
    {
        lock (_gate)
        {
            if (_frames.Count == 0) return null;
            var idx = (int)Math.Round(matchSeconds / MatchSimulator.Dt) - 1;
            return _frames[Math.Clamp(idx, 0, _frames.Count - 1)];
        }
    }

    public IReadOnlyList<OverlayCue> CuesFor(string? profileKey)
    {
        lock (_gate)
            return _cues.Where(c => c.ProfileKey is null || c.ProfileKey == profileKey).ToArray();
    }

    public IReadOnlyList<OverlayCue> AllCues(int since = 0)
    {
        lock (_gate) return _cues.Skip(since).ToArray();
    }

    public IReadOnlyList<AgentTrace> RecentTraces(int count)
    {
        lock (_gate) return _traces.TakeLast(count).Reverse().ToArray();
    }

    public MatchSnapshot LatestSnapshot()
    {
        lock (_gate) return _latest!;
    }

    /// <summary>The last snapshot at or before <paramref name="matchSeconds"/> — used for spoiler-safe recaps.</summary>
    public MatchSnapshot SnapshotAt(double matchSeconds)
    {
        lock (_gate)
        {
            var snap = _snapshots.LastOrDefault(s => s.MatchSeconds <= matchSeconds);
            return snap ?? _analyzer.Snapshot(0);
        }
    }

    public IReadOnlyList<Moment> MomentsUpTo(double matchSeconds)
    {
        lock (_gate) return _analyzer.Moments.Where(m => m.MatchSeconds <= matchSeconds).ToArray();
    }

    public IReadOnlyList<MatchEvent> EventsSince(double matchSeconds)
    {
        lock (_gate) return _analyzer.Events.Where(e => e.MatchSeconds > matchSeconds).ToArray();
    }

    public MatchEvent? EventById(string id)
    {
        lock (_gate) return _eventsById.GetValueOrDefault(id);
    }

    public AutoEventingStats AutoEventing()
    {
        lock (_gate) return _detector.Stats();
    }
}
