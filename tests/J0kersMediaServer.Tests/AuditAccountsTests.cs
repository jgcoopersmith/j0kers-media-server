using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using J0kersMediaServer.Auth;
using J0kersMediaServer.Dlna;
using J0kersMediaServer.Logging;
using Xunit;
using TestServer = J0kersMediaServer.Tests.ShutdownOnCloseTests.TestServer;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The accounts-and-sign-in findings from the 2026-09-17 audit, each driven as
/// its trigger describes: through the real server where the trigger is a
/// request, against the store or the signer directly where it is a file.
/// Every test here was run against the code before its fix and seen to fail
/// for the audit's reason.
/// </summary>
public class AuditAccountsTests
{
    private const string OwnerPass = "test-admin-passphrase-9";   // what SignedInAs claims the server with
    private const string UserPass = "test-user-passphrase-9";

    /// <summary>
    /// The most sessions one account may hold, as AuthService has it. Written
    /// out rather than referenced so this file compiles against the code from
    /// before the cap existed - which is what the red runs were made against.
    /// </summary>
    private const int SessionCap = 64;

    // ---------------------------------------------------------------- helpers

    private sealed record Attempt(HttpStatusCode Code, string? Cookie, string Body);

    /// <summary>One sign-in through the form's endpoint, whatever it answers.</summary>
    private static async Task<Attempt> TryLogin(TestServer server, string username, string? password,
                                                string? forwardedFor = null, bool remember = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = TestServer.Json(new { username, password, remember }),
        };
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var r = await server.Http.SendAsync(request);
        var body = await r.Content.ReadAsStringAsync();
        string? cookie = null;
        if (r.IsSuccessStatusCode && r.Headers.TryGetValues("Set-Cookie", out var set))
            cookie = set.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith(AuthService.CookieName + "=")
                                                                           && !c.EndsWith('='));
        return new Attempt(r.StatusCode, cookie, body);
    }

    private static async Task<(string cookie, string? key)> SignIn(TestServer server, string username, string? password,
                                                                    bool remember = false, string? forwardedFor = null)
    {
        var a = await TryLogin(server, username, password, forwardedFor, remember);
        Assert.True(a.Code == HttpStatusCode.OK && a.Cookie is not null,
                    $"sign-in as {username} failed: {(int)a.Code} {a.Body}");
        string? key = null;
        if (JsonDocument.Parse(a.Body).RootElement.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String)
            key = k.GetString();
        return (a.Cookie!, key);
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

    /// <summary>A client that presents a bearer credential - a key, or the legacy token.</summary>
    private static HttpClient BearerClient(TestServer server, string credential)
    {
        var c = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
        {
            BaseAddress = server.Http.BaseAddress,
            Timeout = TimeSpan.FromSeconds(20),
        };
        c.DefaultRequestHeaders.Add("Authorization", "Bearer " + credential);
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

    /// <summary>One account as GET /api/users describes it.</summary>
    private static async Task<JsonElement> Account(HttpClient admin, string id)
    {
        using var r = await admin.GetAsync("api/users");
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"could not list the accounts: {(int)r.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("users").EnumerateArray()
                           .First(u => u.GetProperty("id").GetString() == id).Clone();
    }

    private static async Task<HttpStatusCode> Me(HttpClient client)
    {
        using var r = await client.GetAsync("api/auth/me");
        return r.StatusCode;
    }

    /// <summary>
    /// Reads a file the server may be rewriting. Shared for writing and
    /// deleting too, or the read itself would be what makes the server's next
    /// save fail.
    /// </summary>
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Holds a file open the way a backup program or a scanner does: reading,
    /// and letting others read, but not replace it. Measured on this machine:
    /// File.Replace onto a file held like this fails with access denied, so
    /// every save of the store fails while it is held.
    /// </summary>
    private static FileStream Hold(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>The action throws, and for the reason under test: the file could not be written.</summary>
    private static void WriteFails(Action action, string what)
    {
        var ex = Record.Exception(action);
        Assert.True(ex is IOException or UnauthorizedAccessException,
                    $"{what}: expected the write to fail, got {(ex is null ? "success" : ex.GetType().Name + ": " + ex.Message)}");
    }

    /// <summary>Every problem found, not only the first, so a red run shows them all.</summary>
    private static void NoProblems(List<string> problems) =>
        Assert.True(problems.Count == 0, string.Join("\n", problems));

    private static void Icacls(params string[] args)
    {
        var psi = new ProcessStartInfo("icacls")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"icacls {string.Join(' ', args)} failed with exit {p.ExitCode}");
    }

    private static string ThisAccount() => Environment.UserDomainName.Length > 0
        ? $"{Environment.UserDomainName}\\{Environment.UserName}"
        : Environment.UserName;

    // ======================================================== [4] sessions

    /// <summary>
    /// [4] An open account signs in on its name alone, and every sign-in
    /// rewrote users.json (a last-sign-in stamp) under the lock every other
    /// request authenticates through. An unauthenticated loop is a loop of
    /// file rewrites.
    /// </summary>
    [Fact]
    public async Task Signing_in_to_an_open_account_again_does_not_rewrite_the_accounts_file()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var admin = await server.SignedInAs("admin");
        await CreateUser(admin, new { username = "visitor", role = "read", passwordless = true });
        var file = Path.Combine(server.Dir, "users.json");

        await SignIn(server, "visitor", null, forwardedFor: "198.51.100.1");
        var after1 = ReadShared(file);
        for (var i = 2; i <= 4; i++) await SignIn(server, "visitor", null, forwardedFor: $"198.51.100.{i}");
        var after4 = ReadShared(file);

        Assert.True(after1.AsSpan().SequenceEqual(after4),
                    "users.json was rewritten by repeat sign-ins to an open account");
    }

    /// <summary>
    /// [4] Nothing limited how often one address could sign in to an open
    /// account: each one is a session row and a log line, for anyone.
    /// </summary>
    [Fact]
    public async Task Open_account_sign_ins_from_one_address_are_rate_limited()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var admin = await server.SignedInAs("admin");
        await CreateUser(admin, new { username = "visitor", role = "read", passwordless = true });

        var codes = new List<int>();
        for (var i = 0; i < 30; i++)
            codes.Add((int)(await TryLogin(server, "visitor", null, forwardedFor: "203.0.113.50")).Code);

        Assert.True(codes.Take(5).All(c => c == 200), "an ordinary handful of sign-ins was refused: " + string.Join(",", codes));
        Assert.True(codes.Contains(429), "30 open sign-ins from one address in a row were never limited: " + string.Join(",", codes));

        // and the limit is that address's, not the account's
        var elsewhere = await TryLogin(server, "visitor", null, forwardedFor: "203.0.113.51");
        Assert.True(elsewhere.Code == HttpStatusCode.OK,
                    $"one address's sign-ins stopped another address signing in: {(int)elsewhere.Code} {elsewhere.Body}");
    }

    /// <summary>
    /// [4] Sessions were only ever removed after twelve hours idle, so a loop of
    /// sign-ins - to an open account, or trading one key for sessions over and
    /// over - grew the table without bound. Capped per account, and what goes
    /// is what has been quiet longest, so a page that is actually in use stays.
    /// </summary>
    [Fact]
    public async Task An_account_cannot_pile_up_sessions_without_limit()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var admin = await server.SignedInAs("admin");
        var visitor = await CreateUser(admin, new { username = "visitor", role = "read", passwordless = true });
        var reader = await CreateUser(admin, new { username = "reader", role = "read", password = UserPass });

        var (inUse, _) = await SignIn(server, "visitor", null, forwardedFor: "198.18.0.1");
        using var page = CookieClient(server, inUse);
        for (var i = 0; i < SessionCap + 20; i++)
        {
            // a different address each time, so no per-address limit is what stops it
            var a = await TryLogin(server, "visitor", null, forwardedFor: $"198.18.{1 + i / 200}.{1 + i % 200}");
            Assert.True(a.Code == HttpStatusCode.OK, $"sign-in {i} was refused: {(int)a.Code} {a.Body}");
            if (i % 10 == 0) Assert.Equal(HttpStatusCode.OK, await Me(page));   // the page someone is using
        }

        var (_, key) = await SignIn(server, "reader", UserPass, remember: true);
        for (var i = 0; i < SessionCap + 20; i++)
        {
            using var trade = new HttpRequestMessage(HttpMethod.Post, "api/auth/session");
            trade.Headers.Add("X-Api-Key", key);
            using var r = await server.Http.SendAsync(trade);
            Assert.True(r.IsSuccessStatusCode, $"trade {i} was refused: {(int)r.StatusCode}");
        }

        var visitorSessions = (await Account(admin, visitor)).GetProperty("sessions").GetInt32();
        var readerSessions = (await Account(admin, reader)).GetProperty("sessions").GetInt32();
        Assert.True(visitorSessions <= SessionCap,
                    $"{SessionCap + 21} sign-ins to an open account left {visitorSessions} live sessions");
        Assert.True(readerSessions <= SessionCap,
                    $"{SessionCap + 20} key trades left {readerSessions} live sessions");
        Assert.True(await Me(page) == HttpStatusCode.OK, "the session that was in use was the one evicted");
    }

    /// <summary>
    /// [4] POST /api/auth/session minted a new session on every call and never
    /// retired the one the browser arrived holding - unlike a sign-in, which
    /// always hands back a fresh identifier in place of the old.
    /// </summary>
    [Fact]
    public async Task Trading_a_key_for_a_session_retires_the_session_it_arrived_with()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var admin = await server.SignedInAs("admin");
        await CreateUser(admin, new { username = "reader", role = "read", password = UserPass });
        var (before, key) = await SignIn(server, "reader", UserPass, remember: true);

        string after;
        using (var trade = new HttpRequestMessage(HttpMethod.Post, "api/auth/session"))
        {
            trade.Headers.Add("X-Api-Key", key);
            trade.Headers.Add("Cookie", before);
            using var r = await server.Http.SendAsync(trade);
            Assert.True(r.IsSuccessStatusCode, $"could not trade the key: {(int)r.StatusCode}");
            after = r.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).First(c => !c.EndsWith('='));
        }

        using var old = CookieClient(server, before);
        using var fresh = CookieClient(server, after);
        Assert.Equal(HttpStatusCode.OK, await Me(fresh));
        Assert.True(await Me(old) == HttpStatusCode.Unauthorized,
                    "the session the browser traded in still works alongside the new one");
    }

    // ================================================= [6] failed writes

    /// <summary>
    /// [6] Every change to an account was made in memory first and then
    /// saved; when the save failed, nothing put it back. The request answered
    /// 500 and the change stayed in force - a revoked key refused - until a
    /// restart brought the file's version back, or a later unrelated save
    /// made the unsaved one permanent.
    /// </summary>
    [Fact]
    public void An_account_change_that_cannot_be_written_is_not_kept()
    {
        using var dir = new TempDir();
        var store = new UserStore(dir.Path);
        store.Create("owner", OwnerPass, UserStore.RoleServerAdmin, null, enabled: true);
        var reader = store.Create("reader", UserPass, UserStore.RoleEdit, "Reader", enabled: true);
        var (_, key) = store.CreateKey(reader, "phone");
        var file = dir.File("users.json");
        var onDisk = File.ReadAllText(file);
        var hash = reader.PasswordHash;

        var problems = new List<string>();
        void Check(bool ok, string problem) { if (!ok) problems.Add(problem); }
        using (Hold(file))
        {
            WriteFails(() => store.RevokeKey(reader, key.Id), "RevokeKey");
            Check(store.KeyAlive(reader, key.Id), "a key revoke that was never written is in force");

            WriteFails(() => store.Delete(reader), "Delete");
            Check(store.FindById(reader.Id) is not null, "an account delete that was never written is in force");

            WriteFails(() => store.Create("late", UserPass, UserStore.RoleRead, null, enabled: true), "Create");
            Check(store.FindByName("late") is null, "an account whose creation was never written exists");

            WriteFails(() => store.SetPassword(reader, "another-passphrase-1"), "SetPassword");
            Check(reader.PasswordHash == hash, "a password change that was never written is in force");

            WriteFails(() => store.Update(reader, "renamed", "Renamed", UserStore.RoleRead, false), "Update");
            Check(reader is { Username: "reader", DisplayName: "Reader", Role: UserStore.RoleEdit, Enabled: true },
                  $"an edit that was never written is in force: {reader.Username} {reader.DisplayName} {reader.Role} {reader.Enabled}");

            var passwordlessBefore = reader.Passwordless;
            WriteFails(() => store.SetPasswordless(reader, true), "SetPasswordless");
            Check(reader.Passwordless == passwordlessBefore, "opening the account, never written, is in force");

            var keysBefore = store.KeysOf(reader).Count;
            WriteFails(() => store.CreateKey(reader, "tablet"), "CreateKey");
            Check(store.KeysOf(reader).Count == keysBefore, "a key whose creation was never written exists");
        }
        NoProblems(problems);

        Assert.Equal(onDisk, File.ReadAllText(file));

        // The next save that does work writes memory as it stands - which has
        // to be what the failed ones left, not what they tried to do.
        store.Create("later", UserPass, UserStore.RoleRead, null, enabled: true);
        var restarted = new UserStore(dir.Path);
        var back = restarted.FindByName("reader");
        Assert.True(back is not null, "the account whose delete failed is gone after a restart");
        Assert.True(back!.Role == UserStore.RoleEdit && back.Enabled && !back.Passwordless && back.PasswordHash == hash);
        Assert.True(restarted.KeyAlive(back, key.Id), "the key whose revoke failed is gone after a restart");
        Assert.Single(restarted.KeysOf(back));
        Assert.Null(restarted.FindByName("late"));
    }

    // ============================================ [28] [75] editing a user

    /// <summary>
    /// [28] The password was checked last, after the rename, the role and the
    /// passwordless change had been saved. Unticking passwordless had also
    /// cleared the account's password and keys, so a 400 for a short password
    /// left an account with no way in, and an administrator who believed
    /// nothing had changed.
    /// </summary>
    [Fact]
    public async Task An_edit_refused_for_its_password_changes_nothing()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var admin = await server.SignedInAs("admin");
        var id = await CreateUser(admin, new { username = "visitor", role = "read", passwordless = true });

        using (var r = await admin.PutAsync($"api/users?id={id}", TestServer.Json(new
               {
                   username = "renamed", role = "edit", enabled = true, passwordless = false, password = "short",
               })))
        {
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"a six-character password was accepted: {(int)r.StatusCode} {body}");
        }

        var u = await Account(admin, id);
        Assert.True(u.GetProperty("username").GetString() == "visitor", "the refused edit renamed the account");
        Assert.True(u.GetProperty("role").GetString() == UserStore.RoleRead, "the refused edit changed the role");
        Assert.True(u.GetProperty("passwordless").GetBoolean(), "the refused edit closed the open account");
        var again = await TryLogin(server, "visitor", null);
        Assert.True(again.Code == HttpStatusCode.OK, $"after a refused edit the account cannot sign in: {(int)again.Code} {again.Body}");
    }

    /// <summary>
    /// [75] Unticking passwordless and choosing a role in one save: the role
    /// was applied while the account was still passwordless, and the store
    /// pins a passwordless account to Read - so the answer was 200 and the
    /// account was Read.
    /// </summary>
    [Fact]
    public async Task Closing_an_open_account_and_choosing_its_role_in_one_save_keeps_the_role()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var admin = await server.SignedInAs("admin");
        var id = await CreateUser(admin, new { username = "visitor", role = "read", passwordless = true });

        using (var r = await admin.PutAsync($"api/users?id={id}", TestServer.Json(new
               {
                   username = "visitor", role = "edit", enabled = true, passwordless = false, password = UserPass,
               })))
            Assert.True(r.IsSuccessStatusCode, $"the save was refused: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");

        var u = await Account(admin, id);
        Assert.True(u.GetProperty("role").GetString() == UserStore.RoleEdit,
                    $"the account was saved as {u.GetProperty("role").GetString()}, not the edit role chosen");
        Assert.False(u.GetProperty("passwordless").GetBoolean());
        await SignIn(server, "visitor", UserPass);
    }

    // ======================================================= [38] the token

    /// <summary>
    /// [38] A configured control.authToken protected nothing until an
    /// administrator account existed: no credential at all fell through to
    /// "open", Server Admin, while the token itself was only Admin. Anyone on
    /// the network could change settings, read the log, or claim the server.
    /// </summary>
    [Fact]
    public async Task A_control_token_protects_a_server_that_has_no_administrator_yet()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        const string token = "legacy-token-audit-0123456789";
        using (var set = await server.Http.PostAsync("api/settings", TestServer.Json(new { authToken = token })))
            Assert.True(set.IsSuccessStatusCode, $"could not set the token: {(int)set.StatusCode}");

        var problems = new List<string>();
        using (var r = await server.Http.GetAsync("api/config"))
            if (r.StatusCode != HttpStatusCode.Unauthorized)
                problems.Add($"with a control token set, a request with no credential read the config: {(int)r.StatusCode}");

        using var holder = BearerClient(server, token);
        using (var r = await holder.GetAsync("api/log"))
            if (!r.IsSuccessStatusCode)
                problems.Add($"the token holder was refused the log, which a caller with no credential got: {(int)r.StatusCode}");

        using (var r = await server.Http.PostAsync("api/auth/setup",
                   TestServer.Json(new { username = "intruder", password = "intruder-passphrase-1" })))
            if (r.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                problems.Add($"with a control token set, the server was claimed without it: {(int)r.StatusCode}");
        NoProblems(problems);

        using (var r = await holder.PostAsync("api/auth/setup", TestServer.Json(new { username = "owner", password = OwnerPass })))
            Assert.True(r.IsSuccessStatusCode, $"the token holder could not claim the server: {(int)r.StatusCode} "
                                               + await r.Content.ReadAsStringAsync());
        await SignIn(server, "owner", OwnerPass);

        // Claimed, the token goes back to what it always was: an administrator.
        using (var r = await holder.GetAsync("api/config"))
            Assert.True(r.IsSuccessStatusCode, $"once claimed, the token no longer reads the config: {(int)r.StatusCode}");
        using (var r = await server.Http.GetAsync("api/config"))
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    /// <summary>
    /// [38], as first fixed, made the token the top tier on a claimed server
    /// too: whoever held it - and it travels in ?token= links - could demote or
    /// delete the owner. The audit's point was the unclaimed server; claimed,
    /// the token is an administrator, as it always was.
    /// </summary>
    [Fact]
    public async Task Once_claimed_the_control_token_is_an_administrator_not_the_owner()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        const string token = "legacy-token-audit-9876543210";
        using (var set = await server.Http.PostAsync("api/settings", TestServer.Json(new { authToken = token })))
            Assert.True(set.IsSuccessStatusCode, $"could not set the token: {(int)set.StatusCode}");
        using var holder = BearerClient(server, token);
        using (var r = await holder.PostAsync("api/auth/setup", TestServer.Json(new { username = "owner", password = OwnerPass })))
            Assert.True(r.IsSuccessStatusCode, $"precondition: the token holder could not claim the server: {(int)r.StatusCode}");

        using (var r = await holder.GetAsync("api/config"))
            Assert.True(r.IsSuccessStatusCode, $"once claimed, the token no longer reads the config: {(int)r.StatusCode}");
        using (var r = await holder.GetAsync("api/log"))
            Assert.False(r.IsSuccessStatusCode,
                         $"once claimed, the token still has the owner's tier: the Server Admin log answered {(int)r.StatusCode}");
    }

    /// <summary>
    /// [38] requires the token for first-run setup, and the setup form had no
    /// way to send it: claiming a token-protected server from a browser was
    /// impossible. The server now says when setup needs the token, and the form
    /// asks for it (login.html, checked by reading: typing a token into a page
    /// is not something these checks do).
    /// </summary>
    [Fact]
    public async Task The_setup_form_is_told_when_setup_needs_the_control_token()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        async Task<bool> NeedsToken()
        {
            using var r = await server.Http.GetAsync("api/auth/state");
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("setupNeedsToken", out var p) && p.GetBoolean();
        }

        Assert.False(await NeedsToken(), "a server with no control token asked the setup form for one");
        using (var set = await server.Http.PostAsync("api/settings", TestServer.Json(new { authToken = "legacy-token-audit-5555" })))
            Assert.True(set.IsSuccessStatusCode, $"could not set the token: {(int)set.StatusCode}");
        Assert.True(await NeedsToken(), "with a control token set, the setup form was not told that setup needs it");
    }

    // ================================================= [39] [40] lockout

    /// <summary>
    /// [39] [40] The lockout was checked, then the password hashed for a tenth
    /// of a second, and only then the failure counted - so every guess that
    /// started before the fifth failure landed got a full check. A burst of
    /// sixty-four is sixty-four guesses per lockout window, not five.
    /// </summary>
    [Fact]
    public async Task A_burst_of_sign_ins_gets_no_more_guesses_than_the_limit()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        (await server.SignedInAs("admin")).Dispose();

        var start = new TaskCompletionSource();
        var burst = Enumerable.Range(0, 64).Select(async i =>
        {
            await start.Task;
            return (await TryLogin(server, "owner", "wrong-guess-" + i)).Code;
        }).ToArray();
        start.SetResult();
        var codes = await Task.WhenAll(burst);

        var checkedGuesses = codes.Count(c => c == HttpStatusCode.Unauthorized);
        Assert.True(checkedGuesses <= 5,
                    $"{checkedGuesses} of 64 simultaneous guesses were checked before the lockout; "
                    + $"answers: {string.Join(",", codes.GroupBy(c => (int)c).Select(g => g.Key + "x" + g.Count()))}");
    }

    /// <summary>[40] The same, over RTSP, which counts on the same lockout.</summary>
    [Fact]
    public void A_burst_of_rtsp_guesses_gets_no_more_than_the_limit()
    {
        using var dir = new TempDir();
        var store = new UserStore(dir.Path);
        var name = "rtsp" + Guid.NewGuid().ToString("N")[..8];
        store.Create(name, UserPass, UserStore.RoleRead, null, enabled: true);
        var auth = new AuthService(store, "");
        var since = Log.Since(0, 1).Last;

        using var go = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, 64).Select(i => new Thread(() =>
        {
            go.Wait();
            var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{name}:wrong-guess-{i}"));
            auth.VerifyRtspCredentials(header, "203.0.113.9");
        })).ToList();
        threads.ForEach(t => t.Start());
        go.Set();
        threads.ForEach(t => t.Join());

        var (entries, _, missed) = Log.Since(since);
        Assert.False(missed, "the log ring wrapped while the burst ran, so the checked guesses cannot be counted");
        var checkedGuesses = entries.Count(e => e.Message.Contains($"failed RTSP login for '{name}'"));
        Assert.True(checkedGuesses <= 5, $"{checkedGuesses} of 64 simultaneous RTSP guesses were checked before the lockout");
    }

    /// <summary>
    /// The "current password" check behind a password change has the same
    /// check-then-count shape (it arrived after the audit, in v2.0.310), so a
    /// borrowed session could fire its guesses at once and have them all
    /// checked.
    /// </summary>
    [Fact]
    public async Task A_burst_of_current_password_guesses_gets_no_more_than_the_limit()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var reader = await server.SignedInAs("read");

        var start = new TaskCompletionSource();
        var burst = Enumerable.Range(0, 64).Select(async i =>
        {
            await start.Task;
            using var r = await reader.PostAsync("api/auth/password",
                TestServer.Json(new { currentPassword = "wrong-guess-" + i, newPassword = "a-new-passphrase-1" }));
            return r.StatusCode;
        }).ToArray();
        start.SetResult();
        var codes = await Task.WhenAll(burst);

        var checkedGuesses = codes.Count(c => c == HttpStatusCode.Forbidden);
        Assert.True(checkedGuesses <= 5,
                    $"{checkedGuesses} of 64 simultaneous current-password guesses were checked; "
                    + $"answers: {string.Join(",", codes.GroupBy(c => (int)c).Select(g => g.Key + "x" + g.Count()))}");
    }

    // ======================================= [41] a sign-in in flight

    /// <summary>
    /// Real <see cref="HttpListenerContext"/>s, for calling AuthService
    /// directly: the class cannot be constructed, so a request is sent to a
    /// listener on loopback and the server side of it is handed back
    /// unanswered.
    /// </summary>
    private sealed class LoopbackContexts : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly HttpClient _client = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false });
        private readonly int _port;

        public LoopbackContexts()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
        }

        public async Task<(HttpListenerContext Context, Task<HttpResponseMessage> Reply)> Next()
        {
            var accept = _listener.GetContextAsync();
            var reply = _client.GetAsync($"http://127.0.0.1:{_port}/");
            return (await accept, reply);
        }

        public void Dispose()
        {
            _client.Dispose();
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }

    /// <summary>
    /// [41] A sign-in reads the stored hash and then spends the whole PBKDF2
    /// on it; a password change sets the new hash and then revokes the
    /// account's sessions. A sign-in with the OLD password that read the hash
    /// before the change, and opened its session after the revocation, got a
    /// brand-new session - valid for a week, after the owner had been told
    /// every other session was signed out.
    ///
    /// Driven deterministically. Sent through the real server the race was won
    /// in 2 runs of 7, and holding the store's lock to widen it still failed
    /// only 5 runs in 10: one hash takes about 16 ms on this machine, below
    /// what a sleep can resolve. So the window is made wide instead, the one
    /// way the file format allows: a stored hash names its own iteration count,
    /// and verification honours up to ten million. The owner's hash is written
    /// at three million, so checking a guess against it takes a few hundred
    /// milliseconds, and the password change lands while it is being checked,
    /// every time. The new password is hashed at the ordinary count, as any
    /// change is.
    /// </summary>
    [Fact]
    public async Task A_sign_in_in_flight_with_the_old_password_does_not_outlive_the_change()
    {
        using var dir = new TempDir();
        const int slowIterations = 3_000_000;
        var salt = RandomNumberGenerator.GetBytes(16);
        var clock = Stopwatch.StartNew();
        var slow = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(OwnerPass), salt, slowIterations,
                                             HashAlgorithmName.SHA256, 32);
        var slowTime = clock.Elapsed;   // what checking a guess against it will take
        File.WriteAllText(dir.File("users.json"), JsonSerializer.Serialize(new
        {
            users = new[]
            {
                new
                {
                    id = "0a1b2c3d4e5f6a7b", username = "owner", role = UserStore.RoleServerAdmin, enabled = true,
                    passwordHash = $"pbkdf2-sha256${slowIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(slow)}",
                },
            },
        }));
        var store = new UserStore(dir.Path);
        var owner = store.FindByName("owner")!;
        var auth = new AuthService(store, "");
        using var loopback = new LoopbackContexts();

        // One whole sign-in first, on another account, so that the one under
        // test is not the first call through that code and starts at once.
        store.Create("warm", UserPass, UserStore.RoleRead, null, enabled: true);
        var (warm, warmReply) = await loopback.Next();
        Assert.True(auth.Login("warm", UserPass, warm).Ok, "precondition: an ordinary sign-in works");
        warm.Response.Close();
        (await warmReply).Dispose();

        var (ctx, reply) = await loopback.Next();
        var signIn = Task.Factory.StartNew(() => auth.Login("owner", OwnerPass, ctx), TaskCreationOptions.LongRunning);
        await Task.Delay(slowTime / 4);   // it has read the stored hash and is checking the guess against it

        // what ChangeOwnPassword and EditUser do, in their order
        store.SetPassword(owner, "a-changed-passphrase-4");
        auth.RevokeSessionsFor(owner.Id);
        Assert.False(signIn.IsCompleted,
                     $"precondition: the sign-in finished before the password changed (one slow hash: {slowTime.TotalMilliseconds:F0} ms)");

        var outcome = await signIn;
        ctx.Response.Close();
        (await reply).Dispose();

        Assert.True(auth.SessionCountFor(owner.Id) == 0,
                    $"a sign-in with the old password (answered {(outcome.Ok ? "OK" : outcome.Error)}) holds a live session "
                    + "after the password was changed and every session revoked");

        // and the new password signs in as it should
        var (again, againReply) = await loopback.Next();
        Assert.True(auth.Login("owner", "a-changed-passphrase-4", again).Ok, "the new password does not sign in");
        again.Response.Close();
        (await againReply).Dispose();
        Assert.Equal(1, auth.SessionCountFor(owner.Id));
    }

    /// <summary>
    /// [41] for open accounts. A passwordless sign-in read the credential
    /// generation when it opened the session, not when it found the account
    /// open - so an administrator closing the account in between (which moves
    /// the generation on and revokes every session) left this one stamped with
    /// the new generation, alive in an account that now had no password. The
    /// window is held open with a hook at exactly that point.
    /// </summary>
    [Fact]
    public async Task A_guest_sign_in_in_flight_does_not_outlive_the_account_being_closed()
    {
        using var dir = new TempDir();
        var store = new UserStore(dir.Path);
        var guest = store.Create("visitor", null, UserStore.RoleRead, null, enabled: true, passwordless: true);
        var auth = new AuthService(store, "");
        auth.BeforeGuestSession = u =>
        {
            // what EditUser does when passwordless is unticked
            store.SetPasswordless(u, false);
            auth.RevokeSessionsFor(u.Id);
        };
        using var loopback = new LoopbackContexts();

        var (ctx, reply) = await loopback.Next();
        var outcome = auth.Login("visitor", null, ctx);
        ctx.Response.Close();
        (await reply).Dispose();

        Assert.True(outcome.Ok, "precondition: the sign-in found the account open");
        Assert.True(auth.SessionCountFor(guest.Id) == 0,
                    "a sign-in that found the account open holds a live session after the account was closed "
                    + "and its sessions revoked");
    }

    /// <summary>
    /// [39]/[40], as first fixed: the reservation counted failures plus
    /// attempts in flight against the limit, and after a lock the failures
    /// alone are at the limit - so once the lock ran out, every attempt, the
    /// right password included, was refused and relocked for longer. Five wrong
    /// guesses by anyone locked the account until the server restarted. Once a
    /// lock has run out, one attempt is checked, as before.
    /// </summary>
    [Fact]
    public async Task A_lock_that_has_run_out_lets_the_right_password_in()
    {
        using var dir = new TempDir();
        var store = new UserStore(dir.Path);
        var name = "lapse" + Guid.NewGuid().ToString("N")[..8];
        store.Create(name, UserPass, UserStore.RoleRead, null, enabled: true);
        var auth = new AuthService(store, "");
        string Basic(string password) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{name}:{password}"));

        for (var i = 0; i < 5; i++)
            Assert.False(auth.VerifyRtspCredentials(Basic("wrong-guess-" + i), "203.0.113.20"));
        Assert.False(auth.VerifyRtspCredentials(Basic(UserPass), "203.0.113.20"), "precondition: five wrong guesses lock it");

        await Task.Delay(TimeSpan.FromSeconds(16));   // the first lock is 15 seconds

        Assert.True(auth.VerifyRtspCredentials(Basic(UserPass), "203.0.113.20"),
                    "the lock had run out and the right password was still refused");
    }

    /// <summary>
    /// [4], as first fixed: open sign-ins were counted per address with the
    /// failure counter, and nothing ever started that count again - a success
    /// is what it counts, and throttles are only swept once there are 64 of
    /// them. So an address's sixth guest sign-in since the server started was
    /// refused, and each one after it for longer. A household signing in now
    /// and then, spells apart, must never meet the limit; a loop still does
    /// (Open_account_sign_ins_from_one_address_are_rate_limited). The quiet
    /// spell is shortened so the test need not wait ten minutes for it.
    /// </summary>
    [Fact]
    public async Task Open_account_sign_ins_spells_apart_are_never_refused()
    {
        using var dir = new TempDir();
        var store = new UserStore(dir.Path);
        store.Create("visitor", null, UserStore.RoleRead, null, enabled: true, passwordless: true);
        var auth = new AuthService(store, "") { GuestSignInQuiet = TimeSpan.FromMilliseconds(300) };
        using var loopback = new LoopbackContexts();

        var answers = new List<string>();
        for (var spell = 0; spell < 6; spell++)
        {
            for (var i = 0; i < 5; i++)
            {
                var (ctx, reply) = await loopback.Next();
                var outcome = auth.Login("visitor", null, ctx);
                ctx.Response.Close();
                (await reply).Dispose();
                answers.Add(outcome.Ok ? "ok" : "refused");
            }
            await Task.Delay(600);   // quiet for twice the spell
        }

        Assert.True(answers.All(a => a == "ok"),
                    $"30 open sign-ins in spells of 5, each spell apart, met the limit: {string.Join(",", answers)}");
    }

    // ================================================ [43] [44] signing key

    /// <summary>
    /// [43] Any error reading signing.key - a scanner holding it, a permission
    /// - was taken as "there is no key", and a new one was written over the
    /// file. Every pinned channel's saved link, and every outstanding share
    /// link, died with the old key. Reproduced with a permission that refuses
    /// reading and allows writing, which is exactly the case where the new key
    /// lands on disk.
    /// </summary>
    [Fact]
    public void A_signing_key_that_cannot_be_read_is_not_written_over()
    {
        if (!OperatingSystem.IsWindows()) return;   // the permission is set with icacls
        using var dir = new TempDir();
        var path = dir.File("signing.key");
        var original = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, original);

        Icacls(path, "/deny", ThisAccount() + ":(RD)");
        try
        {
            _ = new MediaLink(dir.Path);
        }
        finally
        {
            Icacls(path, "/remove:d", ThisAccount());
        }
        Assert.True(File.ReadAllText(path).Trim() == original,
                    "signing.key could not be read, and a new key was written over it");
    }

    /// <summary>
    /// [43] A key held open for a moment - the usual scanner - is waited for,
    /// not replaced: links signed afterwards verify with the key on disk.
    /// </summary>
    [Fact]
    public async Task A_signing_key_held_open_for_a_moment_is_waited_for()
    {
        using var dir = new TempDir();
        var path = dir.File("signing.key");
        var secret = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToBase64String(secret));

        MediaLink link;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var loading = Task.Run(() => new MediaLink(dir.Path));
            await Task.Delay(400);
            held.Dispose();
            link = await loading;
        }

        var expected = UserStore.Base64Url(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes("url\ntv:pluto:abc")));
        Assert.True(link.SignUrl("tv:pluto:abc") == expected,
                    "a signing.key held open for 400 ms at startup was replaced by a new key");
        Assert.Equal(Convert.ToBase64String(secret), File.ReadAllText(path).Trim());
    }

    /// <summary>
    /// [44] The expiry was converted to a date before it was compared, and a
    /// number past the year 9999 made that conversion throw - an
    /// unauthenticated request answered 500 and logged a warning instead of
    /// being refused.
    /// </summary>
    [Theory]
    [InlineData("99999999999999")]
    [InlineData("-99999999999999")]
    [InlineData("9223372036854775807")]
    public void An_expiry_off_the_calendar_is_refused_not_thrown(string exp)
    {
        using var dir = new TempDir();
        var link = new MediaLink(dir.Path);
        var verified = false;
        var ex = Record.Exception(() => { verified = link.Verify("vod-some-film", exp, "x"); });
        Assert.True(ex is null, $"an expiry of {exp} threw {ex?.GetType().Name}: {ex?.Message}");
        Assert.False(verified);
    }

    /// <summary>[44] The same, as the audit's request: 401, not 500.</summary>
    [Fact]
    public async Task A_playlist_link_with_an_expiry_off_the_calendar_is_refused()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var r = await server.Http.GetAsync("api/tv/playlist.m3u?exp=99999999999999&sig=x");
        Assert.True(r.StatusCode == HttpStatusCode.Unauthorized,
                    $"a playlist link with an impossible expiry answered {(int)r.StatusCode}");
    }

    // ============================================ [45] [46] [47] [48] users.json

    /// <summary>
    /// [45] [46] Promoting the sole administrator of an older install to
    /// Server Admin saves the file, inside the same try as the parse - so a
    /// file that could not be written was reported as a file that could not be
    /// read ("users.json is invalid"), and the server refused to start.
    /// </summary>
    [Fact]
    public void A_promotion_that_cannot_be_written_does_not_stop_the_server()
    {
        using var dir = new TempDir();
        var path = dir.File("users.json");
        File.WriteAllText(path, """
            { "users": [ { "id": "a1b2c3d4", "username": "boss", "role": "admin", "enabled": true } ] }
            """);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            UserStore? store = null;
            var ex = Record.Exception(() => { store = new UserStore(dir.Path); });
            Assert.True(ex is null, $"a valid, read-only users.json stopped the store loading: {ex?.Message}");
            Assert.True(store!.FindByName("boss")!.IsServerAdmin, "the sole administrator was not promoted for this run");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// [47] A users.json that appears after startup is copied aside before
    /// anything is written over it - but when the copy failed, the write went
    /// ahead anyway, and the only copy of the restored accounts was replaced.
    /// The copy is made to fail here the way it can anywhere: something is
    /// already at the name it copies to.
    /// </summary>
    [Fact]
    public void An_accounts_file_that_cannot_be_kept_aside_is_not_written_over()
    {
        using var dir = new TempDir();
        var store = new UserStore(dir.Path);   // started with no users.json
        var path = dir.File("users.json");
        const string restored = """{ "users": [ { "id": "r1", "username": "restored", "role": "serveradmin", "enabled": true } ] }""";
        File.WriteAllText(path, restored);

        var now = DateTime.UtcNow;
        for (var s = -5; s <= 120; s++)
            Directory.CreateDirectory($"{path}.found-{now.AddSeconds(s):yyyyMMdd-HHmmss}");

        var ex = Record.Exception(() => { store.Create("newowner", OwnerPass, UserStore.RoleServerAdmin, null, enabled: true); });
        Assert.True(File.ReadAllText(path) == restored,
                    "the restored accounts file could not be copied aside and was written over anyway");
        Assert.True(ex is not null, "the save reported success without writing");
    }

    /// <summary>
    /// [48] Every successful password check stamped the time and rewrote
    /// users.json under the store lock - and RTSP checks the password on every
    /// request of a session, keep-alives included. A key's use is stamped at
    /// most once a minute; a password's now is too.
    /// </summary>
    [Fact]
    public void A_password_checked_again_within_the_minute_does_not_rewrite_the_accounts_file()
    {
        using var dir = new TempDir();
        var store = new UserStore(dir.Path);
        store.Create("viewer", UserPass, UserStore.RoleRead, null, enabled: true);
        var path = dir.File("users.json");

        Assert.NotNull(store.VerifyPassword("viewer", UserPass));
        var first = File.ReadAllBytes(path);
        Assert.NotNull(store.VerifyPassword("viewer", UserPass));
        Assert.NotNull(store.VerifyPassword("viewer", UserPass));
        Assert.True(first.AsSpan().SequenceEqual(File.ReadAllBytes(path)),
                    "users.json was rewritten by a password check seconds after the last one");
    }

    // ================================================== [56] who is watching

    /// <summary>
    /// [56] GET /api/sessions is open to a read account - a passwordless guest
    /// included - and listed every viewer's address, player and account. The
    /// status call already keeps that to administrators; this one did not.
    /// </summary>
    [Fact]
    public async Task A_read_account_is_not_told_where_other_viewers_are_watching_from()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true, dlna: true);
        var library = Path.Combine(server.Dir, "library");
        Directory.CreateDirectory(library);
        var clip = Path.Combine(library, "clip.mp4");
        File.WriteAllBytes(clip, RandomNumberGenerator.GetBytes(64 * 1024));
        using (var add = await server.Http.PostAsync("api/library", TestServer.Json(new { folder = library })))
            Assert.True(add.IsSuccessStatusCode, $"could not add the library folder: {(int)add.StatusCode}");

        // a television watching it
        using (var tv = new HttpRequestMessage(HttpMethod.Get, "dlna/file?id=" + Uri.EscapeDataString(DlnaService.Encode(clip))))
        {
            tv.Headers.TryAddWithoutValidation("User-Agent", "VLC/3.0.20 LibVLC/3.0.20");
            using var r = await server.Http.SendAsync(tv);
            Assert.True(r.IsSuccessStatusCode, $"the television could not fetch the file: {(int)r.StatusCode}");
            await r.Content.ReadAsByteArrayAsync();
        }

        using var admin = await server.SignedInAs("admin");
        using var reader = await server.SignedInAs("read");

        static async Task<JsonElement> Viewing(HttpClient client)
        {
            using var r = await client.GetAsync("api/sessions");
            var text = await r.Content.ReadAsStringAsync();
            Assert.True(r.IsSuccessStatusCode, $"GET /api/sessions: {(int)r.StatusCode} {text}");
            return JsonDocument.Parse(text).RootElement.GetProperty("sessions").EnumerateArray()
                               .First(s => s.GetProperty("protocol").GetString() == "dlna").Clone();
        }

        var seenByAdmin = await Viewing(admin);
        Assert.True(seenByAdmin.GetProperty("client").GetString() is { Length: > 0 },
                    "precondition: an administrator sees where the viewer is");

        var seenByReader = await Viewing(reader);
        var client = seenByReader.GetProperty("client").GetString();
        var player = seenByReader.GetProperty("player").GetString();
        var user = seenByReader.GetProperty("user").GetString();
        Assert.True(string.IsNullOrEmpty(client) && string.IsNullOrEmpty(player) && string.IsNullOrEmpty(user),
                    $"a read account was told client='{client}', player='{player}', user='{user}'");
    }

    // ================================================ [74] a key-only admin

    /// <summary>
    /// [74] On an open server the caller has no account. Creating an enabled
    /// administrator with no password ended open access on the spot - and the
    /// new account had nothing to sign in with, the caller could no longer mint
    /// it a key, and setup was refused because accounts now existed. Locked
    /// out, short of editing users.json by hand.
    /// </summary>
    [Fact]
    public async Task An_open_server_cannot_be_locked_by_an_administrator_with_no_password()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);

        using (var r = await server.Http.PostAsync("api/users", TestServer.Json(new { username = "svc", role = "admin" })))
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest,
                        $"an open server accepted an administrator with no password: {(int)r.StatusCode}");

        using (var state = await server.Http.GetAsync("api/auth/state"))
        {
            var s = JsonDocument.Parse(await state.Content.ReadAsStringAsync()).RootElement;
            Assert.False(s.GetProperty("authRequired").GetBoolean(), "the refused create locked the server anyway");
        }
        // it is still reachable, and one made with a password works
        using (var r = await server.Http.PostAsync("api/users",
                   TestServer.Json(new { username = "svc", role = "admin", password = UserPass })))
            Assert.True(r.IsSuccessStatusCode, $"an administrator with a password was refused: {(int)r.StatusCode}");
        await SignIn(server, "svc", UserPass);
    }
}
