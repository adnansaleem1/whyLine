using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Pipeline;
using WhyLine.Core.Domain;
using WhyLine.Core.Simulation;

namespace WhyLine.Web.Live;

public sealed record ControlRequest(string Action, double? Speed, int? Seed, string? Scenario = null);

public sealed record CatchMeUpRequest(ViewerProfile Profile, double PlaybackSeconds);

/// <summary>REST surface for rendering partners, dashboards and tests. See docs/api.md.</summary>
public static class ApiEndpoints
{
    public static void MapWhyLineApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/match", (LiveMatch live, INewsroom newsroom, Speech.SpeechService speech) =>
            new { live.Metadata, Status = live.Status(), Snapshot = live.LatestSnapshot(), live.AgentMode, newsroom.Orchestration, Speech = speech.Status });

        api.MapGet("/matches", (LiveMatch live) => MatchCatalog.All.Select(s => new
        {
            s.Id, s.Title, s.Story, Current = s.Id == live.Scenario.Id // no result: the picker must not spoil the match
        }));

        api.MapGet("/match/events", (LiveMatch live, double? since) => live.EventsSince(since ?? 0));

        api.MapGet("/match/moments", (LiveMatch live) => live.MomentsUpTo(live.Clock));

        api.MapGet("/cues", (LiveMatch live, int? since, string? profileKey) =>
            profileKey is null ? live.AllCues(since ?? 0) : live.CuesFor(profileKey));

        api.MapGet("/traces", (LiveMatch live, int? count) => live.RecentTraces(count ?? 100));

        api.MapGet("/autoeventing", (LiveMatch live) => live.AutoEventing());

        // Transparency: every agent's role, responsibilities, boundaries, contract and system message.
        api.MapGet("/agents", () => AgentCatalog.All.Select(a => new
        {
            a.Name, a.DisplayName, a.Description, Kind = a.Kind.ToString(), a.ModelTier, a.Role, a.Responsibilities,
            a.AudienceAndTone, a.OutOfScope, a.Inputs, a.OutputContract, a.ReceivesFrom, a.HandsOffTo, a.WhenUnsure,
            a.DataAccess, a.WhySeparateAgent, a.FailureBehaviour, a.Instructions
        }));

        api.MapPost("/control", (LiveMatch live, ControlRequest request) =>
        {
            switch (request.Action.ToLowerInvariant())
            {
                case "play": live.SetRunning(true); break;
                case "pause": live.SetRunning(false); break;
                case "restart": live.Reset(request.Seed ?? live.Seed, request.Scenario); live.SetRunning(true); break;
                case "load":
                    if (MatchCatalog.All.All(s => !string.Equals(s.Id, request.Scenario, StringComparison.OrdinalIgnoreCase)))
                        return Results.BadRequest($"Unknown scenario '{request.Scenario}'. See GET /api/matches.");
                    live.Reset(request.Seed ?? live.Seed, request.Scenario);
                    live.SetRunning(true);
                    break;
                default: return Results.BadRequest($"Unknown action '{request.Action}'. Use play, pause, restart or load.");
            }

            if (request.Speed is { } s) live.SetSpeed(s);
            return Results.Ok(live.Status());
        });

        api.MapPost("/catchmeup", async (LiveMatch live, INewsroom pipeline, CatchMeUpRequest request, CancellationToken ct) =>
        {
            var t = Math.Min(request.PlaybackSeconds, live.Clock);
            var recap = new RecapRequest(RecapKind.CatchMeUp, request.Profile, live.Metadata, live.SnapshotAt(t), live.MomentsUpTo(t));
            return await pipeline.BuildRecapAsync(recap, null, ct);
        });
    }
}
