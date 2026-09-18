using System.Net;
using System.Text.Json;
using J0kersMediaServer.Control;
using Xunit;
using TestServer = J0kersMediaServer.Tests.ShutdownOnCloseTests.TestServer;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The access-control findings from the 2026-09-17 audit, each driven through
/// the real server exactly as the audit's trigger describes. Every test here
/// was run against the code before its fix and seen to fail for its reason.
/// </summary>
public class SecurityTests
{
    private const string OwnerPass = "test-admin-passphrase-9";   // what SignedInAs claims the server with
    private const string UserPass = "test-user-passphrase-9";

    // ---------------------------------------------------------------- helpers

    /// <summary>Signs in through the form's endpoint and returns the session cookie (and the device key, if asked).</summary>
    private static async Task<(string cookie, string? key)> SignIn(TestServer server, string username, string? password,
                                                                    bool remember = false, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = TestServer.Json(new { username, password, remember }),
        };
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var r = await server.Http.SendAsync(request);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"sign-in as {username} failed: {(int)r.StatusCode} {body}");
        Assert.True(r.Headers.TryGetValues("Set-Cookie", out var cookies), "sign-in set no cookie");
        var cookie = cookies!.Select(c => c.Split(';')[0]).First(c => c.Contains('=') && !c.EndsWith('='));
        string? key = null;
        if (JsonDocument.Parse(body).RootElement.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String)
            key = k.GetString();
        return (cookie, key);
    }

    /// <summary>A client that authenticates with a session cookie only, as a browser tab does.</summary>
    private static HttpClient CookieClient(TestServer server, string cookie)
    {
        var c = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
        {
            BaseAddress = server.Http.BaseAddress,
            Timeout = TimeSpan.FromSeconds(20),
        };
        c.DefaultRequestHeaders.Add("Cookie", cookie);
        c.DefaultRequestHeaders.Add("X-J0kers-CSRF", "1");
        return c;
    }

    private static async Task<string> CreateUser(HttpClient admin, object body)
    {
        using var r = await admin.PostAsync("api/users", TestServer.Json(body));
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"could not create the account: {(int)r.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("user").GetProperty("id").GetString()!;
    }

    private static async Task<HttpStatusCode> Me(HttpClient client)
    {
        using var r = await client.GetAsync("api/auth/me");
        return r.StatusCode;
    }

    // ------------------------------------------------------------------ tests

    /// <summary>
    /// A guest signs in while the account is passwordless; the administrator
    /// then unticks passwordless to close it. Turning it ON ended sessions;
    /// turning it OFF did not - so the guest's session carried on, into an
    /// account now with no password at all, where it could set one of its own.
    /// </summary>
    [Fact]
    public async Task Closing_a_passwordless_account_ends_the_sessions_it_let_in()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var admin = await server.SignedInAs("admin");
        var id = await CreateUser(admin, new { username = "visitor", role = "read", passwordless = true });

        var (cookie, _) = await SignIn(server, "visitor", null);
        using var guest = CookieClient(server, cookie);
        Assert.Equal(HttpStatusCode.OK, await Me(guest));

        using (var close = await admin.PutAsync($"api/users?id={id}", TestServer.Json(new { passwordless = false })))
            Assert.True(close.IsSuccessStatusCode, $"could not close the account: {(int)close.StatusCode}");

        Assert.True(await Me(guest) == HttpStatusCode.Unauthorized,
                    "the guest's session outlived the account being closed");
    }

    /// <summary>
    /// The takeover itself: an account with no password skipped the "current
    /// password" check, because there was nothing to check it against - so any
    /// session on it could set a password the administrator did not know.
    /// </summary>
    [Fact]
    public async Task An_account_with_no_password_cannot_give_itself_one()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var admin = await server.SignedInAs("admin");
        await CreateUser(admin, new { username = "visitor", role = "read", passwordless = true });

        var (cookie, _) = await SignIn(server, "visitor", null);
        using var guest = CookieClient(server, cookie);
        using var r = await guest.PostAsync("api/auth/password", TestServer.Json(new { newPassword = "chosen-by-the-guest-1" }));
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.Forbidden,
                    $"an account with no password set one for itself: {(int)r.StatusCode} {body}");
    }

    /// <summary>
    /// "Remember this device" hands back a key alongside the session. Revoking
    /// that key - the page promises "anything using it stops working
    /// immediately" - left the session running for up to a week.
    /// </summary>
    [Fact]
    public async Task Revoking_a_device_key_ends_the_session_it_came_with()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var admin = await server.SignedInAs("admin");
        await CreateUser(admin, new { username = "reader", password = UserPass, role = "read" });

        var (cookie, key) = await SignIn(server, "reader", UserPass, remember: true);
        Assert.False(string.IsNullOrEmpty(key), "remember=true returned no key");
        using var browser = CookieClient(server, cookie);
        Assert.Equal(HttpStatusCode.OK, await Me(browser));

        // The same browser on its next visit: the sign-in page cashes the
        // stored key in for a fresh session.
        string traded;
        using (var trade = new HttpRequestMessage(HttpMethod.Post, "api/auth/session"))
        {
            trade.Headers.Add("X-Api-Key", key);
            using var r = await server.Http.SendAsync(trade);
            Assert.True(r.IsSuccessStatusCode, $"could not trade the key for a session: {(int)r.StatusCode}");
            traded = r.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).First(c => !c.EndsWith('='));
        }
        using var nextVisit = CookieClient(server, traded);
        Assert.Equal(HttpStatusCode.OK, await Me(nextVisit));

        // And a session that never had anything to do with the key.
        var (other, _) = await SignIn(server, "reader", UserPass);
        using var elsewhere = CookieClient(server, other);

        var keyId = key!["jmk_".Length..].Split('_')[0];
        using (var revoke = await browser.DeleteAsync($"api/auth/keys?id={keyId}"))
            Assert.True(revoke.IsSuccessStatusCode, $"could not revoke the key: {(int)revoke.StatusCode}");

        Assert.True(await Me(browser) == HttpStatusCode.Unauthorized,
                    "the session kept working after the key it came with was revoked");
        Assert.True(await Me(nextVisit) == HttpStatusCode.Unauthorized,
                    "the session traded for the key kept working after it was revoked");
        Assert.True(await Me(elsewhere) == HttpStatusCode.OK,
                    "revoking one device's key signed out a session that had nothing to do with it");
    }

    /// <summary>
    /// Rotating a leaked legacy token: the settings page saved the new one and
    /// the server went on honouring only the value it read at startup - the
    /// leaked token kept working, the new one did not, until a restart.
    /// </summary>
    [Fact]
    public async Task Changing_the_legacy_token_takes_effect_at_once()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var admin = await server.SignedInAs("admin");

        async Task<HttpStatusCode> With(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/status");
            request.Headers.Add("Authorization", "Bearer " + token);
            using var r = await server.Http.SendAsync(request);
            return r.StatusCode;
        }

        using (var one = await admin.PostAsync("api/settings", TestServer.Json(new { authToken = "legacy-token-one-0123456789" })))
            Assert.True(one.IsSuccessStatusCode, "could not set the token");
        Assert.True(await With("legacy-token-one-0123456789") == HttpStatusCode.OK, "a newly set token was not honoured");

        using (var two = await admin.PostAsync("api/settings", TestServer.Json(new { authToken = "legacy-token-two-0123456789" })))
            Assert.True(two.IsSuccessStatusCode, "could not rotate the token");
        Assert.True(await With("legacy-token-one-0123456789") == HttpStatusCode.Unauthorized, "the rotated-out token still works");
        Assert.Equal(HttpStatusCode.OK, await With("legacy-token-two-0123456789"));

        // and turning it off, which is what a leaked token calls for
        using (var off = await admin.PostAsync("api/settings", TestServer.Json(new { authToken = "" })))
            Assert.True(off.IsSuccessStatusCode, "could not clear the token");
        Assert.True(await With("legacy-token-two-0123456789") == HttpStatusCode.Unauthorized,
                    "the token was cleared and still works");
    }

    /// <summary>
    /// GET /api/config hid the token by writing "***" into the live config for
    /// the length of the read. Once the token was read live, "***" was the
    /// admin token while anybody read the config - and two reads at once could
    /// each put back the other's "***", leaving it that way for good.
    /// </summary>
    [Fact]
    public async Task Reading_the_config_never_changes_the_token()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var admin = await server.SignedInAs("admin");
        const string token = "legacy-token-real-0123456789";
        using (var set = await admin.PostAsync("api/settings", TestServer.Json(new { authToken = token })))
            Assert.True(set.IsSuccessStatusCode, "could not set the token");

        async Task<HttpStatusCode> With(string t)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/status");
            request.Headers.Add("Authorization", "Bearer " + t);
            using var r = await server.Http.SendAsync(request);
            return r.StatusCode;
        }

        async Task<string> ReadConfig()
        {
            using var r = await admin.GetAsync("api/config");
            return await r.Content.ReadAsStringAsync();
        }

        var reads = Enumerable.Range(0, 80).Select(_ => ReadConfig()).ToArray();
        var probes = Enumerable.Range(0, 80).Select(_ => With("***")).ToArray();
        await Task.WhenAll(reads);
        var answers = await Task.WhenAll(probes);

        Assert.True(!answers.Contains(HttpStatusCode.OK),
                    $"\"***\" was accepted as the admin token {answers.Count(a => a == HttpStatusCode.OK)} time(s) "
                    + "while the config was being read");
        Assert.True(await With(token) == HttpStatusCode.OK, "after the config was read, the real token no longer works");
        Assert.Equal(HttpStatusCode.Unauthorized, await With("***"));
        // and the read still hides it
        var config = await ReadConfig();
        Assert.DoesNotContain(token, config);
        Assert.Contains("\"***\"", config);
    }

    /// <summary>
    /// Device paths that are not a drive: "\??\UNC\host\share" resolves to a
    /// network share through the object manager, and the check for UNC only
    /// looked for a leading "\\". Opening it makes the server authenticate to
    /// the host over SMB - its NTLM response, handed to whoever named it.
    /// </summary>
    [Theory]
    [InlineData(@"\??\UNC\attacker-host\share\x", false)]
    [InlineData(@"\??\C:\Windows", false)]
    [InlineData(@"\\?\UNC\attacker-host\share", false)]
    [InlineData(@"\\?\C:\Windows", false)]
    [InlineData(@"\\.\pipe\x", false)]
    [InlineData(@"\\attacker-host\share", false)]
    [InlineData(@"//attacker-host/share", false)]
    [InlineData(@"C:\Users\someone\Videos\film.mkv", true)]
    [InlineData(@"D:\", true)]
    public void Only_ordinary_drive_paths_are_local(string path, bool local)
    {
        if (!OperatingSystem.IsWindows()) return;   // the rule is about Windows device paths
        Assert.Equal(local, ControlApi.TryLocalPath(path, out _));
    }

    /// <summary>
    /// "same-site" is not "same-origin": it ignores the port, so any page served
    /// by another program on this machine passed the cross-site check. On a
    /// fresh server, before an account exists, that was enough to create the
    /// first administrator and lock the owner out.
    /// </summary>
    [Fact]
    public async Task A_page_on_another_port_cannot_claim_the_server()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);

        using (var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/setup")
               {
                   Content = TestServer.Json(new { username = "intruder", password = "intruder-passphrase-1" }),
               })
        {
            request.Headers.Add("Sec-Fetch-Site", "same-site");
            using var r = await server.Http.SendAsync(request);
            Assert.True(r.StatusCode == HttpStatusCode.Forbidden,
                        $"a same-site (other port) request created the first administrator: {(int)r.StatusCode}");
        }

        // An older browser sends no Sec-Fetch-Site, only Origin - which named
        // the right host and was never checked for the port.
        var otherPort = server.Port == 65000 ? 65001 : 65000;
        using (var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/setup")
               {
                   Content = TestServer.Json(new { username = "intruder", password = "intruder-passphrase-1" }),
               })
        {
            request.Headers.Add("Origin", $"http://127.0.0.1:{otherPort}");
            using var r = await server.Http.SendAsync(request);
            Assert.True(r.StatusCode == HttpStatusCode.Forbidden,
                        $"an Origin on another port created the first administrator: {(int)r.StatusCode}");
        }

        // and the server's own page still can, by either header
        using (var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/setup")
               {
                   Content = TestServer.Json(new { username = "owner", password = OwnerPass }),
               })
        {
            request.Headers.Add("Origin", $"http://127.0.0.1:{server.Port}");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            using var r = await server.Http.SendAsync(request);
            Assert.True(r.IsSuccessStatusCode, $"the server's own page could not claim it: {(int)r.StatusCode}");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
               {
                   Content = TestServer.Json(new { username = "owner", password = OwnerPass }),
               })
        {
            request.Headers.Add("Origin", $"http://127.0.0.1:{server.Port}");   // an older browser, same origin
            using var r = await server.Http.SendAsync(request);
            Assert.True(r.IsSuccessStatusCode, $"an older browser on the server's own page was refused: {(int)r.StatusCode}");
        }
    }

    /// <summary>
    /// Behind a reverse proxy on the same machine - the setup the README
    /// recommends for TLS - every request arrives from 127.0.0.1, so everyone
    /// shared one lockout. Five bad guesses from anywhere locked out the whole
    /// server, owner included.
    /// </summary>
    [Fact]
    public async Task One_client_behind_a_local_proxy_cannot_lock_everyone_out()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        (await server.SignedInAs("admin")).Dispose();

        for (var i = 0; i < 7; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
            {
                Content = TestServer.Json(new { username = "nobody", password = "wrong-guess-" + i }),
            };
            request.Headers.Add("X-Forwarded-For", "203.0.113.9");
            using var r = await server.Http.SendAsync(request);
        }

        // somebody else entirely, through the same proxy
        using var ok = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = TestServer.Json(new { username = "owner", password = OwnerPass }),
        };
        ok.Headers.Add("X-Forwarded-For", "198.51.100.7");
        using var result = await server.Http.SendAsync(ok);
        var body = await result.Content.ReadAsStringAsync();
        Assert.True(result.IsSuccessStatusCode,
                    $"one client's failed guesses locked a different client out: {(int)result.StatusCode} {body}");
    }

    /// <summary>
    /// The other half of believing the proxy's address: a proxy that passes
    /// the client's own X-Forwarded-For along lets that client name a new
    /// address for every guess, and no address ever locks. Everything through
    /// the proxy is also counted together, at a much higher limit.
    /// </summary>
    [Fact]
    public async Task A_client_that_invents_an_address_for_every_guess_is_still_stopped()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        (await server.SignedInAs("admin")).Dispose();

        for (var i = 0; i < 55; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
            {
                // a different name each time as well, or the per-name lockout stops it first
                Content = TestServer.Json(new { username = "sprayed-" + i, password = "wrong-guess" }),
            };
            request.Headers.Add("X-Forwarded-For", $"198.18.{i / 200}.{i % 200 + 1}");
            using var r = await server.Http.SendAsync(request);
        }

        using var next = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = TestServer.Json(new { username = "owner", password = OwnerPass }),
        };
        next.Headers.Add("X-Forwarded-For", "198.51.100.200");
        using var result = await server.Http.SendAsync(next);
        Assert.True((int)result.StatusCode == 429,
                    $"55 failures through the proxy, each from a made-up address, and it never locked: {(int)result.StatusCode}");
    }

    /// <summary>
    /// The throttle on "current password" first shared the sign-in counter for
    /// the account's name - which anyone can fail against. A stranger could
    /// then stop the owner, signed in and holding the right password, from
    /// changing it.
    /// </summary>
    [Fact]
    public async Task A_stranger_failing_sign_ins_cannot_stop_the_owner_changing_their_password()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        (await server.SignedInAs("admin")).Dispose();
        var (cookie, _) = await SignIn(server, "owner", OwnerPass);   // the owner's browser
        using var browser = CookieClient(server, cookie);

        for (var i = 0; i < 7; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
            {
                Content = TestServer.Json(new { username = "owner", password = "stranger-guess-" + i }),
            };
            request.Headers.Add("X-Forwarded-For", "203.0.113.77");
            using var r = await server.Http.SendAsync(request);
        }

        using var change = await browser.PostAsync("api/auth/password",
            TestServer.Json(new { currentPassword = OwnerPass, newPassword = "a-brand-new-passphrase-2" }));
        var body = await change.Content.ReadAsStringAsync();
        Assert.True(change.IsSuccessStatusCode,
                    $"the owner could not change their own password while a stranger was failing sign-ins: "
                    + $"{(int)change.StatusCode} {body}");

        // Every session ends with the old password - so the browser has to
        // be handed a new one, and it has to work. It used to be refused by
        // the stranger's lockout, leaving the owner signed out.
        var fresh = change.Headers.TryGetValues("Set-Cookie", out var set)
            ? set.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith("j0kers_session=") && !c.EndsWith('='))
            : null;
        Assert.True(fresh is not null, "the password changed and the browser was signed out, with no session to carry on");
        using var after = CookieClient(server, fresh!);
        Assert.Equal(HttpStatusCode.OK, await Me(after));
    }

    /// <summary>
    /// Changing your password hands back a fresh session. For a remembered
    /// browser that session was no longer tied to the browser's key, so after a
    /// password change, revoking that device did not sign it out.
    /// </summary>
    [Fact]
    public async Task A_remembered_browser_that_changes_its_password_still_goes_with_its_key()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var admin = await server.SignedInAs("admin");
        await CreateUser(admin, new { username = "reader", password = UserPass, role = "read" });

        var (cookie, key) = await SignIn(server, "reader", UserPass, remember: true);
        string fresh;
        using (var browser = CookieClient(server, cookie))
        using (var change = await browser.PostAsync("api/auth/password",
                   TestServer.Json(new { currentPassword = UserPass, newPassword = "reader-new-passphrase-3" })))
        {
            Assert.True(change.IsSuccessStatusCode, $"could not change the password: {(int)change.StatusCode}");
            fresh = change.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).First(c => !c.EndsWith('='));
        }
        using var after = CookieClient(server, fresh);
        Assert.Equal(HttpStatusCode.OK, await Me(after));

        var keyId = key!["jmk_".Length..].Split('_')[0];
        using (var revoke = await after.DeleteAsync($"api/auth/keys?id={keyId}"))
            Assert.True(revoke.IsSuccessStatusCode, $"could not revoke the key: {(int)revoke.StatusCode}");
        Assert.True(await Me(after) == HttpStatusCode.Unauthorized,
                    "after a password change, revoking the browser's key no longer signed it out");
    }

    /// <summary>
    /// The Origin rule, case by case. On a plain-HTTP LAN address browsers
    /// send no Sec-Fetch-Site, so this is the whole CSRF check there.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:9000", "127.0.0.1:9090", "127.0.0.1", false)]     // another port on the same host
    [InlineData("http://127.0.0.1:9090", "127.0.0.1:9090", "127.0.0.1", true)]
    [InlineData("http://evil.example:9090", "127.0.0.1:9090", "127.0.0.1", false)]
    [InlineData("http://192.168.1.10:9999", "192.168.1.10:9090", "192.168.1.20", false)]
    [InlineData("http://192.168.1.10:9090", "192.168.1.10:9090", "192.168.1.20", true)]
    [InlineData("http://[::1]:9090", "[::1]:9090", "::1", true)]
    [InlineData("http://[::1]:9000", "[::1]:9090", "::1", false)]
    [InlineData("https://media.lan", "media.lan", "192.168.1.20", true)]             // default port, direct
    [InlineData("http://media.lan:9999", "media.lan", "192.168.1.20", false)]        // Host says 80, Origin says 9999
    [InlineData("http://media.lan:8081", "media.lan", "127.0.0.1", true)]            // a local proxy dropped the port
    [InlineData("https://media.lan", "media.lan:443", "127.0.0.1", true)]
    public void An_origin_must_name_the_same_host_and_port(string origin, string host, string peer, bool same)
    {
        Assert.Equal(same, ControlApi.SameOriginAsHost(new Uri(origin), host, IPAddress.Parse(peer)));
    }

    /// <summary>
    /// "Change password" checks the current one, which is what stops a
    /// borrowed session becoming a permanent takeover - but that check was not
    /// throttled, so a borrowed session could simply guess until it was right.
    /// </summary>
    [Fact]
    public async Task Guessing_the_current_password_is_throttled()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var reader = await server.SignedInAs("read");

        var codes = new List<int>();
        for (var i = 0; i < 8; i++)
        {
            using var r = await reader.PostAsync("api/auth/password",
                TestServer.Json(new { currentPassword = "guess-number-" + i, newPassword = "a-new-passphrase-1" }));
            codes.Add((int)r.StatusCode);
        }
        Assert.True(codes.Contains(429),
                    "eight wrong guesses at the current password were never throttled: " + string.Join(",", codes));
    }

    /// <summary>
    /// The owner, the only Server Admin, picks "admin" for their own role. The
    /// last-administrator rule counted plain admins, so with a second admin
    /// around the change went through - and nobody was left who could see the
    /// log or the transcoder, or make a Server Admin again.
    /// </summary>
    [Fact]
    public async Task The_last_server_admin_cannot_step_down()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var owner = await server.SignedInAs("admin");
        await CreateUser(owner, new { username = "deputy", password = UserPass, role = "admin" });

        string ownerId;
        using (var me = await owner.GetAsync("api/auth/me"))
            ownerId = JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement
                                  .GetProperty("user").GetProperty("id").GetString()!;

        using var r = await owner.PutAsync($"api/users?id={ownerId}", TestServer.Json(new { role = "admin" }));
        var body = await r.Content.ReadAsStringAsync();
        Assert.True((int)r.StatusCode is 400 or 409,
                    $"the last Server Admin demoted themself: {(int)r.StatusCode} {body}");
    }
}
