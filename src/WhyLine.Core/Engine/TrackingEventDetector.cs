using WhyLine.Core.Domain;

namespace WhyLine.Core.Engine;

public sealed record DetectedPass(double MatchSeconds, int FromPlayerId, int ToPlayerId, bool Completed, double DistanceM, double BallSpeedKmh);

public sealed record AutoEventingStats(
    int DetectedPasses,
    int GroundTruthPasses,
    int Matched,
    double RecallPct,
    double PrecisionPct,
    double MeanSpeedErrorKmh);

/// <summary>
/// Auto-eventing: derives passes and ball speed from tracking positions only (no event feed), the way a model
/// running over a video feed would, then scores itself against the ground-truth event feed.
/// </summary>
public sealed class TrackingEventDetector
{
    private const double ControlRadius = 1.6;
    private const double MaxRelativeSpeed = 3.5;
    private const double MinPassBallSpeed = 6;

    private readonly int[] _playerIds;
    private readonly TeamSide[] _teams;
    private readonly List<DetectedPass> _detected = [];
    private readonly List<(DetectedPass Detected, MatchEvent Truth)> _matches = [];
    private readonly List<MatchEvent> _truth = [];

    private TrackingFrame? _prev;
    private int? _controller;
    private int? _lastController;
    private int? _stableController;
    private int _controllerFrames;
    private double _gapPeakSpeed;
    private readonly List<double> _gapSpeeds = [];
    private Vec2 _releasePoint;

    public TrackingEventDetector(MatchMetadata meta)
    {
        var ordered = meta.Players.OrderBy(p => p.Id).ToArray();
        _playerIds = ordered.Select(p => p.Id).ToArray();
        _teams = ordered.Select(p => p.Team).ToArray();
    }

    public IReadOnlyList<DetectedPass> Detected => _detected;

    public DetectedPass? IngestFrame(TrackingFrame frame)
    {
        DetectedPass? result = null;
        if (_prev is { } prev && frame.MatchSeconds > prev.MatchSeconds)
        {
            var dt = frame.MatchSeconds - prev.MatchSeconds;
            var ballVel = (frame.Ball - prev.Ball) * (1 / dt);
            var ballSpeed = ballVel.Length;

            int? controller = null;
            var best = ControlRadius;
            var keepCurrent = false;
            for (var i = 0; i < frame.Players.Length; i++)
            {
                var d = frame.Players[i].DistanceTo(frame.Ball);
                if (d > ControlRadius) continue;
                var playerVel = (frame.Players[i] - prev.Players[i]) * (1 / dt);
                if ((ballVel - playerVel).Length > MaxRelativeSpeed) continue;
                if (i == _controller) keepCurrent = true; // hysteresis: contested balls stay with the carrier
                if (d > best) continue;
                best = d;
                controller = i;
            }

            if (keepCurrent) controller = _controller;

            if (ballSpeed > 40) controller = _controller; // re-spot (kick-off): ignore

            _controllerFrames = controller is not null && controller == _controller ? _controllerFrames + 1 : 1;
            if (controller is not null && _controllerFrames >= 3) _stableController = controller;

            if (controller is null && _controller is not null)
            {
                _lastController = _stableController ?? _controller;
                _releasePoint = prev.Ball;
                _gapPeakSpeed = 0;
                _gapSpeeds.Clear();
            }

            if (controller is null)
            {
                _gapPeakSpeed = Math.Max(_gapPeakSpeed, ballSpeed);
                _gapSpeeds.Add(ballSpeed);
            }

            if (controller is { } now && _controller is null && _lastController is { } from && now != from &&
                _gapPeakSpeed >= MinPassBallSpeed && _gapPeakSpeed < 40)
            {
                result = new DetectedPass(frame.MatchSeconds, _playerIds[from], _playerIds[now], _teams[from] == _teams[now],
                    Math.Round(_releasePoint.DistanceTo(frame.Ball), 1), Math.Round(Median(_gapSpeeds) * 3.6, 1));
                _detected.Add(result);
                TryMatch(result);
            }

            _controller = controller;
        }

        _prev = frame;
        return result;
    }

    /// <summary>Feed the ground-truth event stream so accuracy can be measured.</summary>
    public void IngestTruth(MatchEvent e)
    {
        if (e.Type != MatchEventType.Pass) return;
        _truth.Add(e);
        var candidate = _detected.LastOrDefault(d => Math.Abs(d.MatchSeconds - e.MatchSeconds) < 1.5 && d.FromPlayerId == e.PlayerId &&
                                                     _matches.All(m => m.Detected != d));
        if (candidate is not null) _matches.Add((candidate, e));
    }

    private void TryMatch(DetectedPass d)
    {
        var truth = _truth.LastOrDefault(e => Math.Abs(e.MatchSeconds - d.MatchSeconds) < 1.5 && e.PlayerId == d.FromPlayerId &&
                                              _matches.All(m => m.Truth != e));
        if (truth is not null) _matches.Add((d, truth));
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    public AutoEventingStats Stats()
    {
        var speedErrors = _matches.Where(m => m.Truth.Speed is not null)
            .Select(m => Math.Abs(m.Detected.BallSpeedKmh - m.Truth.Speed!.Value * 3.6)).ToArray();
        return new AutoEventingStats(
            _detected.Count,
            _truth.Count,
            _matches.Count,
            _truth.Count == 0 ? 0 : Math.Round(_matches.Count * 100.0 / _truth.Count, 1),
            _detected.Count == 0 ? 0 : Math.Round(_matches.Count * 100.0 / _detected.Count, 1),
            speedErrors.Length == 0 ? 0 : Math.Round(speedErrors.Average(), 1));
    }
}
