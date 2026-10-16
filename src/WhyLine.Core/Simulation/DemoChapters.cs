using WhyLine.Core.Domain;

namespace WhyLine.Core.Simulation;

/// <summary>A point the Studio can jump to in demo mode: just before a scripted beat, so the run-up is on screen.</summary>
public sealed record DemoChapter(string Label, double MatchSeconds);

/// <summary>
/// Demo mode chapters for a fixture, built from its storyline: every goal, big chance and tactical switch, plus
/// kick-off and the full-time recap. Labels name the beat (they are for the presenter, so they may spoil the result).
/// </summary>
public static class DemoChapters
{
    /// <summary>How far before a beat a chapter starts.</summary>
    public const double LeadSeconds = 30;

    /// <summary>The full-time chapter: the last minute, so the final whistle and the full-time recap follow.</summary>
    public const double FullTimeSeconds = 89 * 60;

    public static IReadOnlyList<DemoChapter> For(MatchScript script)
    {
        var chapters = new List<DemoChapter> { new("Kick-off", 0) };
        foreach (var beat in script.Beats.OrderBy(b => b.AtMinute))
        {
            var what = beat.Kind switch
            {
                BeatKind.ProfileChange => "Tactical switch",
                _ when beat.Scores => "Goal",
                _ => "Big chance"
            };
            var team = script.Metadata.TeamOf(beat.Team).ShortName;
            chapters.Add(new($"{beat.AtMinute:0}' {what} · {team}", Math.Max(0, beat.AtMinute * 60 - LeadSeconds)));
        }

        chapters.Add(new("90' Full time and recap", FullTimeSeconds));
        return chapters;
    }
}
