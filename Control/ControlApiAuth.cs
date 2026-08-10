using System.Net;
using System.Text;
using System.Text.Json;
using J0kersMediaServer.Auth;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Control;

/// <summary>
/// Accounts, sessions and keys — the half of the control API that decides
/// who the rest of it will talk to.
///
///   GET    /api/auth/state      is auth on, is setup needed, who am I
///   POST   /api/auth/setup      claim the first admin account (first run only)
///   POST   /api/auth/login      username + password → session cookie
///   POST   /api/auth/logout     drop this session
///   POST   /api/auth/password   change my own password
///   GET    /api/auth/keys       my keys
///   POST   /api/auth/keys       mint one for me (secret returned once)
///   DELETE /api/auth/keys?id=   revoke one of mine
///   GET    /api/users           list accounts                      (admin)
///   POST   /api/users           create an account                  (admin)
///   PUT    /api/users?id=       edit an account                    (admin)
///   DELETE /api/users?id=       remove an account                  (admin)
///   POST   /api/users/keys?id=  mint a key for someone else        (admin)
///   DELETE /api/users/keys?id=&amp;keyId=  revoke someone's key    (admin)
/// </summary>
public sealed partial class ControlApi
{
    private sealed record LoginRequest(string? username, string? password, bool? remember);
    private sealed record PasswordRequest(string? currentPassword, string? newPassword);
    private sealed record KeyRequest(string? label, int? days);
    private sealed record UserRequest(string? username, string? password, string? displayName, string? role, bool? enabled);

    /// <summary>
    /// Handles every /api/auth/* path. Returns false when the path isn't
    /// one of ours, so the caller can carry on routing.
    /// </summary>
    private bool HandleAuthRoutes(HttpListenerContext ctx, AuthResult auth, string method, string path)
    {
        var res = ctx.Response;

        switch (method, path)
        {
            case ("GET", "/api/auth/state"):
                WriteJson(res, 200, new
                {
                    // "open" means no admin account exists yet — the server
                    // behaves as it did before accounts existed
                    authRequired = _auth.Enforcing,
                    setupRequired = _auth.SetupRequired,
                    authenticated = auth.Level != AccessLevel.None,
                    // a plain-HTTP bind beyond loopback means the password
                    // crosses the wire in the clear; the UI says so
                    // TLS, or a reverse proxy that terminated it, or the
                    // password never leaving the machine at all
                    secure = AuthService.IsSecureRequest(ctx)
                             || Hls.HttpListenerBinder.IsLoopbackRequest(ctx),
                    user = Describe(auth),
                });
                return true;

            case ("POST", "/api/auth/setup"):
                Setup(ctx);
                return true;

            case ("POST", "/api/auth/login"):
                Login(ctx);
                return true;

            // Trades a key for a session cookie. The gate on GET / can only
            // read cookies, but a "remembered" device holds its key in
            // browser storage — which nothing can read until a page has
            // loaded. The sign-in page cashes the key in here on load so a
            // remembered browser goes straight through to the dashboard.
            case ("POST", "/api/auth/session"):
            {
                if (auth.Method != "key" || auth.User is null)
                {
                    WriteJson(res, 401, new { error = "a valid key is required" });
                    return true;
                }
                var token = _auth.OpenSession(auth.User, ctx);
                AuthService.SetSessionCookie(ctx, token);
                WriteJson(res, 200, new { user = DescribeUser(auth.User, auth) });
                return true;
            }

            case ("POST", "/api/auth/logout"):
                _auth.Logout(ctx);
                AuthService.ClearSessionCookie(ctx);
                WriteJson(res, 200, new { loggedOut = true });
                return true;

            case ("GET", "/api/auth/me"):
                if (RequireUser(ctx, auth) is null) return true;
                WriteJson(res, 200, new { user = Describe(auth) });
                return true;

            case ("POST", "/api/auth/password"):
                ChangeOwnPassword(ctx, auth);
                return true;

            case ("GET", "/api/auth/keys"):
            {
                if (RequireUser(ctx, auth) is not UserAccount me) return true;
                WriteJson(res, 200, new { keys = me.Keys.Select(DescribeKey) });
                return true;
            }

            case ("POST", "/api/auth/keys"):
            {
                if (RequireUser(ctx, auth) is not UserAccount me) return true;
                IssueKey(ctx, me);
                return true;
            }

            case ("DELETE", "/api/auth/keys"):
            {
                if (RequireUser(ctx, auth) is not UserAccount me) return true;
                var id = ctx.Request.QueryString["id"] ?? "";
                if (_auth.Users.RevokeKey(me, id)) WriteJson(res, 200, new { revoked = id });
                else WriteJson(res, 404, new { error = "unknown key" });
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The /api/users/* admin surface. Reached only after the caller has
    /// been confirmed as an admin by the authorization gate in Handle().
    /// </summary>
    private bool HandleUserRoutes(HttpListenerContext ctx, AuthResult auth, string method, string path)
    {
        var res = ctx.Response;
        var store = _auth.Users;

        switch (method, path)
        {
            case ("GET", "/api/users"):
                WriteJson(res, 200, new
                {
                    users = store.All.Select(u => DescribeUser(u, auth)),
                    roles = UserStore.Roles,
                });
                return true;

            case ("POST", "/api/users"):
                CreateUser(ctx, auth);
                return true;

            case ("PUT", "/api/users"):
                EditUser(ctx, auth);
                return true;

            case ("DELETE", "/api/users"):
            {
                var user = store.FindById(ctx.Request.QueryString["id"]);
                if (user is null) { WriteJson(res, 404, new { error = "unknown user" }); return true; }
                if (auth.User is not null && auth.User.Id == user.Id)
                {
                    WriteJson(res, 400, new { error = "you cannot delete the account you are signed in with" });
                    return true;
                }
                try { store.Delete(user); }
                catch (InvalidOperationException ex) { WriteJson(res, 409, new { error = ex.Message }); return true; }
                _auth.RevokeSessionsFor(user.Id);
                WriteJson(res, 200, new { removed = user.Username });
                return true;
            }

            case ("POST", "/api/users/keys"):
            {
                var user = store.FindById(ctx.Request.QueryString["id"]);
                if (user is null) { WriteJson(res, 404, new { error = "unknown user" }); return true; }
                IssueKey(ctx, user);
                return true;
            }

            case ("DELETE", "/api/users/keys"):
            {
                var user = store.FindById(ctx.Request.QueryString["id"]);
                if (user is null) { WriteJson(res, 404, new { error = "unknown user" }); return true; }
                var keyId = ctx.Request.QueryString["keyId"] ?? "";
                if (store.RevokeKey(user, keyId)) WriteJson(res, 200, new { revoked = keyId });
                else WriteJson(res, 404, new { error = "unknown key" });
                return true;
            }
        }

        return false;
    }

    // ---- first run ----

    /// <summary>
    /// POST /api/auth/setup — creates the very first administrator, and
    /// signs them in. Refused once any account exists.
    /// </summary>
    private void Setup(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        if (!_auth.SetupRequired)
        {
            WriteJson(res, 409, new { error = "accounts already exist — sign in instead" });
            return;
        }
        if (!TryReadJson<LoginRequest>(ctx, out var req, out var error))
        {
            WriteJson(res, 400, new { error });
            return;
        }
        if (UserStore.ValidateUsername(req!.username) is string nameError)
        {
            WriteJson(res, 400, new { error = nameError });
            return;
        }
        if (UserStore.ValidatePassword(req.password) is string passwordError)
        {
            WriteJson(res, 400, new { error = passwordError });
            return;
        }

        var admin = _auth.Users.Create(req.username!, req.password, UserStore.RoleAdmin, req.username, enabled: true);
        Log.Info("auth", $"administrator '{admin.Username}' created — configuration is now protected");

        // sign the new admin straight in rather than bouncing them to a login form
        var outcome = _auth.Login(req.username, req.password, ctx);
        if (outcome is { Ok: true, SessionToken: not null })
            AuthService.SetSessionCookie(ctx, outcome.SessionToken);

        string? deviceKey = null;
        if (req.remember == true)
            deviceKey = _auth.Users.CreateKey(admin, DeviceLabel(ctx), AuthService.DeviceKeyLifetime).secret;

        WriteJson(res, 200, new { user = DescribeUser(admin, null), key = deviceKey });
    }

    // ---- login ----

    private void Login(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        if (!TryReadJson<LoginRequest>(ctx, out var req, out var error))
        {
            WriteJson(res, 400, new { error });
            return;
        }

        var outcome = _auth.Login(req!.username, req.password, ctx);
        if (!outcome.Ok || outcome.SessionToken is null || outcome.User is null)
        {
            if (outcome.RetryAfterSeconds > 0)
                res.Headers["Retry-After"] = outcome.RetryAfterSeconds.ToString();
            WriteJson(res, outcome.RetryAfterSeconds > 0 ? 429 : 401,
                new { error = outcome.Error ?? "invalid username or password", retryAfterSeconds = outcome.RetryAfterSeconds });
            return;
        }

        AuthService.SetSessionCookie(ctx, outcome.SessionToken);

        // "remember this device": a long-lived key the dashboard stores and
        // sends as a bearer token, so this browser (or a player, or a
        // script) never sees the login form again. Shown exactly once.
        string? deviceKey = null;
        if (req.remember == true)
            deviceKey = _auth.Users.CreateKey(outcome.User, DeviceLabel(ctx), AuthService.DeviceKeyLifetime).secret;

        WriteJson(res, 200, new { user = DescribeUser(outcome.User, null), key = deviceKey });
    }

    /// <summary>A human-readable name for a "remember this device" key, from the browser's UA string.</summary>
    private static string DeviceLabel(HttpListenerContext ctx)
    {
        var ua = ctx.Request.UserAgent ?? "";
        var os = ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
            : ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "iPhone"
            : ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iPad"
            : ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) ? "Mac"
            : ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
            : ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows"
            : "device";
        var browser = ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? "Edge"
            : ua.Contains("Firefox", StringComparison.OrdinalIgnoreCase) ? "Firefox"
            : ua.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ? "Chrome"
            : ua.Contains("Safari", StringComparison.OrdinalIgnoreCase) ? "Safari"
            : "browser";
        return $"{os} · {browser}";
    }

    // ---- own password ----

    private void ChangeOwnPassword(HttpListenerContext ctx, AuthResult auth)
    {
        var res = ctx.Response;
        if (RequireUser(ctx, auth) is not UserAccount me) return;
        if (!TryReadJson<PasswordRequest>(ctx, out var req, out var error))
        {
            WriteJson(res, 400, new { error });
            return;
        }
        // proving knowledge of the current password is what stops a borrowed
        // session (an unlocked laptop, a stolen cookie) becoming a permanent
        // takeover of the account
        if (me.HasPassword && _auth.Users.VerifyPassword(me.Username, req!.currentPassword) is null)
        {
            WriteJson(res, 403, new { error = "current password is incorrect" });
            return;
        }
        if (UserStore.ValidatePassword(req!.newPassword) is string passwordError)
        {
            WriteJson(res, 400, new { error = passwordError });
            return;
        }

        _auth.Users.SetPassword(me, req.newPassword!);
        // every other session for this account dies with the old password
        _auth.RevokeSessionsFor(me.Id);
        var outcome = _auth.Login(me.Username, req.newPassword, ctx);
        if (outcome is { Ok: true, SessionToken: not null })
            AuthService.SetSessionCookie(ctx, outcome.SessionToken);
        Log.Info("auth", $"password changed for {me.Username}");
        WriteJson(res, 200, new { changed = true, note = "other sessions for this account were signed out" });
    }

    // ---- account administration ----

    private void CreateUser(HttpListenerContext ctx, AuthResult auth)
    {
        var res = ctx.Response;
        if (!TryReadJson<UserRequest>(ctx, out var req, out var error))
        {
            WriteJson(res, 400, new { error });
            return;
        }
        if (UserStore.ValidateUsername(req!.username) is string nameError)
        {
            WriteJson(res, 400, new { error = nameError });
            return;
        }
        // a key-only account is legitimate (a device, a script) — but an
        // account with neither a password nor, later, a key can never sign in
        if (!string.IsNullOrEmpty(req.password) && UserStore.ValidatePassword(req.password) is string passwordError)
        {
            WriteJson(res, 400, new { error = passwordError });
            return;
        }

        // Only a server admin may make one. Otherwise an ordinary admin
        // could mint an account above their own level and sign into it,
        // which makes the tier decorative.
        if (UserStore.LevelOf(req.role) >= AccessLevel.ServerAdmin && !auth.IsServerAdmin)
        {
            WriteJson(res, 403, new { error = "only a Server Admin can create a Server Admin" });
            return;
        }

        try
        {
            var user = _auth.Users.Create(req.username!, req.password, req.role, req.displayName, req.enabled ?? true);
            WriteJson(res, 200, new { user = DescribeUser(user, null) });
        }
        catch (InvalidOperationException ex)
        {
            WriteJson(res, 409, new { error = ex.Message });
        }
    }

    private void EditUser(HttpListenerContext ctx, AuthResult auth)
    {
        var res = ctx.Response;
        var user = _auth.Users.FindById(ctx.Request.QueryString["id"]);
        if (user is null) { WriteJson(res, 404, new { error = "unknown user" }); return; }
        if (!TryReadJson<UserRequest>(ctx, out var req, out var error))
        {
            WriteJson(res, 400, new { error });
            return;
        }
        if (req!.username is not null && UserStore.ValidateUsername(req.username) is string nameError)
        {
            WriteJson(res, 400, new { error = nameError });
            return;
        }
        // an admin demoting or disabling themselves mid-session is a
        // foot-gun, and the store's last-admin rule wouldn't catch it when
        // another admin exists
        var self = auth.User is not null && auth.User.Id == user.Id;
        if (self && (req.enabled == false
                     || (req.role is not null && UserStore.LevelOf(req.role) < AccessLevel.Admin)))
        {
            WriteJson(res, 400, new { error = "you cannot remove your own administrator rights" });
            return;
        }

        // Granting the top tier, or taking it away, is a server admin's
        // alone — including demoting one, which an ordinary admin doing it
        // would be a lateral attack rather than administration.
        if (!auth.IsServerAdmin
            && (UserStore.LevelOf(req.role) >= AccessLevel.ServerAdmin || user.IsServerAdmin))
        {
            WriteJson(res, 403, new { error = "only a Server Admin can change a Server Admin" });
            return;
        }

        try
        {
            _auth.Users.Update(user, req.username, req.displayName, req.role, req.enabled);
        }
        catch (InvalidOperationException ex)
        {
            WriteJson(res, 409, new { error = ex.Message });
            return;
        }

        if (!string.IsNullOrEmpty(req.password))
        {
            if (UserStore.ValidatePassword(req.password) is string passwordError)
            {
                WriteJson(res, 400, new { error = passwordError });
                return;
            }
            _auth.Users.SetPassword(user, req.password);
            _auth.RevokeSessionsFor(user.Id);
            Log.Info("auth", $"password reset for {user.Username} by an administrator");
        }
        if (req.enabled == false) _auth.RevokeSessionsFor(user.Id);

        WriteJson(res, 200, new { user = DescribeUser(user, auth) });
    }

    private void IssueKey(HttpListenerContext ctx, UserAccount user)
    {
        var res = ctx.Response;
        if (!TryReadJson<KeyRequest>(ctx, out var req, out var error, allowEmpty: true))
        {
            WriteJson(res, 400, new { error });
            return;
        }
        var days = req?.days;
        if (days is < 1 or > 3650)
        {
            WriteJson(res, 400, new { error = "days must be 1–3650, or omitted for a key that never expires" });
            return;
        }
        var (secret, record) = _auth.Users.CreateKey(user, req?.label,
            days is int d ? TimeSpan.FromDays(d) : null);
        WriteJson(res, 200, new
        {
            key = secret,
            record = DescribeKey(record),
            note = "copy this now — only its digest is stored, so it cannot be shown again",
        });
    }

    // ---- helpers ----

    /// <summary>Rejects the request with 401 unless it carries a real account.</summary>
    private UserAccount? RequireUser(HttpListenerContext ctx, AuthResult auth)
    {
        if (auth.User is not null) return auth.User;
        WriteJson(ctx.Response, 401, new
        {
            error = auth.Level == AccessLevel.None
                ? "sign in first"
                : "this action needs a user account, not the legacy control token",
        });
        return null;
    }

    private object? Describe(AuthResult auth) => auth.User is null
        ? (auth.Level == AccessLevel.None ? null : new
        {
            id = "",
            username = auth.Method == "token" ? "control token" : "local",
            displayName = auth.Method == "token" ? "Control token" : "Unclaimed server",
            role = auth.Level >= AccessLevel.ServerAdmin ? UserStore.RoleServerAdmin : UserStore.RoleAdmin,
            enabled = true,
            hasPassword = false,
            keys = Array.Empty<object>(),
        })
        : DescribeUser(auth.User, auth);

    private object DescribeUser(UserAccount u, AuthResult? auth) => new
    {
        id = u.Id,
        username = u.Username,
        displayName = u.DisplayName,
        role = u.Role,
        roleLabel = UserStore.RoleLabel(u.Role),
        enabled = u.Enabled,
        hasPassword = u.HasPassword,
        createdUtc = u.CreatedUtc,
        lastLoginUtc = u.LastLoginUtc,
        sessions = _auth.SessionCountFor(u.Id),
        self = auth?.User is not null && auth.User.Id == u.Id,
        keys = u.Keys.Select(DescribeKey),
    };

    private static object DescribeKey(ApiKeyRecord k) => new
    {
        id = k.Id,
        label = k.Label,
        createdUtc = k.CreatedUtc,
        lastUsedUtc = k.LastUsedUtc,
        expiresUtc = k.ExpiresUtc,
        expired = k.Expired,
    };

    private static readonly JsonSerializerOptions BodyJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads a JSON request body, capping it so a huge POST can't be used to exhaust memory.</summary>
    private static bool TryReadJson<T>(HttpListenerContext ctx, out T? value, out string? error, bool allowEmpty = false)
        where T : class
    {
        value = null;
        error = null;
        try
        {
            var body = ReadBody(ctx);
            if (string.IsNullOrWhiteSpace(body))
            {
                if (allowEmpty) return true;
                error = "empty body";
                return false;
            }
            value = JsonSerializer.Deserialize<T>(body, BodyJson);
            if (value is null && !allowEmpty) { error = "empty body"; return false; }
            return true;
        }
        catch (Exception ex)
        {
            error = "bad JSON: " + ex.Message;
            return false;
        }
    }
}
