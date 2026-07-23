namespace Magpie.Infrastructure.Media;

public sealed class MagpieOptions
{
    public const string SectionName = "Magpie";

    /// <summary>Directory containing libav* shared libraries (FFmpeg.AutoGen).</summary>
    public string? FfmpegRootPath { get; set; }
}
