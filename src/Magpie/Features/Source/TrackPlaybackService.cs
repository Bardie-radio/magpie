using System.Diagnostics;
using Bardie.Logos.Channel.Manifest;
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
        // META-OTEL-002: capture RPC Activity before Task.Run; StartTrack span ends when RPC returns.
        var linkContext = MagpieTrackActivity.CaptureLinkContext();
        _ = Task.Run(() => RunJobAsync(job, linkContext), CancellationToken.None);
        return job;
    }

    /// <summary>Resolve + cache blob without opening the session FIFO (queue warmup).</summary>
    public async Task<(string ExternalId, bool FromCache)> PrefetchAsync(
        string trackRef,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackRef);

        var resolved = await _youtube.ResolveAsync(trackRef, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown track_ref '{trackRef}'.");

#if DEBUG
        if (string.Equals(resolved.ExternalId, DevProofTrack.ExternalId, StringComparison.OrdinalIgnoreCase))
        {
            return (resolved.ExternalId, FromCache: true);
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

        _logger.LogInformation(
            "Prefetch {ExternalId} complete (fromCache={FromCache})",
            resolved.ExternalId,
            cached.FromCache);
        return (resolved.ExternalId, cached.FromCache);
    }

    private async Task RunJobAsync(TrackJob job, ActivityContext linkContext)
    {
        using var activity = MagpieTrackActivity.StartLinked("magpie.track.job", linkContext);
        SourceModuleRpc.TagTrackJob(activity, job, _manifest.Slug);

        try
        {
            ResolvedMedia? resolved;
            using (var resolve = MagpieTrackActivity.Source.StartActivity("magpie.track.resolve"))
            {
                resolve?.SetTag("source.module", _manifest.Slug);
                resolve?.SetTag("source.track_job.id", job.TrackJobId);
                resolve?.SetTag("track.ref", job.TrackRef);
                resolved = await _youtube.ResolveAsync(job.TrackRef, job.Cancellation.Token)
                    .ConfigureAwait(false);
            }

            if (resolved is null)
            {
                job.MarkFailed($"Unknown track_ref '{job.TrackRef}'.");
                return;
            }

            job.Title = resolved.Title;
            job.Artist = resolved.Artist;
            // Stay Preparing through download/transcode so Neck keeps silence on.

            await using var pcm = await OpenPcmAsync(resolved, job).ConfigureAwait(false);

            job.MarkRunning();
            using (var fifo = MagpieTrackActivity.Source.StartActivity("magpie.track.fifo"))
            {
                fifo?.SetTag("source.module", _manifest.Slug);
                fifo?.SetTag("source.track_job.id", job.TrackJobId);
                fifo?.SetTag("struna.id", job.StrunaId);
                await _fifo.WriteAsync(
                        job.AudioEndpoint,
                        pcm,
                        job.Cancellation.Token,
                        isPaused: () => job.State == TrackState.Paused)
                    .ConfigureAwait(false);
            }

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

    private async Task<Stream> OpenPcmAsync(ResolvedMedia resolved, TrackJob job)
    {
        var cancellationToken = job.Cancellation.Token;
#if DEBUG
        // Bardie.Module.Source.Debug — StartTrack → FIFO without YouTube/FFmpeg.
        if (string.Equals(resolved.ExternalId, DevProofTrack.ExternalId, StringComparison.OrdinalIgnoreCase))
        {
            return _sine.CreateStream(cancellationToken);
        }
#endif

        using (var cacheSpan = MagpieTrackActivity.Source.StartActivity("magpie.track.cache"))
        {
            cacheSpan?.SetTag("source.module", _manifest.Slug);
            cacheSpan?.SetTag("source.track_job.id", job.TrackJobId);
            cacheSpan?.SetTag("track.external_id", resolved.ExternalId);
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
            cacheSpan?.SetTag("cache.hit", cached.FromCache);

            using var transcode = MagpieTrackActivity.Source.StartActivity("magpie.track.transcode");
            transcode?.SetTag("source.module", _manifest.Slug);
            transcode?.SetTag("source.track_job.id", job.TrackJobId);
            return await _transcoder.TranscodeToPcmAsync(cached.Path, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
