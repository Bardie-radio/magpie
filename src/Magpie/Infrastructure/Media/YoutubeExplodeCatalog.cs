using System.Text.RegularExpressions;
#if DEBUG
using Bardie.Module.Source.Debug;
#endif
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace Magpie.Infrastructure.Media;

public sealed partial class YoutubeExplodeCatalog : IYouTubeCatalog
{
    private readonly YoutubeClient _youtube = new();
    private readonly ILogger<YoutubeExplodeCatalog> _logger;

    public YoutubeExplodeCatalog(ILogger<YoutubeExplodeCatalog> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<MediaSearchHit>> SearchAsync(
        string? title,
        string? artist,
        string? owner,
        int limit,
        CancellationToken cancellationToken = default)
    {
        limit = limit <= 0 ? 10 : Math.Clamp(limit, 1, 50);
        var query = BuildQuery(title, artist, owner);

        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

#if DEBUG
        if (DevProofTrack.Matches(query, "magpie"))
        {
            return [ProofHit()];
        }
#endif

        if (TryParseVideoId(query, out var directId))
        {
            var resolved = await ResolveAsync(directId, cancellationToken).ConfigureAwait(false);
            return resolved is null
                ? []
                : [new MediaSearchHit(
                    resolved.ExternalId,
                    resolved.ExternalId,
                    resolved.Title,
                    resolved.Artist,
                    resolved.Artist,
                    resolved.ArtworkUrl,
                    resolved.DurationSeconds)];
        }

        try
        {
            var results = new List<MediaSearchHit>();
            await foreach (var video in _youtube.Search.GetVideosAsync(query).WithCancellation(cancellationToken))
            {
                results.Add(new MediaSearchHit(
                    video.Id.Value,
                    video.Id.Value,
                    video.Title,
                    video.Author.ChannelTitle,
                    video.Author.ChannelTitle,
                    video.Thumbnails.TryGetWithHighestResolution()?.Url,
                    video.Duration?.TotalSeconds));

                if (results.Count >= limit)
                {
                    break;
                }
            }

            if (results.Count == 0 && TryParseVideoId(query.Trim(), out var fallbackId))
            {
                var resolved = await ResolveAsync(fallbackId, cancellationToken).ConfigureAwait(false);
                if (resolved is not null)
                {
                    results.Add(new MediaSearchHit(
                        resolved.ExternalId,
                        resolved.ExternalId,
                        resolved.Title,
                        resolved.Artist,
                        resolved.Artist,
                        resolved.ArtworkUrl,
                        resolved.DurationSeconds));
                }
            }

            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "YouTube search failed for query {Query}", query);
#if DEBUG
            if (DevProofTrack.Matches(query, "magpie"))
            {
                return [ProofHit()];
            }
#endif
            return [];
        }
    }

    public async Task<ResolvedMedia?> ResolveAsync(string trackRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackRef);
        var trimmed = trackRef.Trim();

#if DEBUG
        if (DevProofTrack.Matches(trimmed, "magpie"))
        {
            return new ResolvedMedia(
                DevProofTrack.ExternalId,
                DevProofTrack.Title,
                DevProofTrack.Artist,
                ArtworkUrl: null,
                DurationSeconds: null);
        }
#endif

        if (!TryParseVideoId(trimmed, out var videoId))
        {
            return null;
        }

        try
        {
            var video = await _youtube.Videos.GetAsync(videoId, cancellationToken).ConfigureAwait(false);
            return new ResolvedMedia(
                video.Id.Value,
                video.Title,
                video.Author.ChannelTitle,
                video.Thumbnails.TryGetWithHighestResolution()?.Url,
                video.Duration?.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve YouTube id {VideoId}", videoId);
            return null;
        }
    }

    public async Task DownloadAudioAsync(
        string videoId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        ArgumentNullException.ThrowIfNull(destination);

        var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, cancellationToken)
            .ConfigureAwait(false);
        var streamInfo = manifest.GetAudioOnlyStreams().GetWithHighestBitrate()
            ?? throw new InvalidOperationException($"No downloadable audio streams for video '{videoId}'.");

        await _youtube.Videos.Streams.CopyToAsync(streamInfo, destination, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static string BuildQuery(string? title, string? artist, string? owner)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(title))
        {
            parts.Add(title.Trim());
        }

        if (!string.IsNullOrWhiteSpace(artist))
        {
            parts.Add(artist.Trim());
        }

        if (!string.IsNullOrWhiteSpace(owner))
        {
            parts.Add(owner.Trim());
        }

        return string.Join(' ', parts);
    }

#if DEBUG
    private static MediaSearchHit ProofHit() =>
        new(
            DevProofTrack.TrackRef,
            DevProofTrack.ExternalId,
            DevProofTrack.Title,
            DevProofTrack.Artist,
            DevProofTrack.Artist,
            null,
            null);
#endif

    private static bool TryParseVideoId(string input, out string videoId)
    {
        videoId = string.Empty;
        var trimmed = input.Trim();
        if (VideoId.TryParse(trimmed) is { } parsed)
        {
            videoId = parsed.Value;
            return true;
        }

        var match = YoutubeUrlRegex().Match(trimmed);
        if (match.Success && VideoId.TryParse(match.Groups[1].Value) is { } fromUrl)
        {
            videoId = fromUrl.Value;
            return true;
        }

        if (trimmed.Length is >= 10 and <= 12 && trimmed.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
        {
            videoId = trimmed;
            return true;
        }

        return false;
    }

    [GeneratedRegex(
        @"(?:youtube\.com/watch\?v=|youtu\.be/|youtube\.com/embed/|youtube\.com/shorts/)([A-Za-z0-9_-]{10,12})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YoutubeUrlRegex();
}
