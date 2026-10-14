using System.Globalization;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Template;

/// <summary>Reads facts by key for templates. Numbers are formatted exactly as the Verifier will accept them.</summary>
public sealed class FactReader(IEnumerable<Fact> facts)
{
    private readonly Dictionary<string, Fact> _facts = facts.GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.Last());

    public bool Has(string key) => _facts.ContainsKey(key);

    /// <summary>Text value, or the number formatted plainly.</summary>
    public string this[string key] => _facts.TryGetValue(key, out var f) ? (f.Text ?? Num(key)) : "";

    public string Num(string key, string format = "0.#") =>
        _facts.TryGetValue(key, out var f) && f.Number is { } n ? n.ToString(format, CultureInfo.InvariantCulture) : "";

    public double? Number(string key) => _facts.TryGetValue(key, out var f) ? f.Number : null;

    public IEnumerable<Fact> Numeric => _facts.Values.Where(f => f.Number is not null && f.Key != "minute");
}

public static class PitchWords
{
    /// <summary>Plain-language location from the attacking team's point of view, for audio description.</summary>
    public static string Where(Vec2 p, TeamSide attacking)
    {
        var leftIsHighY = attacking == TeamSide.Home;
        var lane = p.Y < Pitch.Width / 3 ? (leftIsHighY ? "right" : "left")
            : p.Y > Pitch.Width * 2 / 3 ? (leftIsHighY ? "left" : "right")
            : "central";
        var third = Pitch.Third(attacking, p) switch
        {
            3 => "the final third",
            2 => "midfield",
            _ => "their own half"
        };
        return lane == "central" ? $"centrally in {third}" : $"down the {lane} side, in {third}";
    }
}
