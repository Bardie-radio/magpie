namespace Magpie.Infrastructure.Media;

public sealed class MagpieOptions
{
    public const string SectionName = "Magpie";

    public int MaxParallelJobs { get; set; } = 4;

    public double SineFrequencyHz { get; set; } = 440;

    public double SineDurationSeconds { get; set; } = 30;

    /// <summary>Directory containing libav* shared libraries (FFmpeg.AutoGen).</summary>
    public string? FfmpegRootPath { get; set; }
}
