using System.Security.Cryptography;
using System.Text;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Auth;

/// <summary>
/// Capability tokens for media URLs.
///
/// A media player is not a browser: VLC, a Chromecast, a smart TV and a
/// bare &lt;video&gt; element can all fetch a URL and nothing else — no
/// custom header, no login form, often no cookie. Requiring any of those on
/// a playlist or a segment simply breaks playback. So media is authorized
/// the one way every client understands: by what's in the URL.
///
/// A token is <c>exp=&lt;unix&gt;&amp;sig=&lt;HMAC-SHA256&gt;</c> over the
/// scope and the expiry, keyed by a per-install secret. It carries no
/// identity and grants nothing but playback, it expires, and — unlike
/// putting an account key in the query string — leaking one costs a few
/// hours of access to one stream rather than the whole server forever.
///
/// Two scopes: a single stream name (a share or cast link) and
/// <see cref="AllStreams"/> (the dashboard's own session, which lists and
/// plays everything). Verification tries the stream first, then the
/// wildcard, so neither has to be named in the URL.
/// </summary>
public sealed class MediaLink
{
    /// <summary>Scope covering every stream — what the dashboard signs with.</summary>
    public const string AllStreams = "*";

    private readonly byte[] _secret;

    public MediaLink(string baseDirectory)
    {
        _secret = LoadOrCreateSecret(Path.Combine(baseDirectory, "signing.key"));
    }

    /// <summary>
    /// The signing secret outlives the process: tokens minted before a
    /// restart keep working, so a tray-app restart mid-movie doesn't stop
    /// playback. Sessions deliberately don't survive; playback links do.
    /// </summary>
    private static byte[] LoadOrCreateSecret(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var existing = Convert.FromBase64String(File.ReadAllText(path).Trim());
                // a key written by an older build inherited the folder's
                // permissions; tighten it on the way past
                if (existing.Length >= 32) { Services.SecretFile.Protect(path); return existing; }
                Log.Warn("media", "signing.key was too short — generating a new one");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("media", $"could not read signing.key ({ex.Message}) — generating a new one");
        }

        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            File.WriteAllText(path, Convert.ToBase64String(secret));
            // anyone holding this can mint a playable link for any stream
            Services.SecretFile.Protect(path);
        }
        catch (Exception ex)
        {
            // an unwritable config directory shouldn't stop the server; the
            // cost is that links don't survive a restart
            Log.Warn("media", $"could not save signing.key ({ex.Message}) — media links won't survive a restart");
        }
        return secret;
    }

    /// <summary>Mints a token for a scope. Returns the bare query string, no leading '?'.</summary>
    public string Sign(string scope, TimeSpan lifetime)
    {
        var exp = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        return $"exp={exp}&sig={Signature(scope, exp)}";
    }

    /// <summary>
    /// True when the URL's token authorizes this stream. Checked against
    /// the stream's own scope first, then the all-streams scope.
    /// </summary>
    public bool Verify(string stream, string? exp, string? sig)
    {
        if (string.IsNullOrEmpty(exp) || string.IsNullOrEmpty(sig)) return false;
        if (!long.TryParse(exp, out var expiry)) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expiry) <= DateTimeOffset.UtcNow) return false;

        return Matches(stream, expiry, sig) || Matches(AllStreams, expiry, sig);
    }

    /// <summary>
    /// Signs something the TV proxy is allowed to act on — an upstream URL it
    /// may fetch, or a "tv:provider:channel" capability naming one channel.
    ///
    /// The proxy takes its target from the query string, which without this
    /// would make it an open relay: anyone who could reach the control port
    /// could have the server fetch arbitrary URLs on their behalf, including
    /// hosts only it can see. A signature means the only targets it accepts
    /// are ones it generated itself.
    ///
    /// These deliberately don't expire. They authorize a public free-TV
    /// channel and nothing else, and the restreaming ffmpeg process holding
    /// one has no way to refresh it.
    /// </summary>
    public string SignUrl(string target) =>
        UserStore.Base64Url(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes("url\n" + target)));

    /// <summary>True when this target carries a signature this install minted.</summary>
    public bool VerifyUrl(string target, string? sig)
    {
        if (string.IsNullOrEmpty(sig)) return false;
        // A forged relay target always contained a newline - "{target}#\n{exp}",
        // from a stream token minted over a crafted scope - and proxy
        // signatures never expire, so every forgery minted before the signing
        // changed would otherwise keep working for ever. Refusing that one
        // character retires them all without changing a byte of what SignUrl
        // produces, so pinned channels, saved with its signature, keep working.
        //
        // The newline and nothing wider. A first version refused every
        // control character, and review found real channels it broke: an M3U
        // provider's tvg-id can carry a tab, or a Latin-1 byte decoded as a C1
        // control, and those pinned restreams would have answered 401 after
        // the upgrade. An M3U id can never contain a newline - the playlist is
        // split on them - and a Pluto id is hex.
        if (target.Contains('\n')) return false;
        var expected = Encoding.ASCII.GetBytes(SignUrl(target));
        var actual = Encoding.ASCII.GetBytes(sig);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private bool Matches(string scope, long exp, string presented)
    {
        var actual = Encoding.ASCII.GetBytes(presented);
        return Same(Signature(scope, exp), actual)
            // Tokens minted before the signing gained its "stream\n" prefix:
            // share links, a PVR's M3U playlist, a player tab left open across
            // the upgrade. Refusing them would break every one at once, and
            // gains nothing - the forgery needed the ability to MINT this form
            // over a chosen scope, and nothing mints it any more. They expire
            // on their own schedule like any other token.
            || Same(LegacySignature(scope, exp), actual);

        static bool Same(string expectedSig, byte[] actual)
        {
            var expected = Encoding.ASCII.GetBytes(expectedSig);
            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
    }

    /// <summary>The form every token had before the prefix. Verified, never minted.</summary>
    private string LegacySignature(string scope, long exp) =>
        UserStore.Base64Url(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes($"{scope}\n{exp}")));

    /// <summary>
    /// A stream token's MAC. Prefixed with "stream\n" so that no scope, however
    /// it is spelled, can produce the same bytes as a proxy signature, which
    /// is prefixed "url\n" (see <see cref="SignUrl"/>). Both come from the same
    /// key, so the prefixes are the only thing keeping them apart.
    ///
    /// They used not to be apart. This hashed "{scope}\n{exp}" and SignUrl
    /// hashes "url\n{target}", so a scope of "url\n{X}" produced exactly the
    /// proxy signature for "{X}\n{exp}" - and GET /api/media/token signs any
    /// scope a read account asks for. With a '#' at the end of X the tail lands
    /// in the URL fragment and is never sent, so the forged target fetches
    /// cleanly, and proxy signatures never expire: a read account could mint a
    /// permanent, anonymous open relay through this server.
    ///
    /// SignUrl is the half that stays as it was. Pinned channels are saved with
    /// its signature in their URL, and changing it would break every one of
    /// them; stream tokens are short-lived and simply re-minted. The cost of
    /// this change is that tokens and share links minted before it stop
    /// working and have to be issued again.
    /// </summary>
    private string Signature(string scope, long exp)
    {
        var mac = HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes($"stream\n{scope}\n{exp}"));
        return UserStore.Base64Url(mac);
    }
}
