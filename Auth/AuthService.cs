using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Auth;

/// <summary>How much authority a request carries. Ordered: each tier includes the one below.</summary>
public enum AccessLevel
{
    /// <summary>Unauthenticated — the login screen and static assets only.</summary>
    None = 0,
    /// <summary>Watch what has been shared. Changes nothing.</summary>
    Read = 1,
    /// <summary>Adds and removes library content — folders, channels, mounts, playlists, streams.</summary>
    Edit = 2,
    /// <summary>Everything: configuration, the power button, and accounts.</summary>
    Admin = 3,
    /// <summary>
    /// The person who runs the machine, as distinct from the person who runs
    /// the library. Everything an administrator has, plus what exposes the
    /// server's own workings — the log, which names file paths, accounts and
    /// client addresses — and the sole right to grant this level.
    /// </summary>
    ServerAdmin = 4,
}

/// <summary>
/// Who is making this request and how they proved it.
/// </summary>
/// <param name="KeyId">
/// The key behind the request: the one it presented, or the one its session
/// came with. Null for a password session and the legacy token.
/// </param>
public sealed record AuthResult(AccessLevel Level, UserAccount? User, string Method, string? KeyId = null)
{
    public static readonly AuthResult Anonymous = new(AccessLevel.None, null, "none");
    public bool IsAdmin => Level >= AccessLevel.Admin;
    public bool IsServerAdmin => Level >= AccessLevel.ServerAdmin;
    /// <summary>Cookie-backed requests are the ones a hostile page could ride; keys and tokens are not.</summary>
    public bool IsCookie => Method == "session";
    public string Name => User?.Username ?? (Method == "token" ? "legacy-token" : "anonymous");
}

/// <summary>
/// Session and login handling for the control API.
///
/// Two ways in, both mapping onto the same accounts:
///   • password → a session, carried in an HttpOnly, SameSite=Strict cookie
///     so page JavaScript (and anything that manages to inject some) can't
///     read it, and so it never appears in a URL, log line, or Referer;
///   • key → <c>Authorization: Bearer jmk_…</c> (or <c>?key=</c> where a
///     media element can't set headers), for phones, players and scripts
///     that should just keep working without a login prompt.
///
/// A start signs everybody out. Sessions used to be restored from a
/// sessions.json sidecar so that a restart didn't cost anyone a login; see
/// ClearSessions for why that was the wrong trade.
/// Failed logins are throttled per account and per source address with an
/// escalating lockout.
/// </summary>
public sealed class AuthService
{
    public const string CookieName = "j0kers_session";
    /// <summary>Idle timeout — every authenticated request slides it forward.</summary>
    private static readonly TimeSpan SessionIdle = TimeSpan.FromHours(12);
    /// <summary>Hard cap, regardless of activity.</summary>
    private static readonly TimeSpan SessionMax = TimeSpan.FromDays(7);
    /// <summary>Lifetime of a "remember this device" key.</summary>
    public static readonly TimeSpan DeviceKeyLifetime = TimeSpan.FromDays(365);

    private const int MaxFailuresBeforeLockout = 5;
    /// <summary>For everyone behind one reverse proxy together. See Login.</summary>
    private const int MaxFailuresThroughProxy = 50;

    /// <summary>
    /// Passwordless sign-ins one address may make in a row before it is asked
    /// to wait. A loop is what this stops; a household opening the guest
    /// account is nowhere near it. See Login.
    /// </summary>
    private const int MaxGuestSignInsInARow = 20;

    /// <summary>
    /// How long an address must go without a passwordless sign-in for its
    /// count to start again. Settable so a test need not wait ten minutes.
    /// </summary>
    internal TimeSpan GuestSignInQuiet { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// For the tests: runs in a passwordless sign-in after the account was
    /// found open and before its session is opened - the window an
    /// administrator closing the account can land in. Null in the server.
    /// </summary>
    internal Action<UserAccount>? BeforeGuestSession { get; set; }

    private sealed class Session
    {
        [System.Text.Json.Serialization.JsonPropertyName("userId")]
        public required string UserId { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("createdUtc")]
        public required DateTime CreatedUtc { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("lastSeenUtc")]
        public DateTime LastSeenUtc { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("clientHint")]
        public string ClientHint { get; set; } = "";
        /// <summary>
        /// The key this session came with, if it came with one: the key minted
        /// alongside it by "remember this device", or the key a remembered
        /// browser traded in for it. Revoking or expiring that key ends the
        /// session too - see LiveUser.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("keyId")]
        public string? KeyId { get; set; }

        /// <summary>
        /// The account's credential generation the moment this session was
        /// opened. If the account's credentials change afterwards — a password
        /// change, passwordless toggled — the generation moves on and this
        /// session no longer matches, so it is refused. This is what stops a
        /// sign-in already in flight with the OLD password, whose hash read
        /// landed just before the change, from opening a session that outlives
        /// the "other sessions were signed out" the change promised. See
        /// LiveUser and OpenSession.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("credGen")]
        public int CredentialGeneration { get; set; }
    }

    private sealed class Throttle
    {
        public int Failures;
        /// <summary>
        /// Attempts that have been reserved but not yet settled — counted
        /// alongside Failures so that a burst which all passed the lockout
        /// check in the old check-then-hash gap cannot each still get a full
        /// password check. See Reserve.
        /// </summary>
        public int InFlight;
        public DateTime LockedUntilUtc;
        public DateTime LastFailureUtc = DateTime.UtcNow;
    }

    private readonly UserStore _users;
    /// <summary>
    /// Read on every request, not copied at startup. A copy meant the settings
    /// page could save a new token while the server went on honouring only
    /// the one it started with: rotating a leaked token left the leaked one
    /// working and the new one refused, until somebody thought to restart.
    /// </summary>
    private readonly Func<string?> _legacyToken;
    // keyed by SHA-256 of the token, so a memory dump or a stray log of this
    // dictionary still can't be replayed
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Throttle> _throttles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where sessions are kept between runs. They used to live only in
    /// memory, so every restart signed everybody out — survivable only
    /// because a remembered device could trade its key for a new session,
    /// and not at all if you had never ticked that box.
    ///
    /// What is stored is the SHA-256 of each token, exactly as in memory: the
    /// file cannot be replayed as a cookie, only recognised. Same class of
    /// secret as the key digests already in users.json, and it sits beside
    /// them with the same expectation of being owner-readable.
    /// </summary>
    private readonly string _sessionFile;

    public AuthService(UserStore users, string legacyToken, string? baseDirectory = null)
        : this(users, () => legacyToken, baseDirectory) { }

    public AuthService(UserStore users, Func<string?> legacyToken, string? baseDirectory = null)
    {
        _users = users;
        _legacyToken = legacyToken;
        _sessionFile = baseDirectory is null ? "" : Path.Combine(baseDirectory, "sessions.json");
        ClearSessions();
    }

    /// <summary>
    /// A server that has just started has nobody signed in.
    ///
    /// This used to restore the table from sessions.json, so that a restart
    /// did not cost everyone a login. What it did in practice was accumulate.
    /// The window the server opens for itself signs in through the self-open
    /// token, which mints a row every single start, and nothing ever took one
    /// away: an unused session was kept until it aged out days later. So the
    /// count only ever went up. Seven "users logged in" on a freshly started
    /// server, every one of them the same account at the same address, each
    /// one a window that had been opened once and closed long ago.
    ///
    /// A running total of every window ever opened is not a list of who is
    /// signed in, and it is worse than useless on the page that is supposed
    /// to show that — it is the place you would look to find out whether
    /// somebody else is on your server, answering with a number that means
    /// nothing. Surviving a restart was never worth that.
    ///
    /// So the table starts empty, and the file is emptied with it: whoever is
    /// signed in is whoever has signed in since this server started.
    /// </summary>
    private void ClearSessions()
    {
        if (_sessionFile.Length == 0 || !File.Exists(_sessionFile)) return;
        try
        {
            File.Delete(_sessionFile);
        }
        catch (Exception ex)
        {
            // Not fatal, but it must not pass in silence: a file that cannot
            // be removed is one a later save will merge into, and the count
            // starts creeping again.
            Log.Warn("auth", $"could not clear sessions.json ({ex.Message}) — " +
                             "old sessions may reappear in the signed-in list");
        }
    }

    /// <summary>
    /// Writes the session table. Called after anything that changes it; the
    /// idle-timestamp slide on every request deliberately does not, or a
    /// dashboard poll would rewrite this file every two seconds.
    /// </summary>
    private readonly object _saveLock = new();

    /// <summary>
    /// Does nothing, deliberately.
    ///
    /// sessions.json was how a restart avoided costing everyone their login:
    /// the table was written out and read back. That was removed — the file is
    /// now DELETED at every start, so sessions do not survive a restart — and
    /// the writes were left behind. The file was rewritten on every session
    /// change and on shutdown, and read by nothing at all.
    ///
    /// That is not merely wasted work. The rows are session digests, and
    /// writing credentials to disk for a file nobody reads is cost with no
    /// benefit at all. So it is no longer written, and the delete at startup
    /// clears anything an older build left.
    ///
    /// Kept as a method because the shutdown state-saver registers it by name;
    /// there is simply nothing for it to do.
    /// </summary>
    private void SaveSessions() { }

    /// <summary>
    /// Persists the sliding idle timestamps, so a browser left open for days
    /// isn't signed out by a restart just because the file still says noon.
    /// Called on shutdown, where one write costs nothing.
    /// </summary>
    public void FlushSessions() => SaveSessions();

    public UserStore Users => _users;

    /// <summary>
    /// True once at least one enabled admin exists. Until then the server
    /// behaves as it always has — open — and the dashboard shows a setup
    /// card instead of a login form.
    /// </summary>
    public bool Enforcing => _users.HasEnabledAdmin;

    /// <summary>No accounts at all: the dashboard should offer first-run setup.</summary>
    public bool SetupRequired => !_users.Any;

    // ---- request authentication ----

    /// <summary>
    /// Works out what a request is allowed to do. When no admin account
    /// exists yet, every request is treated as an admin so a fresh install
    /// is usable exactly as before; once one does, nothing is implicit.
    /// </summary>
    public AuthResult Authenticate(HttpListenerContext ctx)
    {
        var presented = BearerValue(ctx);

        // legacy control.authToken — still honoured.
        //
        // On a server nobody has claimed yet it is the whole key, so it is the
        // top tier there, the one the open server hands out. The audit found it
        // ranked BELOW having no credential at all in that state: the token got
        // Admin and was refused the log and the transcoder, while a caller with
        // nothing fell through to the "open" Server Admin below.
        //
        // Once there are accounts it stays what it always was, an
        // administrator. Server Admin there would let whoever holds a token -
        // which travels in ?token= links and sits in a browser's storage -
        // demote or delete the owner and make Server Admins of its own.
        var legacy = _legacyToken() ?? "";
        if (legacy.Length > 0 && presented is not null && FixedTimeEquals(presented, legacy))
            return new AuthResult(Enforcing ? AccessLevel.Admin : AccessLevel.ServerAdmin, null, "token");

        if (presented is not null && _users.VerifyKey(presented) is UserAccount keyUser)
            return new AuthResult(LevelOf(keyUser), keyUser, "key", UserStore.KeyIdOf(presented));

        // A key or token was offered and it is not one of ours. That is a
        // failed sign-in as surely as a wrong password, and it was the one
        // that left no trace at all: passwords, passwordless logins, lockouts
        // and RTSP failures were all logged, while somebody working through
        // guessed keys against this port produced complete silence.
        //
        // Rate-limited, and counted rather than remembered per credential:
        // the caller chooses the value, so keeping a set of the ones already
        // seen is a table an unauthenticated loop can grow without bound, and
        // writing a line per attempt is the same problem in the log file.
        // Same reasoning as NoteKeyInUrl below, which learned it the hard way.
        if (presented is not null) NoteRejectedCredential(ClientKey(ctx));

        if (ReadSessionCookie(ctx) is string token && ResolveSession(token, ctx, out var sessionKey) is UserAccount sessionUser)
            return new AuthResult(LevelOf(sessionUser), sessionUser, "session", sessionKey);

        // No accounts yet.
        if (!Enforcing)
        {
            // A configured control.authToken is meant to protect a server that
            // has no admin account yet, and until now it protected nothing: the
            // "open" ServerAdmin below was handed to ANY request, token or none,
            // so a token set with the server bound to the network left it wide
            // open to the whole LAN — settings, the log, first-run setup. When a
            // token is configured, only the caller who presents it (handled
            // above) gets into the control API; everyone else is Anonymous
            // here. (The media ports go by accounts alone, as they always have:
            // HLS and RTSP are open until the server is claimed, token or not.)
            // With no token configured, a fresh install is
            // still open to whoever reaches it, top tier included, so the owner
            // can create the first account and see the log while doing it.
            if (legacy.Length > 0) return AuthResult.Anonymous;
            return new AuthResult(AccessLevel.ServerAdmin, null, "open");
        }

        return AuthResult.Anonymous;
    }

    /// <summary>
    /// Whether a legacy control.authToken is set. When it is, an unclaimed
    /// server is protected by it rather than open — including first-run setup,
    /// which Setup gates on this. Read live, like the token itself.
    /// </summary>
    public bool LegacyTokenConfigured => (_legacyToken() ?? "").Length > 0;

    private static AccessLevel LevelOf(UserAccount user) => UserStore.LevelOf(user.Role);

    /// <summary>
    /// Pulls the credential out of an Authorization header, an
    /// X-Api-Key header, or a ?key=/?token= query parameter. The query
    /// forms exist because &lt;audio&gt;/&lt;video&gt; elements cannot set
    /// headers; they are for keys only — a password never travels in a URL.
    /// </summary>
    private string? BearerValue(HttpListenerContext ctx)
    {
        var auth = ctx.Request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = auth["Bearer ".Length..].Trim();
            if (value.Length > 0) return value;
        }
        var header = ctx.Request.Headers["X-Api-Key"];
        if (!string.IsNullOrWhiteSpace(header)) return header.Trim();

        // The query-string fallback exists for clients that cannot set a
        // header. The dashboard no longer needs it for ordinary requests —
        // media has its own signed links and everything else is same-origin,
        // so the cookie travels by itself — but scripts, players and the one
        // browser API that has no choice still rely on it. A URL is a leaky
        // place for a credential (history, logs, Referer), so it is warned
        // about, but only where the warning names something the caller could
        // actually have done differently: see CanSendAHeader.
        var query = ctx.Request.QueryString["key"] ?? ctx.Request.QueryString["token"];
        if (string.IsNullOrWhiteSpace(query)) return null;
        if (CanSendAHeader(ctx.Request.Url?.AbsolutePath ?? "/")) NoteKeyInUrl();
        return query.Trim();
    }

    /// <summary>
    /// Endpoints whose clients have no way to send an Authorization header,
    /// so a credential in the URL is the only option open to them.
    ///
    /// EventSource is the entire list. The browser API cannot set a header on
    /// the request it opens — openLiveLink says so in its own comment — and
    /// the dashboard reopens that link every twenty seconds per open page. So
    /// the warning below fired at about six requests a minute, for ever,
    /// advising a change the caller cannot make: 537 lines of it in this
    /// install's logs, every one of them the server's own page. A third-party
    /// script genuinely putting a key in a URL would have been invisible in
    /// among them, which is the only case the warning exists to catch.
    ///
    /// Exact paths, and this list is about the warning alone — the credential
    /// itself is read and honoured exactly as before, here and everywhere.
    /// </summary>
    private static readonly HashSet<string> HeaderlessClients = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/server/session",
        // The close beacon, for the same reason: sendBeacon cannot set a
        // header, so a dashboard signed in with a device key has to put the
        // key in the URL to sign its close - which is what makes that close
        // count as the owner's decision. Without this, every closed or
        // refreshed tab tripped the "credentials are arriving in URLs" warning.
        "/api/server/closing",
    };

    /// <summary>Whether a caller on this path had the option of a header.</summary>
    internal static bool CanSendAHeader(string path) => !HeaderlessClients.Contains(path);

    private long _rejectedKeyCount;
    private long _rejectedKeyWarnedTicks;
    private static readonly TimeSpan RejectedKeyWarnInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Records a key or token that was presented and refused. Says so at most
    /// once a minute, with a running total and the address it last came from,
    /// so a run of guesses is one visible line per minute rather than either
    /// nothing at all or a log somebody can flood.
    /// </summary>
    private void NoteRejectedCredential(string client)
    {
        var total = Interlocked.Increment(ref _rejectedKeyCount);
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _rejectedKeyWarnedTicks);
        if (last != 0 && now - last < RejectedKeyWarnInterval.Ticks) return;
        if (Interlocked.CompareExchange(ref _rejectedKeyWarnedTicks, now, last) != last) return;
        Log.Warn("auth", $"rejected key/token from {client} — {total} refused since this server started");
    }

    private long _urlKeyCount;
    private long _urlKeyWarnedTicks;
    private static readonly TimeSpan UrlKeyWarnInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Warns that a credential arrived in a URL, at most once every ten
    /// minutes with a running count.
    ///
    /// This deliberately keeps no per-credential state. Remembering which
    /// credentials had already been warned about meant remembering a value
    /// an unauthenticated caller chooses, so a loop of requests carrying a
    /// different bogus ?key= each time grew the table without bound and
    /// wrote a log line every time — the opposite of what "warn once" was
    /// supposed to buy. Two counters can't be made to grow.
    /// </summary>
    private void NoteKeyInUrl()
    {
        var total = Interlocked.Increment(ref _urlKeyCount);
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _urlKeyWarnedTicks);
        if (last != 0 && now - last < UrlKeyWarnInterval.Ticks) return;
        // whoever wins the exchange writes the line; the rest stay quiet
        if (Interlocked.CompareExchange(ref _urlKeyWarnedTicks, now, last) != last) return;
        Log.Warn("auth", $"credentials are arriving in URLs (?key=/?token=), {total} so far — " +
                         "a header keeps them out of logs, history and Referer");
    }

    private static string? ReadSessionCookie(HttpListenerContext ctx)
    {
        // HttpListener's Cookies collection is fussy about some real-world
        // headers; parsing the raw value is more predictable.
        var raw = ctx.Request.Headers["Cookie"];
        if (string.IsNullOrEmpty(raw)) return null;
        foreach (var part in raw.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim() == CookieName)
            {
                var value = kv[1].Trim();
                if (value.Length > 0) return value;
            }
        }
        return null;
    }

    /// <param name="keyId">The key the session came with, if any.</param>
    private UserAccount? ResolveSession(string token, HttpListenerContext ctx, out string? keyId)
    {
        keyId = null;
        var id = Digest(token);
        if (!_sessions.TryGetValue(id, out var session)) return null;
        keyId = session.KeyId;

        var now = DateTime.UtcNow;
        if (LiveUser(session, now) is not UserAccount user)
        {
            // expired, or the account (or the key it came with) is gone —
            // drop it immediately
            _sessions.TryRemove(id, out _);
            return null;
        }
        session.LastSeenUtc = now;
        // Where it is being used, not where it was created.
        //
        // This was only ever set at sign-in, so the address stayed whatever it
        // was the moment somebody logged in and never moved again. On a server
        // set up at its own console — every account created there, every
        // session opened there — that means every line of "who is signed in"
        // reads back the server's own address for good, however far away the
        // person actually is now. Refreshing it here, beside the timestamp
        // that is already being slid forward, costs nothing and makes the
        // answer true.
        session.ClientHint = ClientKey(ctx);
        return user;
    }

    /// <summary>
    /// The account a session still speaks for, or null once it no longer
    /// does: idle too long, too old, the account deleted or disabled - or the
    /// key it came with revoked or expired.
    ///
    /// That last one is new. "Remember this device" hands back a session and
    /// a key together, and the Keys list promises that revoking a key stops
    /// anything using it at once. The browser was using the session, which
    /// knew nothing about the key, so it carried on for up to a week after
    /// the device had been "revoked". The one test, used for a request and
    /// for the signed-in list alike, so neither shows what the other refuses.
    /// </summary>
    private UserAccount? LiveUser(Session session, DateTime now)
    {
        if (now - session.LastSeenUtc > SessionIdle || now - session.CreatedUtc > SessionMax) return null;
        var user = _users.FindById(session.UserId);
        if (user is null || !user.Enabled) return null;
        if (session.KeyId is string key && !_users.KeyAlive(user, key)) return null;
        // The account's credentials have moved on since this session opened — a
        // password change, passwordless toggled. A session stamped with the
        // generation before that change no longer speaks for the account, even
        // if RevokeSessionsFor has not swept it yet, and even if it was opened
        // by a sign-in whose old-password hash read finished after the change.
        if (session.CredentialGeneration != _users.CredentialGenerationOf(user)) return null;
        return user;
    }

    // ---- login / logout ----

    public sealed record LoginOutcome(bool Ok, UserAccount? User, string? SessionToken, string? Error, int RetryAfterSeconds = 0);

    /// <summary>
    /// Verifies a password and opens a session. Failures are counted against
    /// both the account name and the source address, so neither a targeted
    /// nor a spray attack gets unlimited guesses; the error text is
    /// deliberately identical for "no such user" and "wrong password".
    /// </summary>
    /// <summary>
    /// Why a sign-in was refused, in words, for the log only. Never returned
    /// to the caller — see the call site.
    /// </summary>
    private string FailureReason(string? username)
    {
        var user = _users.FindByName(username);
        if (user is null) return " — no such account";
        if (!user.Enabled) return " — the account is disabled";
        if (!user.HasPassword && !user.Passwordless)
            return " — the account has no password and is not marked passwordless, so it can never sign in. " +
                   "Tick 'passwordless (read-only)' in Users, or give it a password";
        if (!user.HasPassword) return " — the account has no password";
        return " — wrong password";
    }

    public LoginOutcome Login(string? username, string? password, HttpListenerContext ctx)
    {
        var (client, proxy) = ClientOf(ctx);
        var nameKey = "user:" + (username?.Trim().ToLowerInvariant() ?? "");
        var addrKey = "addr:" + client;
        // Everything that came through the proxy, together, at a far higher
        // limit. The per-address lockout above now keys on the address the
        // proxy reports - but only a proxy that WRITES X-Forwarded-For makes
        // that address true. One that passes the client's own header along
        // (nginx with no proxy_set_header for it, a plain TCP forwarder) lets
        // a client name a fresh address for every guess, and the address
        // lockout never fires. This caps that: fifty failures through the
        // proxy, from anyone, and it locks - far past a household's typos,
        // well short of a password spray.
        var proxyKey = proxy is null ? null : "proxy:" + proxy;

        // Log the lockout-blocked attempts too, or "any attempt" isn't true:
        // once an account or address is locked, these returned before reaching
        // the failed-login line below, and the attempts vanished silently.
        if (LockedFor(nameKey) is int a && a > 0)
        {
            Log.Warn("auth", $"login attempt for '{username}' from {client} refused — account locked ({a}s left)");
            return new LoginOutcome(false, null, null, "too many failed attempts — try again shortly", a);
        }
        if (LockedFor(addrKey) is int b && b > 0)
        {
            Log.Warn("auth", $"login attempt for '{username}' from {client} refused — address locked ({b}s left)");
            return new LoginOutcome(false, null, null, "too many failed attempts — try again shortly", b);
        }
        if (proxyKey is not null && LockedFor(proxyKey) is int c && c > 0)
        {
            Log.Warn("auth", $"login attempt for '{username}' from {client} refused — too many failures through "
                             + $"the proxy at {proxy} ({c}s left)");
            return new LoginOutcome(false, null, null, "too many failed attempts — try again shortly", c);
        }

        // A deliberately-open, read-only account signs in on its username alone.
        // No password to verify, so nothing to throttle or brute-force; the
        // account is Read-only and was marked open on purpose in the Users
        // dialog. A password sent along with it is simply ignored.
        if (_users.FindPasswordless(username, out var guestGeneration) is UserAccount open)
        {
            // Successful passwordless sign-ins ARE rate-limited, per address.
            //
            // There is no password to fail, so the failure lockout never fired
            // here — and each sign-in is a session row, a users.json stamp and
            // a log line. An unauthenticated client could loop
            // POST /api/auth/login {"username":"guest"} and grow the session
            // table by millions of rows in a day, every one of them rewriting
            // users.json under the store lock that every request queues on. So
            // the successes themselves are counted, on a key of their own, and
            // an address doing this in a tight loop is told to wait — a
            // household opening the guest account a handful of times is not.
            var guestKey = "guest:" + client;
            ForgetQuietGuest(guestKey);
            if (LockedFor(guestKey) is int g && g > 0)
            {
                Log.Warn("auth", $"passwordless login for '{open.Username}' from {client} refused — "
                                 + $"too many in a row from this address ({g}s left)");
                return new LoginOutcome(false, null, null, "too many sign-ins from here — try again shortly", g);
            }

            // The name, but deliberately NOT the address.
            //
            // A passwordless sign-in proves nothing about who is at that
            // address — the account is open to anyone by definition — so
            // clearing the source lockout here handed out an unlimited reset:
            // spray passwords at a real account until the address locks, sign
            // in once as the open one from the same address, and both the
            // lockout and the PBKDF2 work it was rationing start over. Repeat
            // for as long as you like.
            //
            // The name key is still cleared, because a passwordless account has
            // no password to have failed against in the first place.
            _throttles.TryRemove(nameKey, out _);
            if (ReadSessionCookie(ctx) is string prior0) _sessions.TryRemove(Digest(prior0), out _);
            // count this success toward the per-address ceiling
            RegisterFailure(guestKey, MaxGuestSignInsInARow);
            _users.TouchLogin(open);
            Log.Info("auth", $"passwordless login: {open.Username} ({open.Role}) from {client}");
            BeforeGuestSession?.Invoke(open);
            // Stamped with the generation read when the account was found open,
            // not the one current now: if it was closed in between, this
            // session is dead on arrival, like a password sign-in in flight
            // across a password change.
            return new LoginOutcome(true, open, OpenSession(open, ctx, credentialGeneration: guestGeneration), null);
        }

        // Reserve the attempt against name and address BEFORE the hash, so a
        // burst cannot each get a full password check in the old check-then-act
        // gap. Name first; if the address is (now) locked, give the name
        // reservation back rather than leak it.
        var nameReserve = Reserve(nameKey, MaxFailuresBeforeLockout);
        if (nameReserve > 0)
        {
            Log.Warn("auth", $"login attempt for '{username}' from {client} refused — account locked ({nameReserve}s left)");
            return new LoginOutcome(false, null, null, "too many failed attempts — try again shortly", nameReserve);
        }
        var addrReserve = Reserve(addrKey, MaxFailuresBeforeLockout);
        if (addrReserve > 0)
        {
            ReleaseReservation(nameKey);
            Log.Warn("auth", $"login attempt for '{username}' from {client} refused — address locked ({addrReserve}s left)");
            return new LoginOutcome(false, null, null, "too many failed attempts — try again shortly", addrReserve);
        }

        // Captured before the hash, so a password change that lands during it
        // stamps this login's session with the OLD generation — see [41] in
        // OpenSession / LiveUser. A name that does not exist has no generation
        // to protect; VerifyPassword refuses it anyway.
        var probe = _users.FindByName(username);
        var credGen = probe is null ? 0 : _users.CredentialGenerationOf(probe);

        var user = _users.VerifyPassword(username, password);
        if (user is null)
        {
            SettleFailure(nameKey);
            SettleFailure(addrKey);
            // Not cleared by a success, unlike the two above: a sign-in that
            // works says nothing about the other people behind the proxy. It
            // lapses the way every counter does, an hour after it goes quiet.
            if (proxyKey is not null) RegisterFailure(proxyKey, MaxFailuresThroughProxy);
            // The reply stays deliberately vague — telling an anonymous caller
            // which half was wrong is how account names get enumerated. The
            // log is a different audience: it is the administrator's, it
            // already names accounts and addresses, and without the reason in
            // it a refusal that has an obvious cause looks like a mystery.
            //
            // The case that prompted this: an account created with no password
            // but never ticked "passwordless (read-only)" cannot sign in at
            // all — the passwordless branch above skips it because the flag is
            // off, and VerifyPassword refuses it because it has no password to
            // verify. Both ends are correct and the result is a working-looking
            // account that always says "invalid username or password".
            Log.Warn("auth", $"failed login for '{username}' from {client}{FailureReason(username)}");
            return new LoginOutcome(false, null, null, "invalid username or password");
        }

        _throttles.TryRemove(nameKey, out _);
        _throttles.TryRemove(addrKey, out _);

        // Retire whatever session the caller arrived holding. Signing in
        // should always hand back a fresh identifier, so a cookie planted
        // or observed beforehand isn't still valid afterwards.
        if (ReadSessionCookie(ctx) is string previous) _sessions.TryRemove(Digest(previous), out _);

        Log.Info("auth", $"login: {user.Username} ({user.Role}) from {client}");
        return new LoginOutcome(true, user, OpenSession(user, ctx, credentialGeneration: credGen), null);
    }

    /// <summary>
    /// The "current password" check behind changing your own password,
    /// throttled.
    ///
    /// That check is what stops a borrowed session (an unlocked laptop, a
    /// stolen cookie) becoming a permanent takeover. It was not throttled at
    /// all, so a borrowed session could simply ask again and again until it
    /// guessed right, as fast as PBKDF2 would answer.
    ///
    /// Counted per ACCOUNT, on a counter of its own - not the sign-in one.
    /// Anybody can fail sign-ins against a name they know, so sharing that
    /// counter meant a stranger could keep an account locked and, with it,
    /// stop its owner - already signed in, holding the right password -
    /// from changing it. Only a session on this account can move this one.
    /// Returns 0 when the password is right, -1 when it is wrong, or the
    /// seconds left on a lockout.
    /// </summary>
    public int VerifyOwnPassword(UserAccount user, string? password, HttpListenerContext ctx)
    {
        var client = ClientKey(ctx);
        var key = "pwchange:" + user.Id;
        // Reserved before the hash, so a borrowed session firing its guesses at
        // once cannot have them all checked in the old check-then-act gap.
        var reserved = Reserve(key, MaxFailuresBeforeLockout);
        if (reserved > 0)
        {
            Log.Warn("auth", $"password change for '{user.Username}' from {client} refused — too many wrong "
                             + $"current passwords ({reserved}s left)");
            return reserved;
        }
        if (_users.VerifyPassword(user.Username, password) is not null)
        {
            _throttles.TryRemove(key, out _);
            return 0;
        }
        SettleFailure(key);
        Log.Warn("auth", $"wrong current password for '{user.Username}' from {client} on a password change");
        return -1;
    }

    /// <summary>
    /// Starts a session for an already-authenticated user and returns the
    /// token to put in the cookie. Callers must have proved identity first —
    /// by password, or by presenting a valid key.
    /// </summary>
    /// <summary>The most sessions one account may hold at once. See OpenSession.</summary>
    private const int MaxSessionsPerUser = 64;

    /// <param name="keyId">The key this session is traded for, whose revocation should end it too.</param>
    /// <param name="credentialGeneration">
    /// The account's credential generation captured before the sign-in started
    /// hashing, or null to read it now. A password sign-in passes the captured
    /// value so that a change which landed during the hash leaves this session
    /// stamped with the old generation — dead on arrival (see LiveUser). Every
    /// other caller (a key trade, an own-password change that has already set
    /// the new password) wants the current generation.
    /// </param>
    public string OpenSession(UserAccount user, HttpListenerContext ctx, string? keyId = null,
                              int? credentialGeneration = null)
    {
        var token = UserStore.Base64Url(RandomNumberGenerator.GetBytes(32));
        _sessions[Digest(token)] = new Session
        {
            UserId = user.Id,
            CreatedUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
            ClientHint = ClientKey(ctx),
            KeyId = keyId,
            CredentialGeneration = credentialGeneration ?? _users.CredentialGenerationOf(user),
        };
        PruneSessions();
        CapSessionsFor(user.Id);
        SaveSessions();
        return token;
    }

    /// <summary>
    /// Keeps one account to a bounded number of sessions, evicting the ones
    /// that have gone quiet longest.
    ///
    /// Nothing bounded this before: a session was removed only after twelve
    /// hours idle, so anything that opens sessions in a loop grew the table
    /// without limit. An unauthenticated loop of passwordless sign-ins, or a
    /// remembered device trading its key for a session over and over, could add
    /// rows by the million — the memory is the table, and /api/status shipped
    /// the whole signed-in list to every admin dashboard every two seconds. A
    /// ceiling, and what goes is the least-recently-seen, so a page actually in
    /// use is the last to be dropped rather than the first.
    /// </summary>
    private void CapSessionsFor(string userId)
    {
        var mine = _sessions.Where(kv => kv.Value.UserId == userId)
                            .OrderByDescending(kv => kv.Value.LastSeenUtc)
                            .ToList();
        for (var i = MaxSessionsPerUser; i < mine.Count; i++)
            _sessions.TryRemove(mine[i].Key, out _);
    }

    /// <summary>
    /// Ties a session opened by a password sign-in to the "remember this
    /// device" key minted with it, so that revoking the device signs it out.
    /// The session exists first - the key is only made once the password has
    /// been accepted - so this is a second step rather than an argument.
    /// </summary>
    public void BindSessionToKey(string token, string keyId)
    {
        if (_sessions.TryGetValue(Digest(token), out var session)) session.KeyId = keyId;
    }

    /// <summary>
    /// Checks an RTSP <c>Authorization: Basic</c> header. Accepts an account
    /// username and password, or a key as the username (so a camera or a
    /// set-top box can be given something revocable instead of a password).
    ///
    /// Throttled on the same counters as the web login, and against the same
    /// account keys, so an attacker can't sidestep the lockout by moving to
    /// RTSP — and so a locked-out guess is refused before it costs a
    /// PBKDF2, which otherwise makes this a cheap way to burn the CPU.
    /// </summary>
    public bool VerifyRtspCredentials(string? header, string clientAddress)
    {
        var addrKey = "addr:" + clientAddress;
        if (LockedFor(addrKey) > 0) return false;

        if (string.IsNullOrEmpty(header)) return false;
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;

        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim())); }
        catch { return false; }

        var split = decoded.IndexOf(':');
        if (split < 0) return false;
        var username = decoded[..split];
        var password = decoded[(split + 1)..];

        // a key is high-entropy and can't be guessed, so it isn't throttled
        if (username.StartsWith(UserStore.KeyPrefix, StringComparison.Ordinal))
            return _users.VerifyKey(username) is not null;
        if (password.StartsWith(UserStore.KeyPrefix, StringComparison.Ordinal)
            && _users.VerifyKey(password) is not null)
            return true;

        var nameKey = "user:" + username.Trim().ToLowerInvariant();
        if (LockedFor(nameKey) > 0) return false;

        // Reserve before the hash, the same as the web login: RTSP dispatches a
        // request per thread too, and DESCRIBE/SETUP/PLAY plus keep-alives make
        // it a comfortable place to fire a burst of guesses. Without this every
        // guess that started inside the unlocked window got a full PBKDF2.
        if (Reserve(nameKey, MaxFailuresBeforeLockout) > 0) return false;
        if (Reserve(addrKey, MaxFailuresBeforeLockout) > 0) { ReleaseReservation(nameKey); return false; }

        if (_users.VerifyPassword(username, password) is not null)
        {
            _throttles.TryRemove(nameKey, out _);
            _throttles.TryRemove(addrKey, out _);
            return true;
        }

        SettleFailure(nameKey);
        SettleFailure(addrKey);
        Log.Warn("auth", $"failed RTSP login for '{username}' from {clientAddress}");
        return false;
    }

    /// <summary>
    /// Drops the session named by the request's cookie, if it has one, without
    /// the sign-out log line — for a key trade that is about to open a fresh
    /// session in its place. Bounded: it can only remove a session that exists.
    /// </summary>
    public void RetireCookieSession(HttpListenerContext ctx)
    {
        if (ReadSessionCookie(ctx) is string token) _sessions.TryRemove(Digest(token), out _);
    }

    public void Logout(HttpListenerContext ctx)
    {
        if (ReadSessionCookie(ctx) is not string token) return;
        // Named before it is dropped, so the log has both ends of a session
        // rather than only the sign-in. Bounded by definition: it takes a
        // session that existed to remove one.
        var who = _sessions.TryGetValue(Digest(token), out var s)
            ? _users.FindById(s.UserId)?.Username ?? "unknown" : null;
        if (!_sessions.TryRemove(Digest(token), out _)) return;
        Log.Info("auth", $"signed out: {who} from {ClientKey(ctx)}");
        SaveSessions();
    }

    /// <summary>Drops every session belonging to a user — used when their password changes or they are disabled.</summary>
    public void RevokeSessionsFor(string userId)
    {
        var changed = false;
        foreach (var (id, session) in _sessions)
            if (session.UserId == userId && _sessions.TryRemove(id, out _)) changed = true;
        if (changed) SaveSessions();
    }

    /// <summary>Number of live sessions for a user (dashboard display).</summary>
    public int SessionCountFor(string userId)
    {
        var now = DateTime.UtcNow;
        return _sessions.Count(s => s.Value.UserId == userId && LiveUser(s.Value, now) is not null);
    }

    /// <summary>How many accounts exist on this server, enabled or not.</summary>
    public int AccountCount => _users.All.Count;

    /// <summary>One live sign-in: who, from where, and how long since it spoke.</summary>
    public sealed record SignedIn(string Username, string Client, int IdleSeconds);

    /// <summary>
    /// Every sign-in that is live right now — one per session, so two browsers
    /// and a phone are three, whether or not they are the same account.
    ///
    /// The liveness test is the one <see cref="ResolveSession"/> applies when a
    /// request actually arrives: idle timeout, absolute lifetime, the account
    /// still existing and enabled, and any key it came with still valid
    /// (<see cref="LiveUser"/>). Reading the raw dictionary instead
    /// would list sessions that have expired but not yet been swept — sign-ins
    /// that look present while the next request from any of them would be
    /// refused.
    /// </summary>
    public IReadOnlyList<SignedIn> SignedInSessions
    {
        get
        {
            var now = DateTime.UtcNow;
            var live = new List<SignedIn>();
            foreach (var (_, s) in _sessions)
            {
                if (LiveUser(s, now) is not UserAccount user) continue;
                live.Add(new SignedIn(user.Username, s.ClientHint,
                                      (int)(now - s.LastSeenUtc).TotalSeconds));
            }
            return live.OrderBy(l => l.Username, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(l => l.IdleSeconds)
                       .ToList();
        }
    }

    /// <summary>How many sign-ins are live — sessions, not accounts.</summary>
    public int SignedInCount => SignedInSessions.Count;

    // ---- cookie plumbing ----

    /// <summary>
    /// HttpOnly so no script can read it, SameSite=Strict so no other site
    /// can make the browser send it, Path=/ so it covers the whole API, and
    /// no Max-Age — a session cookie dies with the browser session while the
    /// server-side idle timeout handles the rest. Secure is set only on
    /// HTTPS: on a plain-HTTP LAN bind it would make the cookie unusable.
    /// </summary>
    /// <summary>
    /// Whether this request reached the user over TLS — directly, or through
    /// a reverse proxy that terminated it and said so.
    ///
    /// <c>X-Forwarded-Proto</c> is a claim, not a fact: anyone who can reach
    /// the port can send it. It is believed only from loopback, which is
    /// where a proxy on this machine connects from and where a remote
    /// attacker cannot put themselves. Believing it from anywhere else would
    /// let a plain-HTTP client talk the server into marking its cookie
    /// Secure — and a Secure cookie is one the browser then refuses to send
    /// back over the plain connection it actually has.
    /// </summary>
    public static bool IsSecureRequest(HttpListenerContext ctx)
    {
        if (ctx.Request.IsSecureConnection) return true;
        if (!IPAddress.IsLoopback(ctx.Request.RemoteEndPoint?.Address ?? IPAddress.None)) return false;
        var proto = ctx.Request.Headers["X-Forwarded-Proto"];
        return proto is not null
               && proto.Split(',')[0].Trim().Equals("https", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetSessionCookie(HttpListenerContext ctx, string token)
    {
        var secure = IsSecureRequest(ctx) ? "; Secure" : "";
        ctx.Response.Headers.Add("Set-Cookie",
            $"{CookieName}={token}; Path=/; HttpOnly; SameSite=Strict{secure}");
    }

    public static void ClearSessionCookie(HttpListenerContext ctx)
    {
        ctx.Response.Headers.Add("Set-Cookie",
            $"{CookieName}=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0");
    }

    // ---- throttling ----

    private int LockedFor(string key)
    {
        if (!_throttles.TryGetValue(key, out var t)) return 0;
        DateTime until;
        lock (t) until = t.LockedUntilUtc;   // read under the same lock that writes it
        var remaining = (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds);
        return remaining > 0 ? remaining : 0;
    }

    /// <summary>
    /// Starts an address's passwordless count again once it has gone quiet.
    ///
    /// That count is of successes, so the way out every failure count has - a
    /// sign-in that works - is the very thing it adds to, and throttles are
    /// only swept once there are 64 of them. Without this it counted for the
    /// life of the server: the sixth guest sign-in from one address since the
    /// server started was refused, and every one after it for longer, up to a
    /// quarter of an hour. A lock still running is left to run out.
    /// </summary>
    private void ForgetQuietGuest(string key)
    {
        if (!_throttles.TryGetValue(key, out var t)) return;
        bool quiet;
        lock (t) quiet = DateTime.UtcNow - t.LastFailureUtc > GuestSignInQuiet && t.LockedUntilUtc <= DateTime.UtcNow;
        if (quiet) _throttles.TryRemove(new KeyValuePair<string, Throttle>(key, t));
    }

    private void RegisterFailure(string key, int threshold = MaxFailuresBeforeLockout)
    {
        var t = _throttles.GetOrAdd(key, _ => new Throttle());
        lock (t)
        {
            t.Failures++;
            t.LastFailureUtc = DateTime.UtcNow;
            if (t.Failures >= threshold) SetLock(t, threshold);
        }
        PruneThrottles();
    }

    /// <summary>The escalating lock: 5th failure → 15 s, doubling to a 15-minute ceiling. Under the Throttle's lock.</summary>
    private static void SetLock(Throttle t, int threshold)
    {
        var steps = Math.Min(t.Failures - threshold, 8);
        var seconds = Math.Min(15 * Math.Pow(2, steps), 900);
        t.LockedUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
    }

    /// <summary>
    /// Reserves one attempt against a key BEFORE the ~100 ms password hash, or
    /// returns the seconds left if the key is (now) locked.
    ///
    /// The lockout was check-then-act: LockedFor was read, then PBKDF2 ran, and
    /// only afterwards was the failure recorded. Every guess dispatched inside
    /// that window passed the check and got a full hash, so a burst of 64
    /// concurrent guesses was 64 guesses per lockout window rather than five,
    /// and each window reopened the same way. Reserving counts the in-flight
    /// attempt up front, under the throttle lock, so no more are checked at once
    /// than there are guesses left (one, once a lock has run out); the rest are
    /// told to wait a second. Settle a reservation with <see cref="SettleFailure"/>
    /// (the guess was wrong) or <see cref="ReleaseReservation"/> (it succeeded,
    /// or is being abandoned before the hash).
    /// </summary>
    private int Reserve(string key, int threshold)
    {
        var t = _throttles.GetOrAdd(key, _ => new Throttle());
        lock (t)
        {
            var remaining = (int)Math.Ceiling((t.LockedUntilUtc - DateTime.UtcNow).TotalSeconds);
            if (remaining > 0) return remaining;
            // As many checked at once as there are guesses left before the
            // lock, and never fewer than one. Once a lock has run out the count
            // is still at the limit, and one attempt is checked, as it always
            // was - a wrong one locks again for longer, a right one clears it.
            // This first counted "failures + in flight" against the limit,
            // which after a lock is always reached: every attempt, the right
            // password included, was refused and relocked for longer, so five
            // wrong guesses by anyone locked the account (or the address) until
            // the server restarted.
            //
            // An attempt refused here was never checked, so it is not counted
            // as a failure either: several players signing in to one account at
            // the same moment are not guesses.
            if (t.InFlight >= Math.Max(1, threshold - t.Failures)) return 1;
            t.InFlight++;
            return 0;
        }
    }

    /// <summary>Gives back a reservation that will not become a failure — a success, or an attempt abandoned before hashing.</summary>
    private void ReleaseReservation(string key)
    {
        if (_throttles.TryGetValue(key, out var t))
            lock (t) { if (t.InFlight > 0) t.InFlight--; }
    }

    /// <summary>Turns a reservation into a recorded failure: the guess was checked and was wrong.</summary>
    private void SettleFailure(string key, int threshold = MaxFailuresBeforeLockout)
    {
        var t = _throttles.GetOrAdd(key, _ => new Throttle());
        lock (t)
        {
            if (t.InFlight > 0) t.InFlight--;
            t.Failures++;
            t.LastFailureUtc = DateTime.UtcNow;
            if (t.Failures >= threshold) SetLock(t, threshold);
        }
        PruneThrottles();
    }

    /// <summary>
    /// Drops counters that have gone quiet. Without this, one entry per
    /// attempted username and per source address accumulates for the life of
    /// the process — slow, because the address lockout limits the rate, but
    /// still a table an outsider decides the size of.
    /// </summary>
    private void PruneThrottles()
    {
        // an entry is only interesting while its lockout could still bite,
        // plus a grace period so the escalation isn't reset by waiting
        if (_throttles.Count < 64) return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        foreach (var (key, t) in _throttles)
        {
            bool stale;
            lock (t) stale = t.LastFailureUtc < cutoff && t.LockedUntilUtc < DateTime.UtcNow;
            if (stale) _throttles.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Who is on the other end, for lockouts, the log and "who is signed in".
    ///
    /// Behind a reverse proxy on this machine - the way the README says to add
    /// TLS - every request arrives from 127.0.0.1. That made the whole world
    /// one client: five wrong guesses from anyone locked the address, and the
    /// address was everybody's, the owner's included. So from loopback, and
    /// only from loopback, the proxy's X-Forwarded-For is believed, and only
    /// its last entry: that is the one the proxy itself wrote, where anything
    /// to the left of it is whatever the client chose to send. From anywhere
    /// else the header is ignored, the same rule IsSecureRequest applies to
    /// X-Forwarded-Proto. A program on this machine can claim any address it
    /// likes this way - but it is already on the machine, and the per-account
    /// lockout does not depend on the address at all. So can a client of a
    /// proxy that forwards the header it was given rather than writing its
    /// own, which is why Login also counts failures for the proxy as a whole.
    /// </summary>
    private static string ClientKey(HttpListenerContext ctx) => ClientOf(ctx).Client;

    /// <summary>
    /// The client, and the proxy it came through when the address was taken
    /// from X-Forwarded-For. See ClientKey, and Login for what the proxy is for.
    /// </summary>
    private static (string Client, string? Proxy) ClientOf(HttpListenerContext ctx)
    {
        var peer = ctx.Request.RemoteEndPoint?.Address;
        if (peer is null) return ("unknown", null);
        if (IPAddress.IsLoopback(peer)
            && ctx.Request.Headers["X-Forwarded-For"] is { Length: > 0 } forwarded
            && IPAddress.TryParse(forwarded.Split(',')[^1].Trim(), out var client))
            return (client.ToString(), peer.ToString());
        return (peer.ToString(), null);
    }

    private void PruneSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, s) in _sessions)
            if (now - s.LastSeenUtc > SessionIdle || now - s.CreatedUtc > SessionMax)
                _sessions.TryRemove(id, out _);

        // Failed-login counters were only cleared by a *successful* login for
        // that same name or address. A wrong username, or an address that
        // never gets in, therefore left an entry for the life of the process -
        // and this server listens on the network, so anyone can add as many as
        // they like. An hour after the last failure the counter has done its
        // job and the lockout has long expired.
        foreach (var (key, t) in _throttles)
            if (now - t.LastFailureUtc > TimeSpan.FromHours(1) && now > t.LockedUntilUtc)
                _throttles.TryRemove(key, out _);
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        if (x.Length != y.Length) return false;
        return CryptographicOperations.FixedTimeEquals(x, y);
    }
}
