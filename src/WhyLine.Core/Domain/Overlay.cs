using System.Text.Json.Serialization;

namespace WhyLine.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CueType
{
    PlayerTag,
    SpeedBadge,
    DistanceBadge,
    PassQuality,
    ShotSpeed,
    ScoreBug,
    InsightCard,
    MomentumAnnotation,
    Recap
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Persona
{
    Analyst,
    Casual,
    PlayerFocus,
    AudioDescription
}

/// <summary>A sentence and the event ids that prove it.</summary>
public sealed record Citation(string Text, IReadOnlyList<string> EventIds);

/// <summary>
/// A timed, machine-readable overlay instruction. <see cref="MatchSeconds"/> is the match-clock moment the cue
/// belongs to; clients must not display it before their own playback reaches that moment (spoiler-safe sync).
/// </summary>
public sealed record OverlayCue
{
    public required string Id { get; init; }
    public required CueType Type { get; init; }
    public required double MatchSeconds { get; init; }
    public double DurationSeconds { get; init; } = 8;
    public int Priority { get; init; } = 1;

    /// <summary>Null = for every viewer. Otherwise the profile key this cue was personalised for.</summary>
    public string? ProfileKey { get; init; }

    public string Language { get; init; } = "en";
    public required string Title { get; init; }
    public string Body { get; init; } = "";
    public int? PlayerId { get; init; }
    public TeamSide? Team { get; init; }
    public Vec2? Anchor { get; init; }
    public IReadOnlyList<Citation> Citations { get; init; } = [];
    public IReadOnlyList<string> EvidenceEventIds { get; init; } = [];
    public string? MomentId { get; init; }
    public MomentKind? MomentKind { get; init; }

    /// <summary>"fast-path" (code, milliseconds) or "agents" (verified narrative).</summary>
    public string Source { get; init; } = "fast-path";

    public bool Verified { get; init; }
    public double? GenerationMs { get; init; }
}

/// <summary>What a viewer wants. Agents generate one narrative per active profile.</summary>
public sealed record ViewerProfile(Persona Persona, string Language, TeamSide? FavouriteTeam, int? FocusPlayerId)
{
    public string Key => $"{Persona}|{Language}|{FavouriteTeam?.ToString() ?? "-"}|{FocusPlayerId?.ToString() ?? "-"}";

    public static ViewerProfile Default => new(Persona.Casual, "en", null, null);
}

public static class Languages
{
    public static readonly IReadOnlyDictionary<string, string> Supported = new Dictionary<string, string>
    {
        ["en"] = "English",
        ["ur"] = "Urdu",
        ["es"] = "Spanish",
        ["ar"] = "Arabic"
    };

    public static bool IsRightToLeft(string code) => code is "ur" or "ar";
}
