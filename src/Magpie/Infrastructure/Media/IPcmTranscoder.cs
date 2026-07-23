namespace Magpie.Infrastructure.Media;

/// <summary>Decodes an encoded media file/stream to canonical PCM (s16le / 48 kHz / stereo).</summary>
public interface IPcmTranscoder
{
    /// <summary>
    /// Transcodes <paramref name="inputPath"/> to a temporary PCM file and returns a readable stream.
    /// Caller owns and must dispose the returned stream (deletes the temp file on dispose).
    /// </summary>
    Task<Stream> TranscodeToPcmAsync(string inputPath, CancellationToken cancellationToken = default);
}
