using J0kersMediaServer.Auth;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The "credentials are arriving in URLs" warning only fires where the advice
/// means something.
///
/// It used to fire for every credential in a query string, including the
/// dashboard's own EventSource - which cannot set a header, reopens every
/// twenty seconds per open page, and so produced about six warnings a minute
/// telling the reader to do something impossible. 537 lines of it in one
/// install's logs, every one self-inflicted, and a third-party script really
/// putting a key in a URL would have been lost among them.
///
/// This is only about the warning. The credential is read and honoured on
/// every path either way.
/// </summary>
public class UrlCredentialWarningTests
{
    /// The one client with no choice.
    [Fact]
    public void TheEventSourceLinkIsNotWarnedAbout()
        => Assert.False(AuthService.CanSendAHeader("/api/server/session"));

    [Fact]
    public void TheExemptionIsCaseInsensitive()
        => Assert.False(AuthService.CanSendAHeader("/API/Server/Session"));

    /// Everything else could have sent a header, so a credential in its URL is
    /// worth a line.
    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/browse")]
    [InlineData("/api/play")]
    [InlineData("/api/sessions")]
    [InlineData("/api/users")]
    [InlineData("/")]
    public void EveryOtherPathIsStillWarnedAbout(string path)
        => Assert.True(AuthService.CanSendAHeader(path));

    /// Exact paths only: a lookalike must not inherit the exemption, or a
    /// caller could silence the warning by inventing a path.
    [Theory]
    [InlineData("/api/server/session/extra")]
    [InlineData("/api/server/sessions")]
    [InlineData("/api/server")]
    public void LookalikePathsDoNotInheritTheExemption(string path)
        => Assert.True(AuthService.CanSendAHeader(path));
}
