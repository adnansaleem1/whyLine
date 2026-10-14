using System.Collections.Concurrent;
using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Pipeline;
using WhyLine.Core.Domain;

namespace WhyLine.Web.Live;

/// <summary>Tracks which viewer profiles are connected (Blazor viewers and SignalR clients).</summary>
public sealed class ViewerRegistry : IProfileSource
{
    /// <summary>The main broadcast feed (the rendering partner's overlay) is always on air.</summary>
    public const string BroadcastFeedId = "broadcast";

    private readonly ConcurrentDictionary<string, ViewerProfile> _viewers = new() { [BroadcastFeedId] = ViewerProfile.Default };

    public void Upsert(string viewerId, ViewerProfile profile) => _viewers[viewerId] = profile;
    public void Remove(string viewerId) => _viewers.TryRemove(viewerId, out _);

    public IReadOnlyCollection<ViewerProfile> ActiveProfiles() =>
        _viewers.Values.GroupBy(p => p.Key).Select(g => g.First()).ToArray();
}

/// <summary>
/// Background worker that feeds detected moments through the agent newsroom. Moments run concurrently (up to
/// Agents:MaxConcurrentMoments) and start in detection order, so one slow model call does not hold up the next moment.
/// </summary>
public sealed class NewsroomWorker(LiveMatch live, INewsroom pipeline, DirectorAgent director, AgentOptions options,
    ILogger<NewsroomWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        pipeline.MomentsSoFar = () => live.MomentsUpTo(live.Clock);
        var generation = live.Generation;
        using var slots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentMoments));
        var running = new List<Task>();

        try
        {
            await foreach (var item in live.MomentQueue.ReadAllAsync(stoppingToken))
            {
                if (item.Generation != live.Generation) continue;
                if (generation != live.Generation)
                {
                    generation = live.Generation;
                    director.Reset();
                }

                await slots.WaitAsync(stoppingToken);
                running.RemoveAll(t => t.IsCompleted);
                running.Add(RunAsync(item.Context, slots, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await Task.WhenAll(running);
    }

    private async Task RunAsync(MomentContext context, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            await pipeline.ProcessAsync(context, live.Clock, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Newsroom failed on moment {Moment}", context.Moment.Id);
        }
        finally
        {
            slots.Release();
        }
    }
}
