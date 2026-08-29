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
public sealed record AuthResult(AccessLevel Level, UserAccount? User, string Method)
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
/// Sessions survive a restart: the table is kept in a sessions.json sidecar
/// as token digests, so an update or a reboot doesn't sign everybody out.
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
    }

    private sealed class Throttle
    {
        public int Failures;
        public DateTime LockedUntilUtc;
        public DateTime LastFailureUtc = DateTime.UtcNow;
    }

    private readonly UserStore _users;
    private readonly string _legacyToken;
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
    {
        _users = users;
        _legacyToken = legacyToken ?? "";
        _sessionFile = baseDirectory is null ? "" : Path.Combine(baseDirectory, "sessions.json");
        LoadSessions();
    }

    private void LoadSessions()
    {
        if (_sessionFile.Length == 0 || !File.Exists(_sessionFile)) return;
        Services.SecretFile.Protect(_sessionFile);   // an older build's file, on the way past
        try
        {
            var stored = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Session>>(
                File.ReadAllText(_sessionFile));
            if (stored is null) return;
            var now = DateTime.UtcNow;
            var kept = 0;
            foreach (var (id, s) in stored)
            {
                // an expired session is not worth restoring, and neither is
                // one whose account has since gone or been disabled
                if (now - s.LastSeenUtc > SessionIdle || now - s.CreatedUtc > SessionMax) continue;
                if (_users.FindById(s.UserId) is not { Enabled: true }) continue;
                _sessions[id] = s;
                kept++;
            }
            if (kept > 0) Log.Info("auth", $"restored {kept} signed-in session(s)");
        }
        catch (Exception ex)
        {
            // a damaged file costs everyone a sign-in, which is recoverable;
            // refusing to start is not
            Log.Warn("auth", $"could not read sessions.json ({ex.Message}) — everyone will sign in again");
        }
    }

    /// <summary>
    /// Writes the session table. Called after anything that changes it; the
    /// idle-timestamp slide on every request deliberately does not, or a
    /// dashboard poll would rewrite this file every two seconds.
    /// </summary>
    private readonly object _saveLock = new();

    private void SaveSessions()
    {
        if (_sessionFile.Length == 0) return;
        // Signing in, signing out and revoking all reach this from different
        // request threads. Two of them writing at once used to interleave
        // into one temp file and move the mess over the real one — and an
        // unreadable sessions.json signs everybody out on the next start,
        // which is the thing the file exists to prevent. One writer at a
        // time, and a temp name that cannot be shared even so.
        lock (_saveLock)
        {
            var tmp = $"{_sessionFile}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
            try
            {
                File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(
                    _sessions, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, _sessionFile, overwrite: true);
                Services.SecretFile.Protect(_sessionFile);
            }
            catch (Exception ex)
            {
                Log.Warn("auth", $"could not save sessions.json: {ex.Message}");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }

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

        // legacy control.authToken — still honoured, still full rights
        if (_legacyToken.Length > 0 && presented is not null && FixedTimeEquals(presented, _legacyToken))
            return new AuthResult(AccessLevel.Admin, null, "token");

        if (presented is not null && _users.VerifyKey(presented) is UserAccount keyUser)
            return new AuthResult(LevelOf(keyUser), keyUser, "key");

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

        if (ReadSessionCookie(ctx) is string token && ResolveSession(token, ctx) is UserAccount sessionUser)
            return new AuthResult(LevelOf(sessionUser), sessionUser, "session");

        // No accounts yet: whoever reaches an unclaimed server is its owner,
        // top tier included — otherwise a fresh install would hide the log
        // from the only person there.
        if (!Enforcing) return new AuthResult(AccessLevel.ServerAdmin, null, "open");

        return AuthResult.Anonymous;
    }

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
        // header. The dashboard no longer needs it — media has its own
        // signed links and everything else is same-origin, so the cookie
        // travels by itself — but scripts and players may still rely on it.
        // A URL is a leaky place for a credential (history, logs, Referer),
        // so it is warned about once per key and never accepted for
        // anything but a key.
        var query = ctx.Request.QueryString["key"] ?? ctx.Request.QueryString["token"];
        if (string.IsNullOrWhiteSpace(query)) return null;
        NoteKeyInUrl();
        return query.Trim();
    }

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

    private UserAccount? ResolveSession(string token, HttpListenerContext ctx)
    {
        var id = Digest(token);
        if (!_sessions.TryGetValue(id, out var session)) return null;

        var now = DateTime.UtcNow;
        if (now - session.LastSeenUtc > SessionIdle || now - session.CreatedUtc > SessionMax)
        {
            _sessions.TryRemove(id, out _);
            return null;
        }
        var user = _users.FindById(session.UserId);
        if (user is null || !user.Enabled)
        {
            // account deleted or disabled mid-session — drop it immediately
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

    // ---- login / logout ----

    public sealed record LoginOutcome(bool Ok, UserAccount? User, string? SessionToken, string? Error, int RetryAfterSeconds = 0);

    /// <summary>
    /// Verifies a password and opens a session. Failures are counted against
    /// both the account name and the source address, so neither a targeted
    /// nor a spray attack gets unlimited guesses; the error text is
    /// deliberately identical for "no such user" and "wrong password".
    /// </summary>
    public LoginOutcome Login(string? username, string? password, HttpListenerContext ctx)
    {
        var client = ClientKey(ctx);
        var nameKey = "user:" + (username?.Trim().ToLowerInvariant() ?? "");
        var addrKey = "addr:" + client;

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

        // A deliberately-open, read-only account signs in on its username alone.
        // No password to verify, so nothing to throttle or brute-force; the
        // account is Read-only and was marked open on purpose in the Users
        // dialog. A password sent along with it is simply ignored.
        if (_users.FindPasswordless(username) is UserAccount open)
        {
            _throttles.TryRemove(nameKey, out _);
            _throttles.TryRemove(addrKey, out _);
            if (ReadSessionCookie(ctx) is string prior0) _sessions.TryRemove(Digest(prior0), out _);
            _users.TouchLogin(open);
            Log.Info("auth", $"passwordless login: {open.Username} ({open.Role}) from {client}");
            return new LoginOutcome(true, open, OpenSession(open, ctx), null);
        }

        var user = _users.VerifyPassword(username, password);
        if (user is null)
        {
            RegisterFailure(nameKey);
            RegisterFailure(addrKey);
            Log.Warn("auth", $"failed login for '{username}' from {client}");
            return new LoginOutcome(false, null, null, "invalid username or password");
        }

        _throttles.TryRemove(nameKey, out _);
        _throttles.TryRemove(addrKey, out _);

        // Retire whatever session the caller arrived holding. Signing in
        // should always hand back a fresh identifier, so a cookie planted
        // or observed beforehand isn't still valid afterwards.
        if (ReadSessionCookie(ctx) is string previous) _sessions.TryRemove(Digest(previous), out _);

        Log.Info("auth", $"login: {user.Username} ({user.Role}) from {client}");
        return new LoginOutcome(true, user, OpenSession(user, ctx), null);
    }

    /// <summary>
    /// Starts a session for an already-authenticated user and returns the
    /// token to put in the cookie. Callers must have proved identity first —
    /// by password, or by presenting a valid key.
    /// </summary>
    public string OpenSession(UserAccount user, HttpListenerContext ctx)
    {
        var token = UserStore.Base64Url(RandomNumberGenerator.GetBytes(32));
        _sessions[Digest(token)] = new Session
        {
            UserId = user.Id,
            CreatedUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
            ClientHint = ClientKey(ctx),
        };
        PruneSessions();
        SaveSessions();
        return token;
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

        if (_users.VerifyPassword(username, password) is not null)
        {
            _throttles.TryRemove(nameKey, out _);
            _throttles.TryRemove(addrKey, out _);
            return true;
        }

        RegisterFailure(nameKey);
        RegisterFailure(addrKey);
        Log.Warn("auth", $"failed RTSP login for '{username}' from {clientAddress}");
        return false;
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
    public int SessionCountFor(string userId) => _sessions.Count(s => s.Value.UserId == userId);

    /// <summary>How many accounts exist on this server, enabled or not.</summary>
    public int AccountCount => _users.All.Count;

    /// <summary>One live sign-in: who, from where, and how long since it spoke.</summary>
    public sealed record SignedIn(string Username, string Client, int IdleSeconds);

    /// <summary>
    /// Every sign-in that is live right now — one per session, so two browsers
    /// and a phone are three, whether or not they are the same account.
    ///
    /// The liveness test is the one <see cref="ResolveSession"/> applies when a
    /// request actually arrives: idle timeout, absolute lifetime, and the
    /// account still existing and enabled. Reading the raw dictionary instead
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
                if (now - s.LastSeenUtc > SessionIdle || now - s.CreatedUtc > SessionMax) continue;
                var user = _users.FindById(s.UserId);
                if (user is null || !user.Enabled) continue;
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

    private void RegisterFailure(string key)
    {
        var t = _throttles.GetOrAdd(key, _ => new Throttle());
        lock (t)
        {
            t.Failures++;
            t.LastFailureUtc = DateTime.UtcNow;
            if (t.Failures >= MaxFailuresBeforeLockout)
            {
                // 5th failure → 15 s, doubling to a 15 minute ceiling
                var steps = Math.Min(t.Failures - MaxFailuresBeforeLockout, 8);
                var seconds = Math.Min(15 * Math.Pow(2, steps), 900);
                t.LockedUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
            }
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

    private static string ClientKey(HttpListenerContext ctx) =>
        ctx.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";

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
