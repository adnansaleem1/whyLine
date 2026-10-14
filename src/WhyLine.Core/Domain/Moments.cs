using System.Globalization;
using System.Text.Json.Serialization;

namespace WhyLine.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MomentKind
{
    Goal,
    BigChance,
    MomentumSwing,
    PressSurge,
    ControlSpell,
    DifficultPass,
    PassStreak,
    ShotSpeed,
    QuietPlayer,
    TacticalShift,
    HalfTime,
    FullTime
}

/// <summary>
/// A verified, numeric or textual fact computed by code. Narratives may only use numbers that appear in facts;
/// the Verifier enforces this.
/// </summary>
public sealed record Fact(string Key, string Label, double? Number = null, string? Text = null, string Unit = "")
{
    public string Display => Number is { } n
        ? (Unit switch
        {
            "%" => $"{n.ToString("0", CultureInfo.InvariantCulture)}%",
            "km/h" => $"{n.ToString("0", CultureInfo.InvariantCulture)} km/h",
            "m" => $"{n.ToString(n < 10 && n % 1 != 0 ? "0.0" : "0", CultureInfo.InvariantCulture)} m",
            "km" => $"{n.ToString("0.0", CultureInfo.InvariantCulture)} km",
            "xG" => n.ToString("0.00", CultureInfo.InvariantCulture),
            "" => n.ToString(n % 1 == 0 ? "0" : "0.0", CultureInfo.InvariantCulture),
            _ => $"{n.ToString("0.#", CultureInfo.InvariantCulture)} {Unit}"
        })
        : Text ?? "";
}

/// <summary>A candidate moment detected by the stats engine, with the evidence that proves it.</summary>
public sealed record Moment
{
    public required string Id { get; init; }
    public required MomentKind Kind { get; init; }
    public required double MatchSeconds { get; init; }
    public TeamSide? Team { get; init; }
    public int? PlayerId { get; init; }
    public int? SecondaryPlayerId { get; init; }

    /// <summary>0..1 — how much this moment changes the story of the match.</summary>
    public double Importance { get; init; }

    public IReadOnlyList<string> EvidenceEventIds { get; init; } = [];
    public IReadOnlyList<Fact> Facts { get; init; } = [];

    public int Minute => (int)(MatchSeconds / 60) + 1;
    public Fact? FactOf(string key) => Facts.FirstOrDefault(f => f.Key == key);
}
