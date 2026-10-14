using System.Text.Json.Serialization;

namespace WhyLine.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MatchEventType
{
    KickOff,
    Pass,
    Shot,
    Goal,
    Tackle,
    Interception,
    PossessionChange,
    Pressure,
    TacticalChange,
    HalfTime,
    FullTime
}

/// <summary>One synthetic match event, as it would arrive from a live data feed.</summary>
public sealed record MatchEvent
{
    public required string Id { get; init; }
    public required MatchEventType Type { get; init; }
    public required double MatchSeconds { get; init; }
    public int Period { get; init; } = 1;
    public TeamSide? Team { get; init; }
    public int? PlayerId { get; init; }
    public int? TargetPlayerId { get; init; }
    public Vec2 Start { get; init; }
    public Vec2? End { get; init; }
    public bool? Success { get; init; }

    /// <summary>Ball speed in m/s (passes and shots).</summary>
    public double? Speed { get; init; }

    /// <summary>Distance travelled by the ball in metres.</summary>
    public double? Distance { get; init; }

    /// <summary>Distance in metres from the actor to the nearest opponent at the moment of the action.</summary>
    public double? PressureMeters { get; init; }

    /// <summary>Distance in metres from the receiver to the nearest opponent (passes).</summary>
    public double? ReceiverPressureMeters { get; init; }

    /// <summary>Number of opponents bypassed by the pass (lines broken proxy).</summary>
    public int? OpponentsBypassed { get; init; }

    public double? Xg { get; init; }
    public string? Note { get; init; }

    public int Minute => (int)(MatchSeconds / 60) + 1;
}

/// <summary>Positions of every player and the ball at an instant (10 Hz).</summary>
public sealed record TrackingFrame(double MatchSeconds, Vec2 Ball, Vec2[] Players, int? BallOwnerIndex);
