using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Pipeline;
using WhyLine.Core.Domain;
using WhyLine.Core.Simulation;

namespace WhyLine.Tests;

/// <summary>Verifier regression tests: deliberately false claims must never get through.</summary>
public sealed class VerifierTests
{
    private static readonly MatchMetadata Meta = SquadFactory.CreateDefaultMatch(7);
    private static readonly Player Scorer = Meta.Players.First(p => p.Team == TeamSide.Home && p.Position == "ST");
    private static readonly Player Bystander = Meta.Players.First(p => p.Team == TeamSide.Away && p.Position == "CB");

    private static readonly Fact[] Facts =
    [
        new("scorer", "Scorer", Text: Scorer.Name),
        new("minute", "Minute", 23),
        new("score", "Score", Text: "1-0"),
        new("xg", "xG", 0.38, Unit: "xG"),
        new("shot_speed", "Shot speed", 96, Unit: "km/h")
    ];

    private readonly ClaimVerifier _verifier = new();

    private VerificationResult Check(params string[] sentences) =>
        _verifier.Verify(new NarrativeDraft("Goal", sentences, "test"), Facts, Meta, [Scorer.Id]);

    [Fact]
    public void Accepts_numbers_that_come_from_facts() =>
        Assert.True(Check($"{Scorer.Name} scores in minute 23 to make it 1-0.", "A 0.38 xG chance struck at 96 km/h.").Passed);

    [Theory]
    [InlineData("It was his 14th goal of the season.")]
    [InlineData("Struck at 120 km/h.")]
    [InlineData("That makes it 3-0.")]
    public void Rejects_invented_numbers(string sentence) => Assert.False(Check(sentence).Passed);

    [Fact]
    public void Rejects_players_who_are_not_in_the_evidence()
    {
        var result = Check($"{Bystander.Name} made the key block earlier.");
        Assert.False(result.Passed);
        Assert.Contains(result.Issues, i => i.Contains(Bystander.Name));
    }

    [Fact]
    public void Checks_numbers_written_in_urdu_and_arabic_digits()
    {
        Assert.True(Check("اسکور اب ۱-۰ ہے۔").Passed);   // Urdu digits for 1-0
        Assert.False(Check("اسکور اب ۳-۰ ہے۔").Passed);  // 3-0 is not a fact
        Assert.True(Check("النتيجة ١-٠").Passed);        // Arabic-Indic digits
    }

    [Fact]
    public void Rejects_empty_narratives() =>
        Assert.False(_verifier.Verify(new NarrativeDraft("", [], "test"), Facts, Meta, [Scorer.Id]).Passed);

    [Fact]
    public void Urdu_cards_must_be_in_urdu_script_but_names_may_stay_in_latin_letters()
    {
        var urdu = new NarrativeDraft($"{Meta.Home.Name} آگے", [$"{Scorer.Name} نے گول کر کے اسکور ۱-۰ کر دیا۔"], "test");
        var roman = new NarrativeDraft($"{Meta.Home.Name} aage", [$"{Scorer.Name} ne goal kar ke score 1-0 kar diya."], "test");

        Assert.True(_verifier.Verify(urdu, Facts, Meta, [Scorer.Id], "ur").Passed);
        var rejected = _verifier.Verify(roman, Facts, Meta, [Scorer.Id], "ur");
        Assert.False(rejected.Passed);
        Assert.Contains(rejected.Issues, i => i.Contains("Urdu script"));
        Assert.True(_verifier.Verify(roman, Facts, Meta, [Scorer.Id]).Passed); // no language given: script is not checked
    }
}
