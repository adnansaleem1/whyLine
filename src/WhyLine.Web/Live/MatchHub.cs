using Microsoft.AspNetCore.SignalR;
using WhyLine.Core.Domain;

namespace WhyLine.Web.Live;

/// <summary>
/// Real-time feed for rendering partners and external clients.
/// Server → client: "status" (clock/score), "cue" (<see cref="OverlayCue"/>), "trace" (agent trace).
/// Clients must hold each cue until their own playback clock reaches cue.matchSeconds (spoiler-safe sync).
/// </summary>
public sealed class MatchHub(ViewerRegistry viewers) : Hub
{
    /// <summary>Register what this client wants; the newsroom will write narratives for this profile.</summary>
    public void RegisterProfile(ViewerProfile profile) => viewers.Upsert(Context.ConnectionId, profile);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        viewers.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
