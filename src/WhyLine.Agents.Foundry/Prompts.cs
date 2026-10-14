using WhyLine.Agents.Abstractions;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Foundry;

/// <summary>
/// Thin accessors over <see cref="AgentCatalog"/>, the single source of truth for every agent's role, boundaries,
/// output contract, grounding and safety rules.
/// </summary>
internal static class Prompts
{
    public static string Analyst => AgentCatalog.Analyst.Instructions;
    public static string Narrator => AgentCatalog.Narrator.Instructions;
    public static string Localizer => AgentCatalog.Localizer.Instructions;
    public static string Recap => AgentCatalog.Recap.Instructions;

    public static string PersonaGuide(Persona persona) => AgentCatalog.PersonaGuide(persona);
    public static string CommentaryStyle(string language) => AgentCatalog.CommentaryStyle(language);
}
