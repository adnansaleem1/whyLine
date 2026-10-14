using System.Text.RegularExpressions;
using WhyLine.Agents.Abstractions;

namespace WhyLine.Tests;

/// <summary>Every agent must have a complete definition: role, responsibilities, boundaries, contract, and safety.</summary>
public sealed class AgentCatalogTests
{
    public static IEnumerable<object[]> Agents => AgentCatalog.All.Select(a => new object[] { a.Name });

    [Theory]
    [MemberData(nameof(Agents))]
    public void Every_agent_has_a_complete_definition(string name)
    {
        var a = AgentCatalog.ByName(name);
        Assert.Matches(new Regex("^whyline-[a-z]+$"), a.Name);
        Assert.False(string.IsNullOrWhiteSpace(a.Description));
        Assert.False(string.IsNullOrWhiteSpace(a.Role));
        Assert.NotEmpty(a.Responsibilities);
        Assert.NotEmpty(a.OutOfScope);
        Assert.NotEmpty(a.WhenUnsure);
        Assert.NotEmpty(a.DataAccess);
        Assert.False(string.IsNullOrWhiteSpace(a.OutputContract));
        Assert.False(string.IsNullOrWhiteSpace(a.WhySeparateAgent));
        Assert.False(string.IsNullOrWhiteSpace(a.FailureBehaviour));
        Assert.NotEmpty(a.ReceivesFrom);
        Assert.NotEmpty(a.HandsOffTo);
    }

    [Fact]
    public void Agent_names_are_unique_and_hand_offs_point_to_real_agents()
    {
        Assert.Equal(AgentCatalog.All.Count, AgentCatalog.All.Select(a => a.Name).Distinct().Count());
        var names = AgentCatalog.All.Select(a => a.Name).ToHashSet();
        foreach (var a in AgentCatalog.All)
            Assert.All(a.HandsOffTo.Concat(a.ReceivesFrom).Where(n => n.StartsWith("whyline-")), n => Assert.Contains(n, names));
    }

    [Theory]
    [MemberData(nameof(Agents))]
    public void Model_agents_get_a_system_message_in_microsofts_recommended_structure(string name)
    {
        var a = AgentCatalog.ByName(name);
        if (a.Kind == AgentKind.Deterministic)
        {
            Assert.Equal("", a.Instructions);
            return;
        }

        Assert.NotNull(a.ModelTier);
        var sections = new[] { "# Role and task", "# Audience and tone", "# Scope and boundaries", "# Output format", "# When unsure", "# Grounding", "# Safety" };
        var last = -1;
        foreach (var section in sections)
        {
            var at = a.Instructions.IndexOf(section, StringComparison.Ordinal);
            Assert.True(at > last, $"{name}: '{section}' is missing or out of order.");
            last = at;
        }

        Assert.Contains("not instructions", a.Instructions);     // prompt-injection guard
        Assert.Contains("only source of facts", a.Instructions); // grounding
    }

    [Fact]
    public void Writing_and_approving_are_separate_agents()
    {
        var verifier = AgentCatalog.Verifier;
        Assert.Equal(AgentKind.Deterministic, verifier.Kind);
        Assert.Contains(AgentCatalog.NarratorName, verifier.ReceivesFrom);
        Assert.Contains(AgentCatalog.VerifierName, AgentCatalog.Narrator.HandsOffTo);
        Assert.Contains(AgentCatalog.VerifierName, AgentCatalog.Localizer.HandsOffTo);
        Assert.Contains(AgentCatalog.VerifierName, AgentCatalog.Recap.HandsOffTo);
    }
}
