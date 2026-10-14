using System.Security;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Speech;

/// <summary>An Azure AI Speech neural voice and the speaking rate used for it.</summary>
public sealed record SpeechVoice(string Locale, string Name, string Rate);

/// <summary>
/// Chooses an Azure AI Speech neural voice per language and persona, and builds the SSML for a card. Commentary voices
/// match each language's commentary style (British TV, Pakistani TV, Latin American radio, Arabic commentary); the Audio
/// Description persona uses a calmer, slightly slower voice, because it is listened to rather than glanced at.
/// </summary>
public static class SpeechVoices
{
    private static readonly Dictionary<string, (SpeechVoice Commentary, SpeechVoice Describer)> ByLanguage = new()
    {
        ["en"] = (new("en-GB", "en-GB-RyanNeural", "+8%"), new("en-GB", "en-GB-SoniaNeural", "-5%")),
        ["ur"] = (new("ur-PK", "ur-PK-AsadNeural", "+5%"), new("ur-PK", "ur-PK-UzmaNeural", "-5%")),
        ["es"] = (new("es-MX", "es-MX-JorgeNeural", "+8%"), new("es-MX", "es-MX-DaliaNeural", "-5%")),
        ["ar"] = (new("ar-SA", "ar-SA-HamedNeural", "+5%"), new("ar-SA", "ar-SA-ZariyahNeural", "-5%"))
    };

    public static SpeechVoice For(string language, Persona persona)
    {
        var pair = ByLanguage.GetValueOrDefault(language, ByLanguage["en"]);
        return persona == Persona.AudioDescription ? pair.Describer : pair.Commentary;
    }

    /// <summary>SSML for one card. The text is XML-escaped, so card content can never inject SSML.</summary>
    public static string Ssml(string text, SpeechVoice voice) =>
        $"<speak version='1.0' xml:lang='{voice.Locale}' xmlns='http://www.w3.org/2001/10/synthesis'>" +
        $"<voice name='{voice.Name}'><prosody rate='{voice.Rate}'>{SecurityElement.Escape(Clean(text))}</prosody></voice></speak>";

    /// <summary>The persona from a cue's profile key ("Persona|language|club|player").</summary>
    public static Persona PersonaOf(string? profileKey) =>
        profileKey?.Split('|')[0] is { } p && Enum.TryParse<Persona>(p, out var persona) ? persona : Persona.Casual;

    private static string Clean(string text) => text.Replace("xG", "x G", StringComparison.Ordinal).Replace("  ", " ").Trim();
}
