using System.Globalization;
using System.Net;

namespace Magpie.Infrastructure.Media;

/// <summary>
/// Parses YouTube session cookies from Netscape cookies.txt or Cookie-header form
/// (<c>name=value; name2=value2</c>).
/// </summary>
public static class YoutubeCookieParser
{
    private const string DefaultDomain = ".youtube.com";
    private const string DefaultPath = "/";

    public static IReadOnlyList<Cookie> Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var trimmed = content.Trim();
        if (LooksLikeNetscape(trimmed))
        {
            return ParseNetscape(trimmed);
        }

        return ParseCookieHeader(trimmed);
    }

    public static IReadOnlyList<Cookie> LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    private static bool LooksLikeNetscape(string content) =>
        content.Contains("# Netscape", StringComparison.OrdinalIgnoreCase)
        || content.Contains('\t');

    private static IReadOnlyList<Cookie> ParseNetscape(string content)
    {
        var cookies = new List<Cookie>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var httpOnly = false;
            // Netscape HttpOnly extension: "#HttpOnly_.youtube.com\tTRUE\t/\t..."
            if (line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
            {
                httpOnly = true;
                line = line["#HttpOnly_".Length..];
            }
            else if (line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 7)
            {
                continue;
            }

            var domain = parts[0].Trim();
            var path = parts[2].Trim();
            var name = parts[5].Trim();
            var value = parts[6].Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(domain))
            {
                continue;
            }

            if (!IsYoutubeDomain(domain))
            {
                continue;
            }

            var cookie = new Cookie(name, value, string.IsNullOrEmpty(path) ? DefaultPath : path, NormalizeDomain(domain))
            {
                Secure = string.Equals(parts[3].Trim(), "TRUE", StringComparison.OrdinalIgnoreCase),
                HttpOnly = httpOnly,
            };

            if (long.TryParse(parts[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiry)
                && expiry > 0)
            {
                try
                {
                    cookie.Expires = DateTimeOffset.FromUnixTimeSeconds(expiry).UtcDateTime;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Ignore unusable expiry; CookieContainer still sends session cookies.
                }
            }

            cookies.Add(cookie);
        }

        return cookies;
    }

    private static IReadOnlyList<Cookie> ParseCookieHeader(string content)
    {
        var cookies = new List<Cookie>();
        foreach (var segment in content.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = segment[..eq].Trim();
            var value = segment[(eq + 1)..].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            // Skip Cookie attribute tokens if someone pasted a Set-Cookie line.
            if (name.Equals("Path", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Domain", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Expires", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Max-Age", StringComparison.OrdinalIgnoreCase)
                || name.Equals("SameSite", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cookies.Add(new Cookie(name, value, DefaultPath, DefaultDomain) { Secure = true });
        }

        return cookies;
    }

    private static bool IsYoutubeDomain(string domain)
    {
        var d = domain.TrimStart('.').ToLowerInvariant();
        return d is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com"
            or "google.com" or "www.google.com";
    }

    private static string NormalizeDomain(string domain) =>
        domain.StartsWith('.') ? domain : "." + domain.TrimStart('.');
}
