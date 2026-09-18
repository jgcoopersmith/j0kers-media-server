using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using J0kersMediaServer.Auth;
using J0kersMediaServer.Config;
using J0kersMediaServer.Hls;
using J0kersMediaServer.Media;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// Lead [U02] from the 2026-09-17 audit: the HLS port routed on the path as
/// HttpListener hands it over, which is still percent-encoded. Stream names
/// keep non-ASCII letters (Slugify and ChannelStream both keep any letter), a
/// browser has to percent-encode those, and so "/vod-am%C3%A9lie-…/index.m3u8"
/// was looked up on disk, and checked against a share link's signature, under
/// its encoded spelling. Neither matches: an all-streams token got 404
/// "unknown stream", and a single-stream link - which /api/media/token signs
/// over the decoded name - got 401.
///
/// Every request here goes to a real HlsServer on 127.0.0.1 over a raw socket,
/// so the bytes on the wire are exactly the ones written below: HttpClient
/// would re-encode or normalise some of them before they left. No discovery,
/// no ffmpeg, nothing outside a temp folder under %TEMP%\claude.
///
/// Three kinds of test:
///  - RED  fails on the code before the fix, for the lead's own reason;
///  - EVIDENCE shows the lead's precondition is real (passes either way);
///  - PIN  encoded traversal must not escape - passes before and after, and is
///         here so decoding the path can never quietly open a way out.
/// </summary>
public sealed class LeadHlsNamesTests : IClassFixture<LeadHlsNamesTests.Rig>
{
    private const string Outside = "OUTSIDE-THE-MEDIA-ROOT";          // in every file a request must not reach
    private const string OtherStream = "ANOTHER-STREAMS-PLAYLIST";   // in "other"'s playlist
    private const string LooseInRoot = "LOOSE-IN-THE-MEDIA-ROOT";    // files directly in the media root

    private readonly Rig _rig;

    public LeadHlsNamesTests(Rig rig) => _rig = rig;

    // ---- EVIDENCE: non-ASCII stream names are made today ----------------

    /// <summary>
    /// EVIDENCE. The names the server itself produces for "Amélie (2001).mkv"
    /// and for a channel called "Télé Info" keep the accented letter: both
    /// keep anything char.IsLetterOrDigit accepts, which is every script, not
    /// only ASCII. So these streams exist the moment someone has such a file.
    /// </summary>
    [Fact]
    public void Evidence_stream_names_keep_non_ascii_letters()
    {
        Assert.Equal("amélie-2001", Slug("Amélie (2001)"));
        Assert.Equal("vod-amélie-2001-abcdef12", Rig.Film);
        Assert.Equal("ch-télé-info", FfmpegManager.ChannelStream("Télé Info"));
        // and what the dashboard puts on the wire for them
        // (dashboard-player.js: "/" + encodeURIComponent(name) + "/index.m3u8")
        Assert.Equal("vod-am%C3%A9lie-2001-abcdef12", Uri.EscapeDataString(Rig.Film));
    }

    // ---- RED: the lead's trigger ----------------------------------------

    /// <summary>
    /// RED. The dashboard's own request for a converted film with an accent in
    /// its title, carrying the all-streams token it takes at startup.
    /// Before the fix: 404 "unknown stream".
    /// </summary>
    [Fact]
    public async Task Red_dashboard_plays_a_non_ascii_film_with_its_all_streams_token()
    {
        var token = _rig.Links.Sign(MediaLink.AllStreams, TimeSpan.FromHours(1));
        var enc = Uri.EscapeDataString(Rig.Film);

        var (status, body) = await _rig.Get($"/{enc}/index.m3u8?{token}");
        Assert.True(status == 200 && body.StartsWith("#EXTM3U", StringComparison.Ordinal),
            $"playlist for {Rig.Film} answered {status} \"{body.Trim()}\"");

        // the segment line the playlist hands back, as a player would follow it
        var (segStatus, segBody) = await _rig.Get($"/{enc}/seg_00000.ts?{token}");
        Assert.True(segStatus == 200 && segBody == "SEGMENT:" + Rig.Film,
            $"segment of {Rig.Film} answered {segStatus} \"{segBody.Trim()}\"");
    }

    /// <summary>
    /// RED. A single-stream share link. /api/media/token?stream=… reads the
    /// name from the query string, which HttpListener decodes, and signs over
    /// that decoded name (ControlApi.MintMediaToken: _mediaLinks.Sign(scope, …));
    /// the link then names the stream encoded in its path.
    /// Before the fix: 401, because the signature was checked against the
    /// still-encoded spelling.
    /// </summary>
    [Fact]
    public async Task Red_share_link_signed_over_a_non_ascii_name_is_accepted()
    {
        var token = _rig.Links.Sign(Rig.Film, TimeSpan.FromHours(1));
        var enc = Uri.EscapeDataString(Rig.Film);

        var (status, body) = await _rig.Get($"/{enc}/index.m3u8?{token}");
        Assert.True(status == 200 && body.StartsWith("#EXTM3U", StringComparison.Ordinal),
            $"share link for {Rig.Film} answered {status} \"{body.Trim()}\"");

        var (segStatus, segBody) = await _rig.Get($"/{enc}/seg_00000.ts?{token}");
        Assert.True(segStatus == 200 && segBody == "SEGMENT:" + Rig.Film,
            $"share-link segment of {Rig.Film} answered {segStatus} \"{segBody.Trim()}\"");
    }

    /// <summary>
    /// RED. The channel half of the trigger: "Télé Info" restreams into
    /// ch-télé-info. Before the fix: 404 "unknown stream".
    /// </summary>
    [Fact]
    public async Task Red_a_channel_with_a_non_ascii_name_plays()
    {
        var token = _rig.Links.Sign(MediaLink.AllStreams, TimeSpan.FromHours(1));
        var (status, body) = await _rig.Get($"/{Uri.EscapeDataString(Rig.Channel)}/index.m3u8?{token}");
        Assert.True(status == 200 && body.StartsWith("#EXTM3U", StringComparison.Ordinal),
            $"playlist for {Rig.Channel} answered {status} \"{body.Trim()}\"");
    }

    /// <summary>
    /// RED. The watch page the dashboard links to ("/watch/" +
    /// encodeURIComponent(name)) opens, and points its player at the playlist
    /// encoded once - not at "%25C3%25A9", which is what re-escaping the
    /// undecoded name produced. Before the fix: 404 "unknown stream".
    /// </summary>
    [Fact]
    public async Task Red_watch_page_for_a_non_ascii_name_opens()
    {
        var token = _rig.Links.Sign(MediaLink.AllStreams, TimeSpan.FromHours(1));
        var enc = Uri.EscapeDataString(Rig.Film);
        var (status, body) = await _rig.Get($"/watch/{enc}?{token}");
        Assert.True(status == 200, $"watch page for {Rig.Film} answered {status} \"{body.Trim()}\"");
        Assert.Contains($"/{enc}/index.m3u8?{token}", body);
        Assert.DoesNotContain("%25C3", body);
    }

    // ---- PIN: encoded traversal stays inside ----------------------------

    /// <summary>Controls, so the pins below are refusals and not a server that refuses everything.</summary>
    [Fact]
    public async Task Pin_controls_plain_names_play_and_a_share_link_is_confined_to_its_stream()
    {
        var all = _rig.Links.Sign(MediaLink.AllStreams, TimeSpan.FromHours(1));
        var legit = _rig.Links.Sign("legit", TimeSpan.FromHours(1));

        Assert.Equal(200, (await _rig.Get($"/legit/index.m3u8?{all}")).Status);
        Assert.Equal(200, (await _rig.Get($"/other/index.m3u8?{all}")).Status);
        Assert.Equal(200, (await _rig.Get($"/legit/index.m3u8?{legit}")).Status);
        Assert.Equal(401, (await _rig.Get($"/other/index.m3u8?{legit}")).Status);
    }

    /// <summary>
    /// Checks the claim in HlsServer that each piece of the path is decoded
    /// exactly once: a folder whose real name holds a literal "%41" is reached
    /// by that name encoded once ("%2541"), and "%41" read as "A" reaches
    /// nothing. Were the path decoded twice - Windows' HTTP layer and then the
    /// server - the first would miss and the second would find it.
    /// </summary>
    [Fact]
    public async Task A_name_is_decoded_exactly_once()
    {
        var token = _rig.Links.Sign(MediaLink.AllStreams, TimeSpan.FromHours(1));
        var once = await _rig.Get($"/pct%2541x/index.m3u8?{token}");
        var twice = await _rig.Get($"/pctAx/index.m3u8?{token}");
        Assert.True(once.Status == 200 && twice.Status == 404,
                    $"the folder named {Rig.PercentName}: asked for as pct%2541x it answered {once.Status}, "
                    + $"as pctAx {twice.Status} - the path is not decoded exactly once");
    }

    /// <summary>
    /// PIN. An all-streams token - the widest there is - still cannot use an
    /// encoded "..", "/" or "\" (or a rooted name, or a name that is only
    /// whitespace) to read outside the stream it names, outside the media
    /// root, the sibling folder "media-old" whose name merely starts with the
    /// root's, or the internal dot-directories. Some of these http.sys refuses
    /// itself; the rest must be refused by the server.
    /// </summary>
    [Theory]
    [InlineData("/%2E%2E/outside.m3u8")]
    [InlineData("/%2E%2E%2Fmedia-old/index.m3u8")]
    [InlineData("/%2e%2e%2fmedia-old/index.m3u8")]
    [InlineData("/..%2Fmedia-old/index.m3u8")]
    [InlineData("/..%5Cmedia-old/index.m3u8")]
    [InlineData("/%2E%2E%5Cmedia-old/index.m3u8")]
    [InlineData("/legit%2F..%2F..%2Fmedia-old/index.m3u8")]
    [InlineData("/legit%5C..%5C..%5Cmedia-old/index.m3u8")]
    [InlineData("/legit/..%2F..%2Foutside.m3u8")]
    [InlineData("/legit/..%5C..%5Coutside.m3u8")]
    [InlineData("/legit/%2E%2E%5C%2E%2E%5Coutside.ts")]
    [InlineData("/legit/%2E%2E%2F%2E%2E%2Foutside.ts")]
    [InlineData("/legit/subs/..%5C..%5C..%5Coutside.vtt")]
    [InlineData("/legit/subs/..%2F..%2F..%2Foutside.vtt")]
    [InlineData("/watch/..%2Fmedia-old")]
    [InlineData("/watch/..%5Cmedia-old")]
    [InlineData("/%252E%252E/outside.m3u8")]            // decoded once: a literal "%2E%2E", not ".."
    [InlineData("/legit/%252E%252E%255Coutside.ts")]
    [InlineData("/%2Ethumbs/index.m3u8")]               // internal dot-directory, dot encoded
    [InlineData("/C%3Amedia-old/index.m3u8")]           // drive-relative
    [InlineData("/%20/index.m3u8")]                     // a name that resolves to the media root itself
    [InlineData("/%20/rootseg.ts")]
    [InlineData("/%20%20/index.m3u8")]
    [InlineData("/%09/index.m3u8")]
    [InlineData("{ROOTED}")]                            // the rooted cases are built from the temp path
    [InlineData("{ROOTED-TS}")]
    [InlineData("{ROOTED-DIR}")]
    public async Task Pin_encoded_traversal_cannot_leave_with_an_all_streams_token(string raw)
    {
        var token = _rig.Links.Sign(MediaLink.AllStreams, TimeSpan.FromHours(1));
        var path = raw switch
        {
            "{ROOTED}" => "/legit/" + Uri.EscapeDataString(Path.Combine(_rig.Root, "outside.m3u8")),
            "{ROOTED-TS}" => "/legit/" + Uri.EscapeDataString(Path.Combine(_rig.Root, "outside.ts")),
            "{ROOTED-DIR}" => "/" + Uri.EscapeDataString(Path.Combine(_rig.Root, "media-old")) + "/index.m3u8",
            _ => raw,
        };
        var (status, body) = await _rig.Get($"{path}?{token}");
        Assert.True(status != 200, $"{path} answered 200: \"{body.Trim()}\"");
        Assert.DoesNotContain(Outside, body);
        Assert.DoesNotContain(LooseInRoot, body);
        Assert.DoesNotContain("rootseg", body);
    }

    /// <summary>
    /// PIN. A share link for one stream is the thing handed to people who are
    /// not trusted with anything else. Encoding a separator into either half of
    /// the path must not carry it into a neighbouring stream.
    /// </summary>
    [Theory]
    [InlineData("/legit/..%2Fother%2Findex.m3u8")]
    [InlineData("/legit/..%5Cother%5Cindex.m3u8")]
    [InlineData("/legit/%2E%2E%5Cother%5Cindex.m3u8")]
    [InlineData("/legit/..%2Fother%2Fseg_00000.ts")]
    [InlineData("/legit/..%5Cother%5Cseg_00000.ts")]
    [InlineData("/legit%2F..%2Fother/index.m3u8")]
    [InlineData("/legit%5C..%5Cother/index.m3u8")]
    [InlineData("/legit%2F..%2Fother/seg_00000.ts")]
    [InlineData("/watch/legit%2F..%2Fother")]
    [InlineData("/legit/subs/..%5C..%5Cother%5Cindex.vtt")]
    public async Task Pin_a_share_link_cannot_be_steered_into_another_stream(string path)
    {
        var token = _rig.Links.Sign("legit", TimeSpan.FromHours(1));
        var (status, body) = await _rig.Get($"{path}?{token}");
        Assert.True(status != 200, $"{path} with legit's share link answered 200: \"{body.Trim()}\"");
        Assert.DoesNotContain(OtherStream, body);
        Assert.DoesNotContain("SEGMENT:other", body);
    }

    /// <summary>
    /// PIN. The two guards the handler relies on, asked directly with names
    /// already decoded - the form they are given once the path is decoded,
    /// whether or not http.sys would have let such a request through.
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData("../media-old")]
    [InlineData("..\\media-old")]
    [InlineData("legit/../other")]
    [InlineData("legit\\..\\other")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".thumbs")]
    [InlineData("C:media-old")]
    [InlineData("C:\\Windows")]
    [InlineData("\\\\server\\share")]
    public void Pin_the_stream_directory_guard_refuses_decoded_separators_and_dots(string name)
    {
        Assert.Null(StreamDirectory(name));
    }

    [Theory]
    [InlineData("../other/index.m3u8")]
    [InlineData("..\\other\\index.m3u8")]
    [InlineData("..\\..\\outside.ts")]
    [InlineData("../../outside.m3u8")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share\\x.ts")]
    [InlineData("{ROOTED}")]
    public void Pin_the_file_guard_keeps_a_decoded_name_inside_its_stream(string name)
    {
        var legit = Path.Combine(_rig.Media, "legit");
        if (name == "{ROOTED}") name = Path.Combine(_rig.Root, "outside.ts");
        var m = typeof(HlsServer).GetMethod("SafeChildPath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        Assert.Null(m!.Invoke(null, new object[] { legit, name }));
    }

    /// <summary>
    /// Part of the fix, not the lead: once "%20" is decoded, a name made only of
    /// whitespace reaches the stream-directory guard, and Windows path
    /// normalisation drops trailing spaces - so " " resolved to the media root
    /// itself and passed a prefix test written for its children. Unreachable
    /// before the fix (the name stayed "%20"); refused outright after it.
    /// </summary>
    [Theory]
    [InlineData(" ")]
    [InlineData("  ")]
    public void The_stream_directory_guard_never_answers_with_the_media_root_itself(string name)
    {
        var dir = StreamDirectory(name);
        Assert.True(dir is null, $"\"{name}\" resolved to {dir}");
    }

    // ---- plumbing --------------------------------------------------------

    private string? StreamDirectory(string name)
    {
        var m = typeof(HlsServer).GetMethod("SafeStreamDirectory", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(m);
        return (string?)m!.Invoke(_rig.Server, new object[] { name });
    }

    /// <summary>FfmpegManager's own slug rule, reached rather than copied so this cannot drift from it.</summary>
    private static string Slug(string title)
    {
        var m = typeof(FfmpegManager).GetMethod("Slugify", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        return (string)m!.Invoke(null, new object[] { title })!;
    }

    /// <summary>
    /// One real HLS server for the class, on 127.0.0.1, with accounts switched
    /// on (an administrator exists) so media is authorized by the signed link
    /// exactly as on a claimed server. Streams, and the files no request may
    /// reach, all live in one temp folder removed at the end.
    /// </summary>
    public sealed class Rig : IDisposable
    {
        public static string Film => "vod-" + Slug("Amélie (2001)") + "-abcdef12";
        /// <summary>A folder whose real name holds a literal "%41" - see A_name_is_decoded_exactly_once.</summary>
        public const string PercentName = "pct%41x";
        public static string Channel => FfmpegManager.ChannelStream("Télé Info");

        public string Root { get; }
        public string Media { get; }
        public MediaLink Links { get; }
        public HlsServer Server { get; }
        public int Port { get; }

        public Rig()
        {
            Root = Path.Combine(Path.GetTempPath(), "claude", "j0kers-hlsnames-" + Guid.NewGuid().ToString("N")[..12]);
            Media = Path.Combine(Root, "media");
            var config = Path.Combine(Root, "config");
            Directory.CreateDirectory(Media);
            Directory.CreateDirectory(config);

            try
            {
                MakeStream(Film);
                MakeStream(Channel);
                MakeStream("legit");
                MakeStream("other", OtherStream);
                MakeStream(PercentName);

                // what no request may reach: beside the root, in a sibling whose
                // name starts with the root's, in an internal dot-directory, and
                // loose in the root itself
                File.WriteAllText(Path.Combine(Root, "outside.m3u8"), "#EXTM3U\n# " + Outside + "\n");
                File.WriteAllText(Path.Combine(Root, "outside.ts"), Outside);
                File.WriteAllText(Path.Combine(Root, "outside.vtt"), "WEBVTT\n\n" + Outside + "\n");
                Directory.CreateDirectory(Path.Combine(Root, "media-old"));
                File.WriteAllText(Path.Combine(Root, "media-old", "index.m3u8"), "#EXTM3U\n# " + Outside + "\n");
                Directory.CreateDirectory(Path.Combine(Media, ".thumbs"));
                File.WriteAllText(Path.Combine(Media, ".thumbs", "index.m3u8"), "#EXTM3U\n# " + Outside + "\n");
                File.WriteAllText(Path.Combine(Media, "index.m3u8"), "#EXTM3U\n# " + LooseInRoot + "\n");
                File.WriteAllText(Path.Combine(Media, "rootseg.ts"), LooseInRoot);

                var users = new UserStore(config);
                users.Create("owner", "test-admin-passphrase-9", UserStore.RoleServerAdmin, null, enabled: true);
                var sessions = new AuthService(users, "", config);
                Links = new MediaLink(config);

                HlsServer? started = null;
                var port = 0;
                for (var attempt = 0; started is null; attempt++)
                {
                    port = FreePort();
                    var hls = new HlsServer(new HlsConfig
                    {
                        Enabled = true,
                        BindAddress = "127.0.0.1",
                        Port = port,
                        MediaRoot = Media,
                    }, Root)
                    {
                        Links = Links,
                        Sessions = sessions,
                    };
                    try { hls.Start(); started = hls; }
                    catch (HttpListenerException) when (attempt < 5) { hls.Dispose(); }
                }
                Server = started;
                Port = port;
                Assert.True(sessions.Enforcing, "accounts are not enforcing - the auth half of the lead would not be tested");
            }
            catch
            {
                try { Directory.Delete(Root, recursive: true); } catch { }
                throw;
            }
        }

        private void MakeStream(string name, string? note = null)
        {
            var dir = Directory.CreateDirectory(Path.Combine(Media, name)).FullName;
            File.WriteAllText(Path.Combine(dir, "index.m3u8"),
                "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n"
                + "#EXT-X-PLAYLIST-TYPE:VOD\n"
                + (note is null ? "" : "# " + note + "\n")
                + "#EXTINF:6.000,\nseg_00000.ts\n#EXT-X-ENDLIST\n");
            File.WriteAllText(Path.Combine(dir, "seg_00000.ts"), "SEGMENT:" + name, new UTF8Encoding(false));
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }

        /// <summary>
        /// GET with the request target written byte for byte as given. The
        /// target is ASCII by construction - everything outside it is
        /// percent-encoded, as a browser sends it.
        /// </summary>
        public async Task<(int Status, string Body)> Get(string target)
        {
            Assert.True(target.All(c => c > ' ' && c < 0x7f), $"request target is not plain ASCII: {target}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Port, timeout.Token);
            var stream = client.GetStream();
            var request = $"GET {target} HTTP/1.1\r\nHost: 127.0.0.1:{Port}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
            using var received = new MemoryStream();
            await stream.CopyToAsync(received, timeout.Token);

            var text = Encoding.UTF8.GetString(received.ToArray());
            var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var statusLine = text.Split("\r\n", 2)[0];
            var pieces = statusLine.Split(' ', 3);
            Assert.True(pieces.Length >= 2 && int.TryParse(pieces[1], out _), $"no status line in: {text}");
            return (int.Parse(pieces[1]), headEnd < 0 ? "" : text[(headEnd + 4)..]);
        }

        public void Dispose()
        {
            try { Server.Dispose(); } catch { }
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
