using System.Net;
using J0kersMediaServer.Services;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// These are security answers, not conveniences. Inbound, a wrong "true" opens
/// unauthenticated DLNA to the internet; outbound, a wrong "false" lets a
/// third-party playlist point the fetcher at the control port or at a cloud
/// metadata service. The boundary cases either side of each range are the ones
/// that matter, so they are all named here rather than sampled.
///
/// Nothing in this file resolves a name. Every case is an IP literal, or one
/// of the two suffixes the code answers without asking DNS, so the suite gives
/// the same answer on a machine with no network as on one with a resolver that
/// happens to have an opinion about the name being tested.
/// </summary>
public class PrivateNetworkTests
{
    [Theory]
    // loopback, both families
    [InlineData("127.0.0.1", true)]
    [InlineData("127.255.255.254", true)]
    [InlineData("::1", true)]
    // RFC 1918 class A
    [InlineData("10.0.0.1", true)]
    [InlineData("10.255.255.255", true)]
    // RFC 1918 class B is 172.16 through 172.31 and nothing either side of it.
    // This is the range that gets implemented as "172.anything" by accident.
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.32.0.1", false)]
    // RFC 1918 class C
    [InlineData("192.168.0.1", true)]
    [InlineData("192.168.255.255", true)]
    [InlineData("192.167.1.1", false)]
    [InlineData("192.169.1.1", false)]
    // link-local, which is where the cloud metadata address lives
    [InlineData("169.254.1.1", true)]
    [InlineData("169.253.1.1", false)]
    // carrier-grade NAT, 100.64 through 100.127
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.1", false)]
    // "this network"
    [InlineData("0.0.0.0", true)]
    // ordinary public addresses
    [InlineData("93.184.216.34", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2606:4700:4700::1111", false)]
    // IPv6 unique-local is fc00::/7, so both fc and fd are inside it
    [InlineData("fc00::1", true)]
    [InlineData("fd12:3456:789a::1", true)]
    [InlineData("fe80::1", true)]
    // An IPv4-mapped IPv6 address has to be judged by the IPv4 address inside
    // it, otherwise ::ffff:10.0.0.1 reaches the LAN through the front door.
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("::ffff:93.184.216.34", false)]
    public void IsPrivate_classifies_the_range(string address, bool expected) =>
        Assert.Equal(expected, PrivateNetwork.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public void IsPrivate_of_no_address_is_not_private() =>
        Assert.False(PrivateNetwork.IsPrivate(null));

    [Theory]
    [InlineData("169.254.169.254", true)]
    [InlineData("fd00:ec2::254", true)]
    // the rest of link-local is an ordinary LAN address, not the metadata service
    [InlineData("169.254.169.253", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("10.0.0.1", false)]
    public void IsCloudMetadata_matches_only_the_metadata_addresses(string address, bool expected) =>
        Assert.Equal(expected, PrivateNetwork.IsCloudMetadata(IPAddress.Parse(address)));

    [Fact]
    public void IsCloudMetadata_of_no_address_is_false() =>
        Assert.False(PrivateNetwork.IsCloudMetadata(null));

    [Theory]
    [InlineData("http://93.184.216.34/playlist.m3u8")]
    [InlineData("https://93.184.216.34/playlist.m3u8")]
    [InlineData("https://8.8.8.8:8443/a/b?c=d")]
    public void MayFetch_allows_a_public_host(string url)
    {
        Assert.True(PrivateNetwork.MayFetch(url, out var reason), reason);
        Assert.Equal("", reason);
    }

    [Theory]
    // file: would be a local disk read dressed up as a download
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://93.184.216.34/x")]
    public void MayFetch_refuses_a_scheme_that_is_not_http(string url)
    {
        Assert.False(PrivateNetwork.MayFetch(url, out var reason));
        Assert.Contains("not a fetchable scheme", reason);
    }

    [Theory]
    [InlineData("/just/a/path")]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData(null)]
    public void MayFetch_refuses_anything_that_is_not_an_absolute_url(string? url)
    {
        Assert.False(PrivateNetwork.MayFetch(url, out var reason));
        Assert.Equal("not an absolute URL", reason);
    }

    [Theory]
    // the control port on this very machine - the whole point of the check
    [InlineData("http://127.0.0.1:9090/api/streams")]
    [InlineData("http://localhost:9090/api/streams")]
    [InlineData("http://10.0.0.5/playlist.m3u8")]
    [InlineData("http://192.168.1.10:8080/stream.m3u8")]
    [InlineData("http://172.16.4.4/x")]
    // the cloud metadata service, which hands out credentials to any caller
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    // mDNS: a .local name is this network by definition, without asking DNS
    [InlineData("http://nas.local/media/x.m3u8")]
    // an IPv6 literal in a URL arrives wrapped in brackets and still has to
    // be recognised as unique-local
    [InlineData("http://[fd12:3456:789a::1]/x.m3u8")]
    public void MayFetch_refuses_a_host_inside_this_network(string url)
    {
        Assert.False(PrivateNetwork.MayFetch(url, out var reason));
        Assert.Contains("inside this network", reason);
    }
}
