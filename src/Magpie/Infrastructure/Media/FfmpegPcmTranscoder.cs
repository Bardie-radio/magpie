using System.Runtime.InteropServices;
using Bardie.Module.Source;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Options;

namespace Magpie.Infrastructure.Media;

/// <summary>
/// In-process libav demux/decode/resample via FFmpeg.AutoGen (not the ffmpeg CLI).
/// Output: <see cref="CanonicalPcm"/> (s16le / 48 kHz / stereo).
/// </summary>
public sealed unsafe class FfmpegPcmTranscoder : IPcmTranscoder
{
    private const int OutSampleRate = CanonicalPcm.SampleRate;
    private const int OutChannels = CanonicalPcm.Channels;

    private readonly ILogger<FfmpegPcmTranscoder> _logger;
    private readonly MagpieOptions _options;
    private readonly object _initLock = new();
    private bool _initialized;

    public FfmpegPcmTranscoder(
        IOptions<MagpieOptions> options,
        ILogger<FfmpegPcmTranscoder> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<Stream> TranscodeToPcmAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Media input not found.", inputPath);
        }

        EnsureNativeLoaded();
        cancellationToken.ThrowIfCancellationRequested();

        var pcmPath = Path.Combine(Path.GetTempPath(), $"magpie-pcm-{Guid.NewGuid():N}.s16le");
        try
        {
            DecodeFileToPcm(inputPath, pcmPath, cancellationToken);
            return Task.FromResult<Stream>(new TempFileStream(pcmPath));
        }
        catch
        {
            TryDelete(pcmPath);
            throw;
        }
    }

    private void EnsureNativeLoaded()
    {
        if (_initialized)
        {
            return;
        }

        lock (_initLock)
        {
            if (_initialized)
            {
                return;
            }

            var root = ResolveFfmpegRoot();
            if (!string.IsNullOrWhiteSpace(root))
            {
                ffmpeg.RootPath = root;
                _logger.LogInformation("FFmpeg.AutoGen RootPath={Root}", root);
            }

            DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
            DynamicallyLoadedBindings.Initialize();

            try
            {
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
                _logger.LogInformation("FFmpeg native ready: {Version}", ffmpeg.av_version_info());
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "FFmpeg.AutoGen failed after Initialize — need FFmpeg 6.1 shared libs (libavcodec.so.60). Check MAGPIE_FFMPEG_ROOT.",
                    ex);
            }

            _initialized = true;
        }
    }

    private string? ResolveFfmpegRoot()
    {
        if (!string.IsNullOrWhiteSpace(_options.FfmpegRootPath) && Directory.Exists(_options.FfmpegRootPath))
        {
            return _options.FfmpegRootPath;
        }

        foreach (var candidate in new[]
                 {
                     "/usr/lib/x86_64-linux-gnu",
                     "/usr/lib/aarch64-linux-gnu",
                     "/usr/lib",
                     "/usr/local/lib",
                 })
        {
            if (Directory.Exists(candidate)
                && Directory.EnumerateFiles(candidate, "libavcodec.so*").Any())
            {
                return candidate;
            }
        }

        return null;
    }

    private static void DecodeFileToPcm(string inputPath, string pcmPath, CancellationToken cancellationToken)
    {
        AVFormatContext* format = null;
        AVCodecContext* codecCtx = null;
        SwrContext* swr = null;
        AVPacket* packet = null;
        AVFrame* frame = null;

        AVChannelLayout outLayout;
        ffmpeg.av_channel_layout_default(&outLayout, OutChannels);

        try
        {
            AVFormatContext* fmtPtr = null;
            var open = ffmpeg.avformat_open_input(&fmtPtr, inputPath, null, null);
            format = fmtPtr;
            if (open < 0)
            {
                throw new InvalidOperationException($"avformat_open_input failed: {Error(open)}");
            }

            var find = ffmpeg.avformat_find_stream_info(format, null);
            if (find < 0)
            {
                throw new InvalidOperationException($"avformat_find_stream_info failed: {Error(find)}");
            }

            AVCodec* codec = null;
            var streamIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
            if (streamIndex < 0 || codec is null)
            {
                throw new InvalidOperationException("No audio stream found in media.");
            }

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (codecCtx is null)
            {
                throw new InvalidOperationException("avcodec_alloc_context3 failed.");
            }

            var paramsCopy = ffmpeg.avcodec_parameters_to_context(codecCtx, format->streams[streamIndex]->codecpar);
            if (paramsCopy < 0)
            {
                throw new InvalidOperationException($"avcodec_parameters_to_context failed: {Error(paramsCopy)}");
            }

            var openCodec = ffmpeg.avcodec_open2(codecCtx, codec, null);
            if (openCodec < 0)
            {
                throw new InvalidOperationException($"avcodec_open2 failed: {Error(openCodec)}");
            }

            AVChannelLayout inLayout = codecCtx->ch_layout;
            if (inLayout.nb_channels <= 0)
            {
                ffmpeg.av_channel_layout_default(&inLayout, 2);
            }

            SwrContext* swrLocal = null;
            var swrAlloc = ffmpeg.swr_alloc_set_opts2(
                &swrLocal,
                &outLayout,
                AVSampleFormat.AV_SAMPLE_FMT_S16,
                OutSampleRate,
                &inLayout,
                codecCtx->sample_fmt,
                codecCtx->sample_rate,
                0,
                null);
            if (swrAlloc < 0 || swrLocal is null)
            {
                throw new InvalidOperationException($"swr_alloc_set_opts2 failed: {Error(swrAlloc)}");
            }

            swr = swrLocal;
            var swrInit = ffmpeg.swr_init(swr);
            if (swrInit < 0)
            {
                throw new InvalidOperationException($"swr_init failed: {Error(swrInit)}");
            }

            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            if (packet is null || frame is null)
            {
                throw new InvalidOperationException("Failed to allocate AVPacket/AVFrame.");
            }

            using var output = new FileStream(pcmPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            byte*[] dstData = new byte*[1];
            byte* dstPlane = null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = ffmpeg.av_read_frame(format, packet);
                if (read == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                if (read < 0)
                {
                    throw new InvalidOperationException($"av_read_frame failed: {Error(read)}");
                }

                if (packet->stream_index != streamIndex)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                var send = ffmpeg.avcodec_send_packet(codecCtx, packet);
                ffmpeg.av_packet_unref(packet);
                if (send < 0)
                {
                    throw new InvalidOperationException($"avcodec_send_packet failed: {Error(send)}");
                }

                DrainFrames(codecCtx, swr, frame, output, ref dstPlane, dstData);
            }

            ffmpeg.avcodec_send_packet(codecCtx, null);
            DrainFrames(codecCtx, swr, frame, output, ref dstPlane, dstData);

            // Flush resampler
            FlushResampler(swr, output, ref dstPlane, dstData);
            output.Flush();

            if (dstPlane is not null)
            {
                ffmpeg.av_free(dstPlane);
                dstPlane = null;
            }
        }
        finally
        {
            ffmpeg.av_channel_layout_uninit(&outLayout);

            if (frame is not null)
            {
                var f = frame;
                ffmpeg.av_frame_free(&f);
            }

            if (packet is not null)
            {
                var p = packet;
                ffmpeg.av_packet_free(&p);
            }

            if (swr is not null)
            {
                var s = swr;
                ffmpeg.swr_free(&s);
            }

            if (codecCtx is not null)
            {
                var c = codecCtx;
                ffmpeg.avcodec_free_context(&c);
            }

            if (format is not null)
            {
                var fmt = format;
                ffmpeg.avformat_close_input(&fmt);
            }
        }
    }

    private static void DrainFrames(
        AVCodecContext* codecCtx,
        SwrContext* swr,
        AVFrame* frame,
        Stream output,
        ref byte* dstPlane,
        byte*[] dstData)
    {
        while (true)
        {
            var receive = ffmpeg.avcodec_receive_frame(codecCtx, frame);
            if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            if (receive < 0)
            {
                throw new InvalidOperationException($"avcodec_receive_frame failed: {Error(receive)}");
            }

            ConvertAndWrite(swr, frame->extended_data, frame->nb_samples, output, ref dstPlane, dstData);
            ffmpeg.av_frame_unref(frame);
        }
    }

    private static void FlushResampler(
        SwrContext* swr,
        Stream output,
        ref byte* dstPlane,
        byte*[] dstData)
    {
        ConvertAndWrite(swr, null, 0, output, ref dstPlane, dstData);
    }

    private static void ConvertAndWrite(
        SwrContext* swr,
        byte** srcData,
        int srcSamples,
        Stream output,
        ref byte* dstPlane,
        byte*[] dstData)
    {
        var outSamples = (int)ffmpeg.av_rescale_rnd(
            ffmpeg.swr_get_delay(swr, OutSampleRate) + Math.Max(srcSamples, 0),
            OutSampleRate,
            OutSampleRate,
            AVRounding.AV_ROUND_UP);
        if (outSamples <= 0 && srcSamples <= 0)
        {
            return;
        }

        outSamples = Math.Max(outSamples, 1024);
        var bufferSize = ffmpeg.av_samples_get_buffer_size(
            null,
            OutChannels,
            outSamples,
            AVSampleFormat.AV_SAMPLE_FMT_S16,
            1);
        if (bufferSize < 0)
        {
            throw new InvalidOperationException($"av_samples_get_buffer_size failed: {Error(bufferSize)}");
        }

        if (dstPlane is not null)
        {
            var freePtr = dstPlane;
            ffmpeg.av_free(freePtr);
            dstPlane = null;
        }

        dstPlane = (byte*)ffmpeg.av_malloc((ulong)bufferSize);

        var outPtrs = stackalloc byte*[8];
        outPtrs[0] = dstPlane;
        var converted = ffmpeg.swr_convert(swr, outPtrs, outSamples, srcData, srcSamples);

        if (converted < 0)
        {
            throw new InvalidOperationException($"swr_convert failed: {Error(converted)}");
        }

        if (converted == 0)
        {
            return;
        }

        var byteCount = converted * CanonicalPcm.BytesPerFrame;
        var managed = new byte[byteCount];
        Marshal.Copy((nint)dstPlane, managed, 0, byteCount);
        output.Write(managed, 0, byteCount);
    }

    private static string Error(int code)
    {
        var buffer = new byte[1024];
        fixed (byte* ptr = buffer)
        {
            ffmpeg.av_strerror(code, ptr, (ulong)buffer.Length);
            return Marshal.PtrToStringUTF8((nint)ptr) ?? code.ToString();
        }
    }

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

    private sealed class TempFileStream : FileStream
    {
        private readonly string _path;

        public TempFileStream(string path)
            : base(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.DeleteOnClose)
        {
            _path = path;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            TryDelete(_path);
        }
    }
}
