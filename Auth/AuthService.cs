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
/// Sessions live in memory only: a server restart signs everyone out, while
/// their keys keep working. Failed logins are throttled per account and per
/// source address with an escalating lockout.
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
        public required string UserId { get; init; }
        public required DateTime CreatedUtc { get; init; }
        public DateTime LastSeenUtc { get; set; }
        public string ClientHint { get; init; } = "";
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

    public AuthService(UserStore users, string legacyToken)
    {
        _users = users;
        _legacyToken = legacyToken ?? "";
    }

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

        if (ReadSessionCookie(ctx) is string token && ResolveSession(token) is UserAccount sessionUser)
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

    private UserAccount? ResolveSession(string token)
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

        if (LockedFor(nameKey) is int a && a > 0)
            return new LoginOutcome(false, null, null, "too many failed attempts — try again shortly", a);
        if (LockedFor(addrKey) is int b && b > 0)
            return new LoginOutcome(false, null, null, "too many failed attempts — try again shortly", b);

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
        if (ReadSessionCookie(ctx) is string token) _sessions.TryRemove(Digest(token), out _);
    }

    /// <summary>Drops every session belonging to a user — used when their password changes or they are disabled.</summary>
    public void RevokeSessionsFor(string userId)
    {
        foreach (var (id, session) in _sessions)
            if (session.UserId == userId) _sessions.TryRemove(id, out _);
    }

    /// <summary>Number of live sessions for a user (dashboard display).</summary>
    public int SessionCountFor(string userId) => _sessions.Count(s => s.Value.UserId == userId);

    // ---- cookie plumbing ----

    /// <summary>
    /// HttpOnly so no script can read it, SameSite=Strict so no other site
    /// can make the browser send it, Path=/ so it covers the whole API, and
    /// no Max-Age — a session cookie dies with the browser session while the
    /// server-side idle timeout handles the rest. Secure is set only on
    /// HTTPS: on a plain-HTTP LAN bind it would make the cookie unusable.
    /// </summary>
    public static void SetSessionCookie(HttpListenerContext ctx, string token)
    {
        var secure = ctx.Request.IsSecureConnection ? "; Secure" : "";
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
