using System.Net;
using System.Net.Http;
using J0kersMediaServer.Services;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The check that cannot be raced.
///
/// MayFetch resolves a name to judge it and HttpClient resolves it again to
/// connect, which leaves a gap: a name the attacker controls can answer
/// publicly for the first lookup and with a loopback address for the second.
/// GuardPrivateAddresses closes it by refusing at the moment of connection,
/// on the address actually being dialled - so these tests go through a real
/// HttpClient rather than calling the predicate, because what is being
/// asserted is that the socket never opens.
/// </summary>
public sealed class PrivateAddressGuardTests
{
    private static HttpClient Guarded() =>
        new(PrivateNetwork.GuardPrivateAddresses(new SocketsHttpHandler()))
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

    [Theory]
    [InlineData("http://127.0.0.1/")]        // the server's own ports
    [InlineData("http://169.254.169.254/")]  // cloud instance metadata
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://172.16.0.1/")]
    public async Task A_private_address_is_refused_at_the_socket(string url)
    {
        using var http = Guarded();

        // No listener is needed on any of these: the point is that the
        // connection is refused before it is attempted, so the test asserts
        // on the reason rather than on nothing having answered.
        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => http.GetAsync(url));

        Assert.Contains("inside this network", Flatten(ex), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A literal public address still connects - or fails for some reason
    /// other than this guard. The guard must refuse what is private, not
    /// everything: a check that blocks all traffic passes the test above and
    /// breaks every channel in the product.
    /// </summary>
    [Fact]
    public async Task A_public_address_is_not_refused_by_the_guard()
    {
        using var http = Guarded();
        // TEST-NET-1 (RFC 5737): routable as far as this code is concerned,
        // guaranteed never to actually answer, and never to be someone's real
        // server - so this test neither depends on the internet nor touches
        // anybody when it runs.
        try
        {
            await http.GetAsync("http://192.0.2.1/");
        }
        catch (Exception ex)
        {
            Assert.DoesNotContain("inside this network", Flatten(ex), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Flatten(Exception? ex)
    {
        var text = "";
        for (var e = ex; e is not null; e = e.InnerException) text += e.Message + " | ";
        return text;
    }
}
