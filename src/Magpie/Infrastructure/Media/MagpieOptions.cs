namespace Magpie.Infrastructure.Media;

public sealed class MagpieOptions
{
    public const string SectionName = "Magpie";

    /// <summary>Directory containing libav* shared libraries (FFmpeg.AutoGen).</summary>
    public string? FfmpegRootPath { get; set; }

    /// <summary>
    /// YouTube session cookies (Netscape cookies.txt body or <c>name=value; …</c>).
    /// Prefer env <c>MAGPIE_YOUTUBE_COOKIES</c> over committing secrets to appsettings.
    /// </summary>
    public string? YoutubeCookies { get; set; }

    /// <summary>
    /// Path to a Netscape cookies.txt file (Docker secret / bind mount).
    /// Used when <see cref="YoutubeCookies"/> is empty. Env: <c>MAGPIE_YOUTUBE_COOKIES_FILE</c>.
    /// </summary>
    public string? YoutubeCookiesFile { get; set; }
}
