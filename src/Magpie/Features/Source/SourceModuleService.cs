using Bardie.Logos.Channel.Manifest;
using Bardie.Module.Source;
using Bardie.Source.V1;
using Grpc.Core;
using Magpie.Infrastructure.Media;

namespace Magpie.Features.Source;

/// <summary>SourceModule gRPC façade — playback lives in <see cref="TrackPlaybackService"/>.</summary>
public sealed class SourceModuleService : SourceModuleBase
{
    private readonly TrackPlaybackService _playback;
    private readonly IYouTubeCatalog _youtube;
    private readonly ILogger<SourceModuleService> _logger;

    public SourceModuleService(
        ModuleManifest manifest,
        TrackPlaybackService playback,
        IYouTubeCatalog youtube,
        ITrackJobRegistry jobs,
        ILogger<SourceModuleService> logger)
        : base(manifest, jobs)
    {
        _playback = playback;
        _youtube = youtube;
        _logger = logger;
    }

    public override async Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        request.Fields.TryGetValue("title", out var title);
        request.Fields.TryGetValue("artist", out var artist);
        request.Fields.TryGetValue("owner", out var owner);

        var hits = await _youtube.SearchAsync(
                title,
                artist,
                owner,
                request.Limit,
                context.CancellationToken)
            .ConfigureAwait(false);

        var response = new SearchResponse();
        foreach (var hit in hits)
        {
            var result = new SearchResult
            {
                TrackRef = hit.TrackRef,
                Title = hit.Title ?? string.Empty,
                Artist = hit.Artist ?? string.Empty,
                ExternalId = hit.ExternalId ?? string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(hit.Owner))
            {
                result.Metadata["owner"] = hit.Owner;
            }

            if (hit.DurationSeconds is { } duration)
            {
                result.Metadata["duration_seconds"] = duration.ToString("0.###");
            }

            if (!string.IsNullOrWhiteSpace(hit.ArtworkUrl))
            {
                result.Metadata["artwork_url"] = hit.ArtworkUrl;
            }

            response.Results.Add(result);
        }

        return response;
    }

    public override Task<StartTrackResponse> StartTrack(StartTrackRequest request, ServerCallContext context)
    {
        try
        {
            var job = _playback.Start(request.StrunaId, request.TrackRef, request.AudioEndpoint);
            _logger.LogInformation(
                "Started track job {JobId} for struna {StrunaId} ref {TrackRef}",
                job.TrackJobId,
                request.StrunaId,
                request.TrackRef);
            return Task.FromResult(new StartTrackResponse { TrackJobId = job.TrackJobId });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw SourceModuleRpc.MapStartFailure(ex);
        }
    }

    protected override async Task<PrefetchTrackResponse> PrefetchTrackCoreAsync(
        PrefetchTrackRequest request,
        ServerCallContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TrackRef);
        try
        {
            var (externalId, fromCache) = await _playback
                .PrefetchAsync(request.TrackRef, context.CancellationToken)
                .ConfigureAwait(false);
            return new PrefetchTrackResponse
            {
                Ok = true,
                ExternalId = externalId,
                FromCache = fromCache,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw SourceModuleRpc.MapStartFailure(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Prefetch failed for ref {TrackRef}", request.TrackRef);
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }
}
