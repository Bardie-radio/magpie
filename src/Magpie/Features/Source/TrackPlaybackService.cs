using System.Diagnostics;
using Bardie.Module.Source;
using Bardie.Source.V1;
using Magpie.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace Magpie.Features.Source;

/// <summary>Owns track-job lifecycle: resolve → cache → decode → FIFO write.</summary>
public sealed class TrackPlaybackService
{
    private readonly ITrackJobRegistry _jobs;
    private readonly IYouTubeCatalog _youtube;
    private readonly IModuleBlobStorageClient _blobs;
    private readonly IModuleLibraryClient _library;
    private readonly IPcmTranscoder _transcoder;
    private readonly SinePcmGenerator _sine;
    private readonly MagpieOptions _options;
    private readonly ILogger<TrackPlaybackService> _logger;

    public TrackPlaybackService(
        ITrackJobRegistry jobs,
        IYouTubeCatalog youtube,
        IModuleBlobStorageClient blobs,
        IModuleLibraryClient library,
        IPcmTranscoder transcoder,
        SinePcmGenerator sine,
        IOptions<MagpieOptions> options,
        ILogger<TrackPlaybackService> logger)
    {
        _jobs = jobs;
        _youtube = youtube;
        _blobs = blobs;
        _library = library;
        _transcoder = transcoder;
        _sine = sine;
        _options = options.Value;
        _logger = logger;
    }

    public TrackJob Start(string strunaId, string trackRef, string audioEndpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strunaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioEndpoint);

        var active = _jobs.List().Count(j =>
            j.State is TrackState.Running or TrackState.Paused);
        if (active >= Math.Max(1, _options.MaxParallelJobs))
        {
            throw new InvalidOperationException(
                $"Parallel track-job limit reached ({_options.MaxParallelJobs}).");
        }

        var job = _jobs.Create(strunaId, trackRef, audioEndpoint);
        _ = Task.Run(() => RunJobAsync(job), CancellationToken.None);
        return job;
    }

    public bool TryStop(string trackJobId)
    {
        if (!_jobs.TryGet(trackJobId, out var job) || job is null)
        {
            return false;
        }

        job.Cancellation.Cancel();
        return true;
    }

    public bool TryPause(string trackJobId)
    {
        if (!_jobs.TryGet(trackJobId, out var job) || job is null)
        {
            return false;
        }

        job.IsPaused = true;
        job.State = TrackState.Paused;
        return true;
    }

    public bool TryResume(string trackJobId)
    {
        if (!_jobs.TryGet(trackJobId, out var job) || job is null)
        {
            return false;
        }

        job.IsPaused = false;
        if (job.State == TrackState.Paused)
        {
            job.State = TrackState.Running;
        }

        return true;
    }

    private async Task RunJobAsync(TrackJob job)
    {
        var activity = Activity.Current;
        activity?.SetTag("struna.id", job.StrunaId);
        activity?.SetTag("source.module", "magpie");

        try
        {
            var resolved = await _youtube.ResolveAsync(job.TrackRef, job.Cancellation.Token)
                .ConfigureAwait(false);
            if (resolved is null)
            {
                Fail(job, $"Unknown track_ref '{job.TrackRef}'.");
                return;
            }

            job.Title = resolved.Title;
            job.Artist = resolved.Artist;
            job.State = TrackState.Running;

            await using var pcm = await OpenPcmAsync(resolved, job.Cancellation.Token)
                .ConfigureAwait(false);
            await WritePcmWithPauseAsync(job, pcm, job.Cancellation.Token).ConfigureAwait(false);

            if (!job.Cancellation.IsCancellationRequested && job.State != TrackState.Error)
            {
                job.State = TrackState.Ended;
            }
        }
        catch (OperationCanceledException)
        {
            job.State = TrackState.Ended;
        }
        catch (IOException ex) when (IsBrokenPipe(ex))
        {
            // Reader closed the FIFO (normal for short smoke probes).
            _logger.LogInformation(ex, "Track job {JobId} writer stopped (FIFO reader gone)", job.TrackJobId);
            job.State = TrackState.Ended;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Track job {JobId} failed", job.TrackJobId);
            Fail(job, ex.Message);
        }
        finally
        {
            _jobs.TryRemove(job.TrackJobId, out _);
            job.Cancellation.Dispose();
        }
    }

    private async Task<Stream> OpenPcmAsync(ResolvedMedia resolved, CancellationToken cancellationToken)
    {
        if (resolved.IsSine)
        {
            return _sine.CreateStream(cancellationToken);
        }

        var cacheKeyHint = $"tunes/magpie/{resolved.ExternalId}";
        if (await _blobs.ExistsAsync(cacheKeyHint, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Cache hit for {ExternalId}", resolved.ExternalId);
            await using var cached = await _blobs.GetAsync(cacheKeyHint, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Blob '{cacheKeyHint}' vanished after Exists.");
            var cachedPath = await CopyToTempAsync(cached.Stream, resolved.ExternalId, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                return await _transcoder.TranscodeToPcmAsync(cachedPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                TryDelete(cachedPath);
            }
        }

        _logger.LogInformation("Cache miss for {ExternalId}; downloading", resolved.ExternalId);
        var downloadPath = Path.Combine(Path.GetTempPath(), $"magpie-dl-{resolved.ExternalId}-{Guid.NewGuid():N}");
        try
        {
            await using (var download = new FileStream(
                             downloadPath,
                             FileMode.Create,
                             FileAccess.ReadWrite,
                             FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous))
            {
                await _youtube.DownloadAudioAsync(resolved.ExternalId, download, cancellationToken)
                    .ConfigureAwait(false);
                download.Position = 0;
                var put = await _blobs.PutAsync(
                        download,
                        key: cacheKeyHint,
                        contentType: "application/octet-stream",
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                await _library.EnsureTuneAsync(
                        new EnsureTuneCommand(
                            ExternalId: resolved.ExternalId,
                            Title: resolved.Title,
                            Artist: resolved.Artist,
                            DurationSeconds: resolved.DurationSeconds,
                            ArtworkUrl: resolved.ArtworkUrl,
                            StorageKey: put.Key,
                            ContentType: "application/octet-stream",
                            SizeBytes: put.SizeBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await _transcoder.TranscodeToPcmAsync(downloadPath, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDelete(downloadPath);
        }
    }

    private async Task WritePcmWithPauseAsync(TrackJob job, Stream pcm, CancellationToken cancellationToken)
    {
        const int bufferSize = 16 * 1024;
        var buffer = new byte[bufferSize];

        await using var fifo = new FileStream(
            job.AudioEndpoint,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (job.IsPaused)
            {
                job.State = TrackState.Paused;
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            if (job.State == TrackState.Paused)
            {
                job.State = TrackState.Running;
            }

            var read = await pcm.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await fifo.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await fifo.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> CopyToTempAsync(
        Stream source,
        string externalId,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"magpie-cache-{externalId}-{Guid.NewGuid():N}");
        await using var file = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous);
        await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static void Fail(TrackJob job, string message)
    {
        job.State = TrackState.Error;
        job.ErrorMessage = message;
    }

    private static bool IsBrokenPipe(IOException ex) =>
        ex.Message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase)
        || ex.InnerException?.Message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase) == true;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort
        }
    }
}
