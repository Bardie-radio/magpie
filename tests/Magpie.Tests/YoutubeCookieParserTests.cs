using Magpie.Infrastructure.Media;
using Xunit;

namespace Magpie.Tests;

public class YoutubeCookieParserTests
{
    [Fact]
    public void Parse_cookie_header_form()
    {
        var cookies = YoutubeCookieParser.Parse("SID=abc; HSID=def; SSID=ghi");

        Assert.Equal(3, cookies.Count);
        Assert.Equal("SID", cookies[0].Name);
        Assert.Equal("abc", cookies[0].Value);
        Assert.Equal(".youtube.com", cookies[0].Domain);
        Assert.Equal("/", cookies[0].Path);
    }

    [Fact]
    public void Parse_netscape_cookies_txt()
    {
        const string netscape = """
            # Netscape HTTP Cookie File
            .youtube.com	TRUE	/	TRUE	1893456000	LOGIN_INFO	AFmmF2swRQIhAExample
            .youtube.com	TRUE	/	TRUE	1893456000	VISITOR_INFO1_LIVE	xyz
            .example.com	TRUE	/	FALSE	1893456000	OTHER	nope
            #HttpOnly_.youtube.com	TRUE	/	TRUE	1893456000	SID	secret-sid
            """;

        var cookies = YoutubeCookieParser.Parse(netscape);

        Assert.Equal(3, cookies.Count);
        Assert.Contains(cookies, c => c.Name == "LOGIN_INFO" && c.Value.StartsWith("AFmm"));
        Assert.Contains(cookies, c => c.Name == "VISITOR_INFO1_LIVE");
        var sid = Assert.Single(cookies, c => c.Name == "SID");
        Assert.True(sid.HttpOnly);
        Assert.Equal("secret-sid", sid.Value);
        Assert.DoesNotContain(cookies, c => c.Name == "OTHER");
    }

    [Fact]
    public void Parse_empty_returns_empty()
    {
        Assert.Empty(YoutubeCookieParser.Parse(null));
        Assert.Empty(YoutubeCookieParser.Parse("   "));
    }

    [Fact]
    public void LoadFromFile_reads_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), "magpie-cookies-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "SID=fromfile; HSID=x");
            var cookies = YoutubeCookieParser.LoadFromFile(path);
            Assert.Equal(2, cookies.Count);
            Assert.Equal("fromfile", Assert.Single(cookies, c => c.Name == "SID").Value);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
