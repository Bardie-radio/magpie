namespace Magpie.Infrastructure.Media;

public sealed record MediaSearchHit(
    string TrackRef,
    string ExternalId,
    string Title,
    string? Artist,
    string? Owner,
    string? ArtworkUrl,
    double? DurationSeconds);

public sealed record ResolvedMedia(
    string ExternalId,
    string Title,
    string? Artist,
    string? ArtworkUrl,
    double? DurationSeconds,
    bool IsSine);

public interface IYouTubeCatalog
{
    Task<IReadOnlyList<MediaSearchHit>> SearchAsync(
        string? title,
        string? artist,
        string? owner,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ResolvedMedia?> ResolveAsync(string trackRef, CancellationToken cancellationToken = default);

    Task DownloadAudioAsync(
        string videoId,
        Stream destination,
        CancellationToken cancellationToken = default);
}
