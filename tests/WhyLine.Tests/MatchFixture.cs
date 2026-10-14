using WhyLine.Core.Domain;
using WhyLine.Core.Engine;
using WhyLine.Core.Simulation;

namespace WhyLine.Tests;

/// <summary>Plays the default scripted match once and shares the results across tests.</summary>
public sealed class MatchFixture
{
    public MatchFixture()
    {
        Script = MatchScript.Default(7);
        var sim = new MatchSimulator(Script);
        Analyzer = new MatchAnalyzer(Script.Metadata);
        Detector = new TrackingEventDetector(Script.Metadata);

        while (!sim.Finished)
        {
            var step = sim.Step();
            Analyzer.IngestFrame(step.Frame);
            Detector.IngestFrame(step.Frame);
            foreach (var e in step.Events)
            {
                Events.Add(e);
                Detector.IngestTruth(e);
                Moments.AddRange(Analyzer.Ingest(e));
            }

            Moments.AddRange(Analyzer.Tick(step.Frame.MatchSeconds));
        }

        HomeScore = sim.HomeScore;
        AwayScore = sim.AwayScore;
        Final = Analyzer.Snapshot(sim.Clock);
    }

    public MatchScript Script { get; }
    public MatchAnalyzer Analyzer { get; }
    public TrackingEventDetector Detector { get; }
    public List<MatchEvent> Events { get; } = [];
    public List<Moment> Moments { get; } = [];
    public int HomeScore { get; }
    public int AwayScore { get; }
    public MatchSnapshot Final { get; }
}

[CollectionDefinition("match")]
public sealed class MatchCollection : ICollectionFixture<MatchFixture>;
