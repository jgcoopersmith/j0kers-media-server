using System.Net;
using System.Text.Json;
using J0kersMediaServer.Control;
using J0kersMediaServer.Hls;
using J0kersMediaServer.Services;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The player and the watch page are embedded HTML files with __LIKE_THIS__
/// placeholders in them. Nothing at compile time ties a token in the file to
/// the value the server passes for it, so a token renamed in one half and not
/// the other would ship a page with the literal text __SRC_JS__ where the
/// stream URL belongs and no build error to say so. These tests are what
/// notices.
///
/// The encoding assertions matter more than they look. Which value is HTML
/// encoded and which is JSON encoded is a security property rather than a
/// tidiness one: a stream name reaching the script block HTML-encoded breaks
/// its subtitle fetch, because entities are not decoded inside a script
/// element, and a name reaching the page body unencoded is a way to put
/// markup of someone else's choosing into the page.
/// </summary>
public class PageTemplateTests
{
    // A name that is wrong in every direction at once: the characters HTML
    // encoding exists for, the characters JSON escaping exists for, and a
    // stretch that looks like one of the placeholder tokens.
    private const string Nasty = "a&b<c>d\"e __SRC__ f";

    private const StringComparison Exact = StringComparison.Ordinal;

    [Fact]
    public void Player_page_leaves_no_placeholder_behind()
    {
        var page = ControlApi.PlayerPage("/vod-x/index.m3u8", Nasty);
        Assert.DoesNotContain("__TITLE__", page, Exact);
        Assert.DoesNotContain("__SRC_JS__", page, Exact);
        // the interpolation form the page used to be written in, in case a
        // future edit copies a fragment back out of the old source
        Assert.DoesNotContain("{{", page, Exact);
    }

    [Fact]
    public void Player_page_json_encodes_the_source_and_html_encodes_the_title()
    {
        const string Src = "/vod-a&b/index.m3u8";
        var page = ControlApi.PlayerPage(Src, Nasty);
        var script = page[page.LastIndexOf("<script>", Exact)..];
        Assert.Contains("const src = " + JsonSerializer.Serialize(Src) + ",", script, Exact);
        Assert.Contains("<title>" + WebUtility.HtmlEncode(Nasty), page, Exact);
        // whichever way round the two encodings are, neither value survives raw
        Assert.DoesNotContain(Nasty, page, Exact);
        Assert.DoesNotContain("<title>a&b<c>", page, Exact);
    }

    [Fact]
    public void Watch_page_leaves_no_placeholder_behind()
    {
        var page = HlsServer.WatchPage("vod-skyfall-2012", "?t=abc");
        foreach (var token in new[] { "__PRETTY__", "__NAME__", "__NAME_JS__", "__SRC__", "__TOKEN_JS__" })
            Assert.DoesNotContain(token, page, Exact);
        Assert.DoesNotContain("{{", page, Exact);
    }

    [Fact]
    public void Watch_page_json_encodes_the_name_in_script_and_html_encodes_it_in_the_body()
    {
        var page = HlsServer.WatchPage(Nasty, "");
        var split = page.LastIndexOf("<script>", Exact);
        var body = page[..split];
        var script = page[split..];
        // the script sees the JSON form, so encodeURIComponent gets the real
        // name and the subs.json request it builds resolves
        Assert.Contains("const stream = " + JsonSerializer.Serialize(Nasty) + ";", script, Exact);
        // the body carries the HTML-encoded form instead
        Assert.Contains(WebUtility.HtmlEncode(Nasty), body, Exact);
        Assert.Contains("&amp;", body, Exact);
        // and the unencoded name is nowhere in the document at all
        Assert.DoesNotContain(Nasty, page, Exact);
    }

    [Fact]
    public void Watch_page_carries_the_share_token_on_to_the_playlist_and_the_subtitles()
    {
        var page = HlsServer.WatchPage("vod-x", "?t=abc123");
        Assert.Contains("const src = \"/vod-x/index.m3u8?t=abc123\";", page, Exact);
        Assert.Contains("\"/subs.json\" + \"?t=abc123\"", page, Exact);
    }

    // A value that happens to contain another token's spelling has to be
    // written out and left alone. Chained string.Replace calls would rescan it
    // and substitute inside a value the server never meant as a template.
    [Fact]
    public void Fill_never_substitutes_inside_a_value_it_has_already_written()
    {
        var filled = PageTemplate.Fill("[__A__][__B__]", ("__A__", "__B__"), ("__B__", "x"));
        Assert.Equal("[__B__][x]", filled);
    }

    // Two tokens where one really is a prefix of the other: the longer has to
    // win, or the output keeps a stranded tail.
    [Fact]
    public void Fill_prefers_the_longest_matching_token()
    {
        var filled = PageTemplate.Fill("__A__B__", ("__A__", "short"), ("__A__B__", "long"));
        Assert.Equal("long", filled);
    }
}
