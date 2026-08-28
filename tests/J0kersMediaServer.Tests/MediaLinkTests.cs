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

    public MediaLinkTests(MediaLinkFixture fixture) => _link = fixture.Link;

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
