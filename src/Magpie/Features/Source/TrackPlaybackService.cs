using System.Diagnostics;
using Bardie.Module.Channel.Manifest;
using Bardie.Module.Source;
using Bardie.Source.V1;
using Magpie.Infrastructure.Media;
#if DEBUG
using Bardie.Module.Source.Debug;
#endif

namespace Magpie.Features.Source;

/// <summary>Owns track-job lifecycle: resolve → cache → decode → FIFO write.</summary>
public sealed class TrackPlaybackService
{
    private readonly ITrackJobRegistry _jobs;
    private readonly IYouTubeCatalog _youtube;
    private readonly ModuleTuneCache _cache;
    private readonly IPcmTranscoder _transcoder;
    private readonly IFifoAudioSink _fifo;
#if DEBUG
    private readonly SinePcmGenerator _sine;
#endif
    private readonly ModuleManifest _manifest;
    private readonly ILogger<TrackPlaybackService> _logger;

    public TrackPlaybackService(
        ITrackJobRegistry jobs,
        IYouTubeCatalog youtube,
        ModuleTuneCache cache,
        IPcmTranscoder transcoder,
        IFifoAudioSink fifo,
#if DEBUG
        SinePcmGenerator sine,
#endif
        ModuleManifest manifest,
        ILogger<TrackPlaybackService> logger)
    {
        _jobs = jobs;
        _youtube = youtube;
        _cache = cache;
        _transcoder = transcoder;
        _fifo = fifo;
#if DEBUG
        _sine = sine;
#endif
        _manifest = manifest;
        _logger = logger;
    }

    public TrackJob Start(string strunaId, string trackRef, string audioEndpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strunaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioEndpoint);

        var job = _jobs.Create(strunaId, trackRef, audioEndpoint);
        _ = Task.Run(() => RunJobAsync(job), CancellationToken.None);
        return job;
    }

    private async Task RunJobAsync(TrackJob job)
    {
        SourceModuleRpc.TagTrackJob(Activity.Current, job, _manifest.Slug);

        try
        {
            var resolved = await _youtube.ResolveAsync(job.TrackRef, job.Cancellation.Token)
                .ConfigureAwait(false);
            if (resolved is null)
            {
                job.MarkFailed($"Unknown track_ref '{job.TrackRef}'.");
                return;
            }

            job.Title = resolved.Title;
            job.Artist = resolved.Artist;
            job.MarkRunning();

            await using var pcm = await OpenPcmAsync(resolved, job.Cancellation.Token)
                .ConfigureAwait(false);
            await _fifo.WriteAsync(
                    job.AudioEndpoint,
                    pcm,
                    job.Cancellation.Token,
                    isPaused: () => job.State == TrackState.Paused)
                .ConfigureAwait(false);

            if (!job.Cancellation.IsCancellationRequested && job.State != TrackState.Error)
            {
                job.MarkEnded();
            }
        }
        catch (OperationCanceledException)
        {
            job.MarkEnded();
        }
        catch (IOException ex) when (SourceModuleRpc.IsBrokenPipe(ex))
        {
            _logger.LogInformation(ex, "Track job {JobId} writer stopped (FIFO reader gone)", job.TrackJobId);
            job.MarkEnded();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Track job {JobId} failed", job.TrackJobId);
            job.MarkFailed(ex.Message);
        }
        finally
        {
            _jobs.TryRemove(job.TrackJobId, out _);
            job.Cancellation.Dispose();
        }
    }

    private async Task<Stream> OpenPcmAsync(ResolvedMedia resolved, CancellationToken cancellationToken)
    {
#if DEBUG
        // Bardie.Module.Source.Debug — StartTrack → FIFO without YouTube/FFmpeg.
        if (string.Equals(resolved.ExternalId, DevProofTrack.ExternalId, StringComparison.OrdinalIgnoreCase))
        {
            return _sine.CreateStream(cancellationToken);
        }
#endif

        await using var cached = await _cache.OpenOrFetchAsync(
                new EnsureTuneCommand(
                    ExternalId: resolved.ExternalId,
                    Title: resolved.Title,
                    Artist: resolved.Artist,
                    DurationSeconds: resolved.DurationSeconds,
                    ArtworkUrl: resolved.ArtworkUrl,
                    ContentType: "application/octet-stream"),
                downloadAsync: (stream, ct) => _youtube.DownloadAudioAsync(resolved.ExternalId, stream, ct),
                cancellationToken)
            .ConfigureAwait(false);

        return await _transcoder.TranscodeToPcmAsync(cached.Path, cancellationToken)
            .ConfigureAwait(false);
    }
}
