using Bardie.Module.Channel.Manifest;
using Bardie.Module.Source;
using Bardie.Source.V1;
using Grpc.Core;
using Magpie.Infrastructure.Media;

namespace Magpie.Features.Source;

/// <summary>SourceModule gRPC façade — commands live in <see cref="TrackPlaybackService"/>.</summary>
public sealed class SourceModuleService : SourceModuleBase
{
    private readonly TrackPlaybackService _playback;
    private readonly IYouTubeCatalog _youtube;
    private readonly ITrackJobRegistry _jobs;
    private readonly ILogger<SourceModuleService> _logger;

    public SourceModuleService(
        ModuleManifest manifest,
        TrackPlaybackService playback,
        IYouTubeCatalog youtube,
        ITrackJobRegistry jobs,
        ILogger<SourceModuleService> logger)
        : base(manifest)
    {
        _playback = playback;
        _youtube = youtube;
        _jobs = jobs;
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
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override Task<StopTrackResponse> StopTrack(StopTrackRequest request, ServerCallContext context)
    {
        if (!_playback.TryStop(request.TrackJobId))
        {
            throw JobNotFound(request.TrackJobId);
        }

        return Task.FromResult(new StopTrackResponse { Ok = true });
    }

    protected override Task<PauseTrackResponse> PauseTrackCoreAsync(
        PauseTrackRequest request,
        ServerCallContext context)
    {
        if (!_playback.TryPause(request.TrackJobId))
        {
            throw JobNotFound(request.TrackJobId);
        }

        return Task.FromResult(new PauseTrackResponse { Ok = true });
    }

    protected override Task<ResumeTrackResponse> ResumeTrackCoreAsync(
        ResumeTrackRequest request,
        ServerCallContext context)
    {
        if (!_playback.TryResume(request.TrackJobId))
        {
            throw JobNotFound(request.TrackJobId);
        }

        return Task.FromResult(new ResumeTrackResponse { Ok = true });
    }

    public override async Task TrackStatus(
        TrackStatusRequest request,
        IServerStreamWriter<TrackStatusEvent> responseStream,
        ServerCallContext context)
    {
        if (!_jobs.TryGet(request.TrackJobId, out var job) || job is null)
        {
            // Job may have already ended — emit a terminal event if we can, else NotFound.
            throw JobNotFound(request.TrackJobId);
        }

        TrackState? last = null;
        while (!context.CancellationToken.IsCancellationRequested)
        {
            if (!_jobs.TryGet(request.TrackJobId, out job) || job is null)
            {
                if (last is not null and not TrackState.Ended and not TrackState.Error)
                {
                    await responseStream.WriteAsync(
                            new TrackStatusEvent
                            {
                                TrackJobId = request.TrackJobId,
                                State = TrackState.Ended,
                            },
                            context.CancellationToken)
                        .ConfigureAwait(false);
                }

                break;
            }

            if (last != job.State)
            {
                last = job.State;
                await responseStream.WriteAsync(
                        new TrackStatusEvent
                        {
                            TrackJobId = job.TrackJobId,
                            State = job.State,
                            Title = job.Title ?? string.Empty,
                            Artist = job.Artist ?? string.Empty,
                            ErrorMessage = job.ErrorMessage ?? string.Empty,
                        },
                        context.CancellationToken)
                    .ConfigureAwait(false);

                if (job.State is TrackState.Ended or TrackState.Error)
                {
                    break;
                }
            }

            await Task.Delay(200, context.CancellationToken).ConfigureAwait(false);
        }
    }
}
