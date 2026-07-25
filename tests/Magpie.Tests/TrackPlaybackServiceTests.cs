using System.Diagnostics;
using Bardie.Module.Channel.Manifest;
using Bardie.Module.Source;
using Magpie.Features.Source;
using Magpie.Infrastructure.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
#if DEBUG
using Bardie.Module.Source.Debug;
#endif

namespace Magpie.Tests;

/// <summary>META-QA-001 (Magpie): StartTrack registry + FIFO pacing / cancel.</summary>
public class TrackPlaybackServiceTests
{
    [Fact]
    public async Task Start_cancel_removes_job_after_run_finishes()
    {
        var registry = new TrackJobRegistry(Options.Create(new SourceModuleOptions { MaxParallelJobs = 4 }));
        var youtube = new StubYouTube();
        var fifo = new RecordingFifo();
        var svc = CreateService(registry, youtube, fifo);

        var endpoint = Path.Combine(Path.GetTempPath(), "magpie-job-" + Guid.NewGuid().ToString("N") + ".pcm");
        await File.WriteAllBytesAsync(endpoint, []);
        try
        {
            var job = svc.Start(Guid.NewGuid().ToString("D"), "vid-1", endpoint);
            Assert.True(registry.TryGet(job.TrackJobId, out _));

            Assert.True(registry.TryStop(job.TrackJobId));

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5)
                   && registry.TryGet(job.TrackJobId, out _))
            {
                await Task.Delay(20);
            }

            Assert.False(registry.TryGet(job.TrackJobId, out _));
        }
        finally
        {
            File.Delete(endpoint);
        }
    }

    [Fact]
    public async Task FifoAudioSink_paces_near_realtime()
    {
        // ~100 ms of s16le / 48 kHz / stereo
        const int sampleRate = 48_000;
        const int channels = 2;
        var bytes = sampleRate / 10 * channels * sizeof(short);
        var pcm = new byte[bytes];
        new Random(1).NextBytes(pcm);

        var path = Path.Combine(Path.GetTempPath(), "magpie-pace-" + Guid.NewGuid().ToString("N") + ".pcm");
        await File.WriteAllBytesAsync(path, []);
        try
        {
            var sink = new FifoAudioSink();
            var sw = Stopwatch.StartNew();
            await using var stream = new MemoryStream(pcm);
            await sink.WriteAsync(path, stream);
            sw.Stop();

            // Realtime ≈ 100 ms; allow generous CI slack (not dump-then-drain).
            Assert.True(
                sw.Elapsed >= TimeSpan.FromMilliseconds(40),
                $"Expected pacing delay, elapsed={sw.Elapsed.TotalMilliseconds:F0}ms");
            Assert.True(
                sw.Elapsed < TimeSpan.FromSeconds(2),
                $"Pacing too slow, elapsed={sw.Elapsed.TotalMilliseconds:F0}ms");
            Assert.True(new FileInfo(path).Length >= bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Registry_replaces_prior_job_for_same_struna_without_removing()
    {
        var registry = new TrackJobRegistry(Options.Create(new SourceModuleOptions { MaxParallelJobs = 4 }));
        var first = registry.Create("s1", "a", "/tmp/a.pcm");
        var second = registry.Create("s1", "b", "/tmp/b.pcm");

        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.True(registry.TryGet(first.TrackJobId, out _));
        Assert.True(registry.TryGet(second.TrackJobId, out _));
        Assert.False(second.Cancellation.IsCancellationRequested);
    }

    private static TrackPlaybackService CreateService(
        ITrackJobRegistry jobs,
        IYouTubeCatalog youtube,
        IFifoAudioSink fifo)
    {
        var manifest = new ModuleManifest
        {
            Slug = "magpie",
            Kind = "source",
            OtelServiceName = "bardie.source.magpie",
        };
        var cache = new ModuleTuneCache(
            new InMemoryBlobs(),
            new StubLibrary(),
            manifest,
            NullLogger<ModuleTuneCache>.Instance);
#if DEBUG
        return new TrackPlaybackService(
            jobs,
            youtube,
            cache,
            new StubTranscoder(),
            fifo,
            new SinePcmGenerator(Options.Create(new SinePcmOptions())),
            manifest,
            NullLogger<TrackPlaybackService>.Instance);
#else
        return new TrackPlaybackService(
            jobs,
            youtube,
            cache,
            new StubTranscoder(),
            fifo,
            manifest,
            NullLogger<TrackPlaybackService>.Instance);
#endif
    }

    private sealed class StubYouTube : IYouTubeCatalog
    {
        public Task<IReadOnlyList<MediaSearchHit>> SearchAsync(
            string? title,
            string? artist,
            string? owner,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaSearchHit>>([]);

        public Task<ResolvedMedia?> ResolveAsync(string trackRef, CancellationToken cancellationToken = default) =>
            Task.FromResult<ResolvedMedia?>(new ResolvedMedia(
                ExternalId: trackRef,
                Title: "T",
                Artist: "A",
                ArtworkUrl: null,
                DurationSeconds: 1));

        public Task DownloadAudioAsync(string videoId, Stream destination, CancellationToken cancellationToken = default) =>
            destination.WriteAsync(new byte[] { 1, 2, 3, 4 }, cancellationToken).AsTask();
    }

    private sealed class StubTranscoder : IPcmTranscoder
    {
        public Task<Stream> TranscodeToPcmAsync(string inputPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(new byte[4800]));
    }

    private sealed class RecordingFifo : IFifoAudioSink
    {
        public Task WriteAsync(
            string audioEndpoint,
            Stream pcm,
            CancellationToken cancellationToken = default,
            Func<bool>? isPaused = null) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class InMemoryBlobs : IModuleBlobStorageClient
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);

        public Task<PutBlobResult> PutAsync(
            Stream content,
            string? key = null,
            string? contentType = null,
            CancellationToken cancellationToken = default)
        {
            key ??= Guid.NewGuid().ToString("N");
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            _store[key] = ms.ToArray();
            return Task.FromResult(new PutBlobResult(key, ms.Length));
        }

        public Task<BlobGetResult?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            if (!_store.TryGetValue(key, out var bytes))
            {
                return Task.FromResult<BlobGetResult?>(null);
            }

            return Task.FromResult<BlobGetResult?>(new BlobGetResult(new MemoryStream(bytes), "application/octet-stream"));
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_store.ContainsKey(key));

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_store.Remove(key));
    }

    private sealed class StubLibrary : IModuleLibraryClient
    {
        public Task<EnsureTuneResult> EnsureTuneAsync(
            EnsureTuneCommand command,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EnsureTuneResult(Guid.NewGuid(), Created: true));
    }
}
