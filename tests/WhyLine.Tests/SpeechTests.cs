using System.Xml.Linq;
using WhyLine.Agents.Speech;
using WhyLine.Core.Domain;

namespace WhyLine.Tests;

public sealed class SpeechTests
{
    [Theory]
    [InlineData("en", Persona.Analyst, "en-GB-RyanNeural")]
    [InlineData("ur", Persona.Casual, "ur-PK-AsadNeural")]
    [InlineData("es", Persona.Casual, "es-MX-JorgeNeural")]
    [InlineData("ar", Persona.Casual, "ar-SA-HamedNeural")]
    [InlineData("ur", Persona.AudioDescription, "ur-PK-UzmaNeural")]
    [InlineData("fr", Persona.Casual, "en-GB-RyanNeural")] // unsupported language falls back to English
    public void Each_language_and_persona_gets_a_neural_voice(string language, Persona persona, string voice) =>
        Assert.Equal(voice, SpeechVoices.For(language, persona).Name);

    [Fact]
    public void Card_text_cannot_inject_ssml()
    {
        var ssml = SpeechVoices.Ssml("Goal! <break time='9s'/> 1-0 & counting", SpeechVoices.For("en", Persona.Casual));
        var doc = XDocument.Parse(ssml); // well-formed
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "break");
        Assert.Contains("<break time='9s'/>", doc.Root!.Value);
    }

    [Fact]
    public void Urdu_ssml_keeps_the_script_and_uses_the_urdu_locale()
    {
        var ssml = SpeechVoices.Ssml("Saltmere Athletic آگے ہیں۔", SpeechVoices.For("ur", Persona.Casual));
        var doc = XDocument.Parse(ssml);
        Assert.Equal("ur-PK", doc.Root!.Attribute(XNamespace.Xml + "lang")!.Value);
        Assert.Contains("آگے", doc.Root.Value);
    }

    [Theory]
    [InlineData("AudioDescription|ur|Away|-", Persona.AudioDescription)]
    [InlineData("Analyst|en|-|-", Persona.Analyst)]
    [InlineData(null, Persona.Casual)]
    public void Persona_is_read_from_the_profile_key(string? key, Persona expected) =>
        Assert.Equal(expected, SpeechVoices.PersonaOf(key));
}
