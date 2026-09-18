using J0kersMediaServer.Auth;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// One MediaLink for the whole class, over one temporary directory.
///
/// Constructing it writes a signing.key and then tightens that file's
/// permissions, which on Windows means running icacls. Sharing the instance
/// keeps that to once for the class instead of once per test, and every test
/// here wants the same secret anyway - a token minted in one test being
/// checked against the same key is the point.
/// </summary>
public sealed class MediaLinkFixture : IDisposable
{
    private readonly TempDir _dir = new();

    public MediaLink Link { get; }

    /// <summary>Where the signing.key lives, for the test that pins the proxy signature's format.</summary>
    public string Dir => _dir.Path;

    public MediaLinkFixture() => Link = new MediaLink(_dir.Path);

    public void Dispose() => _dir.Dispose();
}

/// <summary>
/// A media token is the only credential a smart TV or a bare video element can
/// carry, so what it grants has to be exactly what it says: this stream, until
/// this moment, and nothing else. Every test below is one way of getting that
/// wrong that would not be visible from the outside - a signature that verifies
/// for the wrong stream, or an expiry nobody checks, still plays perfectly.
/// </summary>
public class MediaLinkTests : IClassFixture<MediaLinkFixture>
{
    private readonly MediaLink _link;

    private readonly string _dir;

    public MediaLinkTests(MediaLinkFixture fixture)
    {
        _link = fixture.Link;
        _dir = fixture.Dir;
    }

    /// <summary>
    /// Stream tokens and TV-proxy signatures are made with the same key, and
    /// used to hash inputs of the same shape: "{scope}\n{exp}" and
    /// "url\n{target}". So a stream scope of "url\n{X}" produced exactly the
    /// proxy signature for "{X}\n{exp}" - and GET /api/media/token will sign
    /// any scope a read account asks for. A "#" at the end of X puts the
    /// "\n{exp}" tail into the URL fragment, which is never sent, so the
    /// forged target fetches cleanly. Proxy signatures never expire.
    ///
    /// Nothing about either input may be able to impersonate the other.
    /// </summary>
    [Fact]
    public void A_stream_token_cannot_be_turned_into_a_proxy_signature()
    {
        const string target = "http://attacker.example/p.m3u8#";
        var (exp, sig) = Parse(_link.Sign("url\n" + target, TimeSpan.FromHours(1)));

        Assert.False(_link.VerifyUrl(target + "\n" + exp, sig),
                     "a stream token's signature verified as a TV-proxy signature");
    }

    /// <summary>
    /// The other side of that fix, which must not move: pinned channels are
    /// saved with a proxy signature in their URL and restreamed from it by an
    /// ffmpeg process that cannot refresh it. Changing how SignUrl hashes
    /// would break every saved channel at once. Pinned to the exact bytes.
    /// </summary>
    [Fact]
    public void Proxy_signatures_keep_their_format_so_saved_channel_links_survive()
    {
        var secret = Convert.FromBase64String(File.ReadAllText(Path.Combine(_dir, "signing.key")).Trim());
        var expected = UserStore.Base64Url(
            System.Security.Cryptography.HMACSHA256.HashData(secret, System.Text.Encoding.UTF8.GetBytes("url\ntv:pluto:abc")));

        Assert.Equal(expected, _link.SignUrl("tv:pluto:abc"));
    }

    /// <summary>
    /// A share link, a PVR's M3U playlist, a player tab left open: all carry a
    /// token minted before the signing changed. Rejecting them would break
    /// every one at the upgrade, and nothing is gained by it - the forgery
    /// needed the ability to MINT the old form over a chosen scope, and
    /// nothing mints it any more.
    /// </summary>
    [Fact]
    public void A_token_minted_before_the_upgrade_still_plays()
    {
        var secret = Convert.FromBase64String(File.ReadAllText(Path.Combine(_dir, "signing.key")).Trim());
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var legacy = UserStore.Base64Url(System.Security.Cryptography.HMACSHA256.HashData(
            secret, System.Text.Encoding.UTF8.GetBytes("vod-some-film-1a2b3c4d\n" + exp)));

        Assert.True(_link.Verify("vod-some-film-1a2b3c4d", exp.ToString(), legacy),
                    "a token issued before the upgrade no longer plays");
    }

    /// <summary>
    /// The other half of the upgrade: relay signatures forged BEFORE it. They
    /// are real proxy signatures over "{target}#\n{exp}" - that is what the
    /// forgery produced - and proxy signatures never expire, so changing how
    /// stream tokens are signed does nothing to the ones already out there.
    ///
    /// No genuine proxy target contains a control character: they are URLs
    /// and "tv:provider:channel" names. Refusing one retires every past
    /// forgery without changing a byte of what SignUrl produces, so pinned
    /// channels keep working.
    /// </summary>
    [Fact]
    public void A_relay_forged_before_the_upgrade_is_refused()
    {
        const string forged = "http://attacker.example/p.m3u8#\n1789999999";
        Assert.False(_link.VerifyUrl(forged, _link.SignUrl(forged)),
                     "a proxy target containing a newline was accepted - every relay forged before the upgrade still works");

        // and an ordinary target still verifies
        Assert.True(_link.VerifyUrl("tv:pluto:abc", _link.SignUrl("tv:pluto:abc")));
    }

    /// <summary>
    /// Real channels that must survive the upgrade. An M3U provider's tvg-id
    /// can carry a tab, or a Latin-1 byte that decodes as a C1 control. A first
    /// version of the fix refused every control character in a proxy target,
    /// and every such pinned channel would have answered 401 afterwards. Only
    /// the newline the forgery needed is refused.
    /// </summary>
    [Theory]
    [InlineData("tv:m3u:ABC\tHD")]
    [InlineData("tv:m3u:News1")]
    public void A_pinned_channel_whose_id_has_a_control_character_still_verifies(string target)
    {
        Assert.True(_link.VerifyUrl(target, _link.SignUrl(target)),
                    "a genuine pinned channel's signature stopped verifying");
    }

    /// <summary>
    /// The first defence layer, pinned on its own. The forgery tests above are
    /// also caught by the newline check, so without this a revert of the
    /// "stream\n" prefix would leave every test here green.
    /// </summary>
    [Fact]
    public void Stream_tokens_are_signed_with_their_own_prefix()
    {
        var secret = Convert.FromBase64String(File.ReadAllText(Path.Combine(_dir, "signing.key")).Trim());
        var (exp, sig) = Parse(_link.Sign("vod-some-film-1a2b3c4d", TimeSpan.FromHours(1)));

        var prefixed = UserStore.Base64Url(System.Security.Cryptography.HMACSHA256.HashData(
            secret, System.Text.Encoding.UTF8.GetBytes("stream\nvod-some-film-1a2b3c4d\n" + exp)));
        var legacy = UserStore.Base64Url(System.Security.Cryptography.HMACSHA256.HashData(
            secret, System.Text.Encoding.UTF8.GetBytes("vod-some-film-1a2b3c4d\n" + exp)));

        Assert.Equal(prefixed, sig);
        Assert.NotEqual(legacy, sig);
    }

    /// <summary>Splits the "exp=...&amp;sig=..." query string Sign hands back.</summary>
    private static (string Exp, string Sig) Parse(string token)
    {
        var parts = token.Split('&');
        Assert.Equal(2, parts.Length);
        Assert.StartsWith("exp=", parts[0], StringComparison.Ordinal);
        Assert.StartsWith("sig=", parts[1], StringComparison.Ordinal);
        return (parts[0][4..], parts[1][4..]);
    }

    [Fact]
    public void A_freshly_signed_token_verifies_for_its_own_stream()
    {
        var (exp, sig) = Parse(_link.Sign("vod-skyfall-2012", TimeSpan.FromHours(1)));
        Assert.True(_link.Verify("vod-skyfall-2012", exp, sig));
    }

    [Fact]
    public void A_tampered_signature_does_not_verify()
    {
        var (exp, sig) = Parse(_link.Sign("vod-skyfall-2012", TimeSpan.FromHours(1)));

        // one character changed, same length: the comparison is fixed-time and
        // length-checked, so this is what a real forgery attempt looks like
        var tampered = (sig[0] == 'A' ? 'B' : 'A') + sig[1..];
        Assert.NotEqual(sig, tampered);
        Assert.False(_link.Verify("vod-skyfall-2012", exp, tampered));
    }

    [Fact]
    public void Pushing_the_expiry_out_without_resigning_does_not_verify()
    {
        var (exp, sig) = Parse(_link.Sign("vod-skyfall-2012", TimeSpan.FromHours(1)));

        // The expiry is signed along with the scope, so a client that simply
        // edits the number in the URL invalidates the token rather than
        // extending it. Without that, every link would be permanent.
        var extended = (long.Parse(exp) + 86_400).ToString();
        Assert.False(_link.Verify("vod-skyfall-2012", extended, sig));
    }

    [Fact]
    public void An_expired_token_does_not_verify()
    {
        // signed correctly, for a moment that has already passed
        var (exp, sig) = Parse(_link.Sign("vod-skyfall-2012", TimeSpan.FromHours(-1)));
        Assert.False(_link.Verify("vod-skyfall-2012", exp, sig));
    }

    [Fact]
    public void A_token_for_one_stream_does_not_verify_for_another()
    {
        var (exp, sig) = Parse(_link.Sign("vod-skyfall-2012", TimeSpan.FromHours(1)));

        // a share link handed to a guest must not turn into a key for the
        // rest of the library
        Assert.False(_link.Verify("vod-batman-begins-2005", exp, sig));
    }

    [Fact]
    public void An_all_streams_token_verifies_for_any_stream()
    {
        // what the dashboard's own session signs with: it lists and plays
        // everything, so naming one stream in every URL would be pointless
        var (exp, sig) = Parse(_link.Sign(MediaLink.AllStreams, TimeSpan.FromHours(1)));

        Assert.True(_link.Verify("vod-skyfall-2012", exp, sig));
        Assert.True(_link.Verify("vod-batman-begins-2005", exp, sig));
    }

    [Theory]
    [InlineData(null, "anything")]
    [InlineData("", "anything")]
    [InlineData("1893456000", null)]
    [InlineData("1893456000", "")]
    // an expiry that is not a number at all
    [InlineData("tomorrow", "anything")]
    public void A_token_that_is_missing_or_malformed_does_not_verify(string? exp, string? sig) =>
        Assert.False(_link.Verify("vod-skyfall-2012", exp, sig));

    [Fact]
    public void A_signature_of_the_wrong_length_does_not_verify()
    {
        var (exp, _) = Parse(_link.Sign("vod-skyfall-2012", TimeSpan.FromHours(1)));
        Assert.False(_link.Verify("vod-skyfall-2012", exp, "short"));
    }
}
