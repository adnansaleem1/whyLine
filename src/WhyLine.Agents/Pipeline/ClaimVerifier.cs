using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WhyLine.Agents.Abstractions;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Pipeline;

public sealed record VerificationResult(bool Passed, IReadOnlyList<string> Issues)
{
    public string Feedback => string.Join(" ", Issues);
}

/// <summary>
/// The Verifier agent's tool: checks every number and every player reference in a narrative against the facts
/// computed by the stats engine. Anything it cannot trace back to a fact is rejected before a viewer sees it.
/// </summary>
public sealed partial class ClaimVerifier
{
    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex NumberPattern();

    /// <param name="language">The viewer's language. For Urdu and Arabic the draft must be written in Arabic script.</param>
    public VerificationResult Verify(NarrativeDraft draft, IEnumerable<Fact> facts, MatchMetadata meta, IEnumerable<int?> allowedPlayerIds,
        string? language = null)
    {
        var factList = facts.ToList();
        var issues = new List<string>();
        var text = NormalizeDigits(draft.FullText);

        if (draft.Sentences.Count == 0 || string.IsNullOrWhiteSpace(draft.Title))
            issues.Add("The narrative is empty.");

        var allowed = AllowedNumbers(factList);
        foreach (Match m in NumberPattern().Matches(text))
        {
            if (!double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) continue;
            if (allowed.Any(a => Math.Abs(a - n) < 0.051)) continue;
            issues.Add($"The number {m.Value} is not supported by any fact. Use only numbers from FACTS.");
        }

        var allowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in allowedPlayerIds)
            if (meta.PlayerById(id) is { } p) allowedNames.Add(p.Name);
        foreach (var f in factList.Where(f => f.Text is not null))
            foreach (var p in meta.Players.Where(p => f.Text!.Contains(p.Name, StringComparison.OrdinalIgnoreCase)))
                allowedNames.Add(p.Name);

        foreach (var p in meta.Players)
        {
            if (allowedNames.Contains(p.Name)) continue;
            if (ContainsWord(text, p.Name) || (p.ShortName.Length > 3 && ContainsWord(text, p.ShortName) &&
                                               !allowedNames.Any(a => a.EndsWith(p.ShortName, StringComparison.OrdinalIgnoreCase))))
                issues.Add($"{p.Name} is mentioned but is not part of this moment's evidence.");
        }

        if (language is "ur" or "ar" && !MostlyArabicScript(draft.FullText, meta))
            issues.Add(language == "ur"
                ? "Write in Urdu script (اردو), not Roman Urdu. Keep only player and club names in Latin letters."
                : "Write in Arabic script. Keep only player and club names in Latin letters.");

        return new VerificationResult(issues.Count == 0, issues.Distinct().ToArray());
    }

    /// <summary>
    /// True when most letters are Arabic script once player and club names (which stay in Latin letters) are removed.
    /// Catches Roman Urdu, which the evaluation run showed the Localizer sometimes writes.
    /// </summary>
    public static bool MostlyArabicScript(string text, MatchMetadata meta)
    {
        var names = meta.Players.Select(p => p.Name).Concat(meta.Players.Select(p => p.ShortName))
            .Concat([meta.Home.Name, meta.Away.Name, meta.Home.ShortName, meta.Away.ShortName])
            .Concat(meta.Home.Name.Split(' ')).Concat(meta.Away.Name.Split(' '))
            .Where(n => n.Length > 1).OrderByDescending(n => n.Length);
        foreach (var name in names) text = text.Replace(name, " ", StringComparison.OrdinalIgnoreCase);

        int arabic = 0, latin = 0;
        foreach (var c in text)
        {
            if (c is >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F' or >= '\uFB50' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF')
                arabic++;
            else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                latin++;
        }

        return arabic + latin == 0 || arabic >= 0.6 * (arabic + latin);
    }

    public static HashSet<double> AllowedNumbers(IEnumerable<Fact> facts)
    {
        var set = new HashSet<double> { 100 }; // "out of 100" scales are always allowed
        foreach (var f in facts)
        {
            if (f.Number is { } n)
            {
                set.Add(n);
                set.Add(Math.Round(n));
                set.Add(Math.Round(n, 1));
                set.Add(Math.Round(n, 2));
                if (f.Key == "minute")
                {
                    set.Add(n - 1);
                    set.Add(n + 1);
                }
            }

            if (f.Text is { } t)
                foreach (Match m in NumberPattern().Matches(NormalizeDigits(t)))
                    if (double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        set.Add(v);
        }

        return set;
    }

    /// <summary>Converts Arabic-Indic and Extended Arabic-Indic (Urdu) digits to ASCII so numbers can be checked in any language.</summary>
    public static string NormalizeDigits(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is >= '٠' and <= '٩') sb.Append((char)('0' + (c - '٠')));
            else if (c is >= '۰' and <= '۹') sb.Append((char)('0' + (c - '۰')));
            else if (c == '٫') sb.Append('.');
            else sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text, $@"(?<![\p{{L}}]){Regex.Escape(word)}(?![\p{{L}}])", RegexOptions.IgnoreCase);
}
