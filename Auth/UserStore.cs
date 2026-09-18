using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Auth;

/// <summary>
/// A key the user (or an admin on their behalf) minted for unattended
/// access — a phone, a script, a player. Only a SHA-256 digest of the
/// secret is stored, so a leaked users.json can't be replayed as a login.
/// </summary>
public sealed class ApiKeyRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    /// <summary>Hex SHA-256 of the secret half of the key.</summary>
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("lastUsedUtc")] public DateTime? LastUsedUtc { get; set; }
    [JsonPropertyName("expiresUtc")] public DateTime? ExpiresUtc { get; set; }

    [JsonIgnore] public bool Expired => ExpiresUtc is DateTime e && e <= DateTime.UtcNow;
}

public sealed class UserAccount
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    /// <summary>"admin" (full configuration rights) or "user" (watch only).</summary>
    [JsonPropertyName("role")] public string Role { get; set; } = UserStore.RoleRead;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>
    /// A deliberately open, read-only account: it signs in with its username
    /// alone, no password, and is pinned to the Read role. For letting family
    /// or guests watch without handing out a credential. Set on purpose from
    /// the Users dialog; never a side effect of an empty password.
    /// </summary>
    [JsonPropertyName("passwordless")] public bool Passwordless { get; set; }
    /// <summary>PHC-ish string, or empty for a key-only account.</summary>
    [JsonPropertyName("passwordHash")] public string PasswordHash { get; set; } = "";
    [JsonPropertyName("keys")] public List<ApiKeyRecord> Keys { get; set; } = new();
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("lastLoginUtc")] public DateTime? LastLoginUtc { get; set; }

    /// <summary>
    /// Bumped whenever this account's credentials change in a way that must
    /// invalidate the sessions signed in with the old one — a password change,
    /// or passwordless being turned on or off. In memory only: sessions do not
    /// survive a restart, so nothing has to persist. See AuthService.OpenSession
    /// and LiveUser, which stamp a session with the generation current when it
    /// opened and refuse it once that generation has moved on. This is what
    /// stops a sign-in that read the old hash a moment before the change from
    /// opening a brand-new session a moment after the revocation.
    /// </summary>
    [JsonIgnore] internal int CredentialGeneration { get; set; }

    /// <summary>Administrator or above — a server admin is one too.</summary>
    [JsonIgnore] public bool IsAdmin => UserStore.LevelOf(Role) >= AccessLevel.Admin;
    [JsonIgnore] public bool IsServerAdmin => UserStore.LevelOf(Role) >= AccessLevel.ServerAdmin;
    [JsonIgnore] public bool HasPassword => PasswordHash.Length > 0;
    /// <summary>Signs in on username alone: enabled, and marked passwordless.</summary>
    [JsonIgnore] public bool CanSignInWithoutPassword => Passwordless && Enabled;
}

/// <summary>
/// The account database: users.json next to the rest of the config.
/// Passwords are PBKDF2-HMAC-SHA256 (210k iterations, per-user 16-byte
/// salt); keys are random 256-bit secrets stored only as digests. Nothing
/// here is ever reversible, so the file leaks no credentials — but it is
/// still the crown jewels for authorization and should stay owner-readable.
/// </summary>
public sealed class UserStore
{
    /// <summary>Runs the machine: everything an admin has, plus the log, plus granting this role.</summary>
    public const string RoleServerAdmin = "serveradmin";
    /// <summary>Full access: configuration, the power button, and accounts.</summary>
    public const string RoleAdmin = "admin";
    /// <summary>Adds and removes library content, but can't reach the Config dialog or accounts.</summary>
    public const string RoleEdit = "edit";
    /// <summary>Watches what has been shared. Changes nothing.</summary>
    public const string RoleRead = "read";

    public static readonly string[] Roles = { RoleServerAdmin, RoleAdmin, RoleEdit, RoleRead };

    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Algorithm = "pbkdf2-sha256";

    /// <summary>Verified against when the username doesn't exist, so a wrong
    /// name and a wrong password cost the same wall-clock time.</summary>
    private static readonly string DummyHash = HashPassword("j0kers-timing-equalizer");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class Document
    {
        [JsonPropertyName("users")] public List<UserAccount> Users { get; set; } = new();
    }

    private readonly string _file;
    private readonly object _lock = new();
    private List<UserAccount> _users = new();

    public UserStore(string baseDirectory)
    {
        _file = Path.Combine(baseDirectory, "users.json");
        Load();
        // On startup too, not only when it is next written: a file created by
        // an older build kept whatever the config folder allowed — which on a
        // machine where that folder grants Everyone is every local account.
        Services.SecretFile.Protect(_file);
    }

    public string FilePath => _file;

    /// <summary>
    /// Whether an accounts file was actually read at startup. Guards Save()
    /// from writing a fresh list over one this process never loaded — see
    /// there for why that is the difference between "the accounts are
    /// somewhere else" and "the accounts are gone".
    /// </summary>
    private bool _loadedFromDisk;

    private void Load()
    {
        if (!File.Exists(_file))
        {
            // Said out loud, and with the path in it.
            //
            // This returned in silence, and silence is what made an account
            // list that had gone missing impossible to diagnose: a good start
            // logs "loaded N user account(s)", a bad one logged nothing at
            // all, and the dashboard simply offered to create the first
            // administrator as though the server were new. The usual cause is
            // not a deleted file but a server started against a different
            // directory, so the line that matters is which one it looked in.
            Log.Warn("auth", $"no accounts file at {_file} — this server will ask for a new " +
                             "administrator. If it had accounts, it has been started against " +
                             "the wrong folder; check the config path rather than creating one.");
            return;
        }
        try
        {
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(_file), JsonOpts);
            _users = doc?.Users ?? new List<UserAccount>();
            foreach (var u in _users)
            {
                if (string.IsNullOrEmpty(u.Id)) u.Id = NewId();
                u.Keys ??= new List<ApiKeyRecord>();
            }
            _loadedFromDisk = true;
            Log.Info("auth", $"loaded {_users.Count} user account(s) from {Path.GetFileName(_file)}");
        }
        catch (Exception ex)
        {
            // Refusing to start IS the safe failure here, and the throw is
            // deliberate — the comment that used to sit here said the
            // opposite, and following it would have been dangerous.
            //
            // "With no users loaded, nothing validates" is not how this server
            // behaves. An empty account list is the state a fresh install is
            // in, and Program.cs treats it as "no administrator account — the
            // dashboard and its configuration are open to anyone on this
            // network". So carrying on with a users.json that failed to parse
            // would turn a stray comma into an OPEN server, which is the one
            // outcome worse than not starting.
            //
            // Not starting is loud, is recoverable by fixing the file, and
            // leaves the accounts exactly where they are — Save also refuses
            // to write over a file this process never read.
            //
            // Only the PARSE is inside this try. The Server Admin promotion
            // below used to be as well, and its Save() with it — so a valid
            // accounts file that merely could not be WRITTEN (read-only, or
            // held by a backup) was reported as "users.json is invalid" and
            // the server refused to start, sending the operator to edit or
            // delete a file that was perfectly good. Deleting it would have
            // turned the server into an open, unclaimed one. The promotion is
            // now outside, and its save is quiet: a write failure there costs
            // only the persisting of a promotion that still applies in memory.
            throw new InvalidOperationException($"users.json is invalid: {ex.Message}");
        }

        // A server with no Server Admin has features nobody can reach: the
        // transcode panel and the log window are that tier's, and only that
        // tier can grant it. Installs made before first-run setup created the
        // owner as Server Admin are in exactly that state, so promote the sole
        // enabled administrator — the person who claimed the server — rather
        // than leaving them locked out of their own box. Deliberately outside
        // the parse try/catch above (see there) and saved quietly: the
        // promotion holds in memory whether or not the file can be written.
        if (_loadedFromDisk && _users.Count > 0 && !_users.Any(u => u.Enabled && u.IsServerAdmin))
        {
            var owners = _users.Where(u => u.Enabled && u.IsAdmin).ToList();
            if (owners.Count == 1)
            {
                owners[0].Role = RoleServerAdmin;
                TrySaveQuietly("the Server Admin promotion");
                Log.Info("auth", $"'{owners[0].Username}' promoted to Server Admin — " +
                                 "this server had no account of that tier");
            }
        }
    }

    private void Save()
    {
        // Never write over an accounts file this process did not read.
        //
        // The sequence that costs somebody their server: it starts against a
        // folder with no users.json (a bad upgrade, the wrong config path),
        // loads nothing, and the dashboard offers to create the first
        // administrator — because to the server that is exactly what a new
        // install looks like. Up to that point the real file is untouched and
        // the mistake is recoverable. The moment anyone accepts the offer,
        // this method writes one account over whatever is at that path, and it
        // is not recoverable any more.
        //
        // It also covers the opposite order: an operator who spots the problem
        // and copies users.json back while the server is running. Load runs
        // once, in the constructor, so the in-memory list is still empty and
        // the next save would erase what they just restored.
        //
        // So if a file has appeared where this store never found one, it is
        // somebody else's account list. Keep it.
        if (!_loadedFromDisk && File.Exists(_file))
        {
            var aside = $"{_file}.found-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            try
            {
                File.Copy(_file, aside, overwrite: true);
                Log.Warn("auth", $"an accounts file appeared at {_file} that this server never " +
                                 $"loaded — copied to {Path.GetFileName(aside)} before writing. " +
                                 "If accounts went missing, that copy is them.");
            }
            catch (Exception ex)
            {
                // Refuse the write, do not carry on regardless.
                //
                // The whole point of this branch is to keep an accounts file
                // this process never loaded — someone else's, or one an
                // operator restored while the server ran. If the copy aside
                // fails (the volume is nearly full, the .found- name is taken,
                // a permission), continuing to write ANYWAY replaces that
                // unread file with the in-memory list and leaves no copy at
                // all: the exact loss this guard exists to prevent, now with
                // the safety net removed. So the save fails instead, loudly,
                // and the unread file is left untouched.
                Log.Error("auth", $"could not preserve the accounts file already at {_file}: {ex.Message} — " +
                                  "refusing to overwrite an accounts file this server never loaded");
                throw;
            }
        }

        try
        {
            var json = JsonSerializer.Serialize(new Document { Users = _users }, JsonOpts);
            // Password hashes and key digests: this account's business alone.
            // Written through SecretFile so the restrictive ACL survives — the
            // plain temp-file-and-rename this used to do left a new file at
            // that name every save, inheriting the folder's permissions and
            // quietly undoing the one Protect that had run at startup.
            Services.SecretFile.WriteAllText(_file, json);
            _loadedFromDisk = true;   // this process owns the file from here on
        }
        catch (Exception ex)
        {
            // Said before it is rethrown. A save that fails leaves an account
            // that works until the next restart and then is simply not there,
            // which reads as the account having been deleted by something —
            // and nothing in the log said the write had failed at all.
            Log.Error("auth", $"could not write {_file}: {ex.Message} — " +
                              "changes to accounts will not survive a restart");
            throw;
        }
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    // ---- queries ----

    /// <summary>Snapshot copy — callers enumerate outside the lock.</summary>
    public IReadOnlyList<UserAccount> All { get { lock (_lock) return _users.ToArray(); } }

    /// <summary>
    /// A snapshot of one account's API keys, taken under the lock.
    ///
    /// The account objects themselves escape the lock — <see cref="All"/>,
    /// <see cref="FindById"/> and the auth result all hand out live references
    /// — and <c>Keys</c> is a plain mutable list. Enumerating it directly (the
    /// key-listing endpoints serialize <c>user.Keys.Select(...)</c>, which
    /// defers the actual walk to JSON serialization with no lock held) while
    /// CreateKey/RevokeKey add or remove on another request thread is exactly
    /// "Collection was modified; enumeration operation may not execute". A copy
    /// taken here is safe to enumerate afterwards, whatever those do next.
    /// </summary>
    public IReadOnlyList<ApiKeyRecord> KeysOf(UserAccount user)
    {
        lock (_lock) return user.Keys.ToArray();
    }

    public bool Any { get { lock (_lock) return _users.Count > 0; } }

    public bool HasEnabledAdmin { get { lock (_lock) return _users.Any(u => u.Enabled && u.IsAdmin); } }

    public UserAccount? FindById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_lock) return _users.FirstOrDefault(u => u.Id == id);
    }

    public UserAccount? FindByName(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        lock (_lock)
            return _users.FirstOrDefault(u => u.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    // ---- passwords ----

    /// <summary>
    /// Rules kept deliberately mild — this guards a home media server on a
    /// LAN, and complexity theatre pushes people toward reused passwords.
    /// Length is what actually helps against the offline case.
    /// </summary>
    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) return "password is required";
        if (password.Length < 8) return "password must be at least 8 characters";
        if (password.Length > 256) return "password must be at most 256 characters";
        return null;
    }

    public static string? ValidateUsername(string? username)
    {
        var name = username?.Trim() ?? "";
        if (name.Length == 0) return "username is required";
        if (name.Length > 64) return "username must be at most 64 characters";
        if (!name.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '@'))
            return "username may contain letters, digits, and . _ - @ only";
        return null;
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Algorithm}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Constant-time verification of a password against a stored hash string.</summary>
    private static bool VerifyHash(string stored, string password)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations is < 1 or > 10_000_000) return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[2]); expected = Convert.FromBase64String(parts[3]); }
        catch { return false; }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Checks a username/password pair. Always performs one PBKDF2 pass —
    /// even for an unknown or key-only account — so response time can't be
    /// used to enumerate valid usernames.
    /// </summary>
    public UserAccount? VerifyPassword(string? username, string? password)
    {
        var user = FindByName(username);
        var stored = user is { PasswordHash.Length: > 0 } ? user.PasswordHash : DummyHash;
        var ok = VerifyHash(stored, password ?? "");
        if (user is null || !user.Enabled || !user.HasPassword || !ok) return null;

        // The password was right; the login has happened. Recording WHEN is a
        // nicety, and Save rethrows — so a full disk, a file held open by a
        // backup, or a permission change turned every correct password into a
        // 500 and locked everyone out of a server whose accounts were fine.
        //
        // Only once a minute or so, the same limit VerifyKey already puts on
        // the identical stamp. RtspServer checks the password on EVERY request
        // of a Basic session — DESCRIBE, SETUP, PLAY, and then GET_PARAMETER
        // keep-alives for the life of the stream — so without this each one
        // was a full serialize-and-rewrite of users.json under the store lock
        // that every HTTP and HLS request also authenticates through, plus a
        // warning per write whenever a scanner held the file.
        lock (_lock)
        {
            if (user.LastLoginUtc is null || DateTime.UtcNow - user.LastLoginUtc.Value > TimeSpan.FromMinutes(1))
            {
                user.LastLoginUtc = DateTime.UtcNow;
                TrySaveQuietly("last sign-in time");
            }
        }
        return user;
    }

    /// <summary>
    /// The account's current credential generation — captured by a sign-in
    /// before it starts the ~100 ms password hash, so the session it opens can
    /// be stamped with the generation the credentials had when it began rather
    /// than whatever they are by the time it finishes. See UserAccount.
    /// </summary>
    public int CredentialGenerationOf(UserAccount user)
    {
        lock (_lock) return user.CredentialGeneration;
    }

    public void SetPassword(UserAccount user, string password)
    {
        lock (_lock)
        {
            var oldHash = user.PasswordHash;
            var oldGen = user.CredentialGeneration;
            user.PasswordHash = HashPassword(password);
            user.CredentialGeneration++;
            SaveOrRollback(() => { user.PasswordHash = oldHash; user.CredentialGeneration = oldGen; });
        }
    }

    /// <summary>
    /// Persists a change, and puts it back in memory if the write fails.
    ///
    /// Every mutating method here changed the live objects first and then
    /// called Save, which rethrows on an I/O or permission failure — and
    /// nothing undid the in-memory change. So a revoke, a delete, a password
    /// reset that could not be written answered 500 while already in force:
    /// the request looked like it had failed, but the key really was refused,
    /// the account really was gone — until a restart brought the file's
    /// version back, or some later unrelated save (a key's last-used stamp)
    /// happened to write the un-asked-for state to disk and make it permanent.
    /// Whether a "failed" change stuck was pure timing.
    ///
    /// Now a failed write is a failed change: the caller's <paramref name="undo"/>
    /// restores exactly what was there, and the exception still propagates so
    /// the 500 is honest. Called under <see cref="_lock"/>, like every mutation.
    /// </summary>
    private void SaveOrRollback(Action undo)
    {
        try { Save(); }
        catch { undo(); throw; }
    }

    // ---- accounts ----

    public UserAccount Create(string username, string? password, string? role, string? displayName, bool enabled,
                              bool passwordless = false)
    {
        lock (_lock)
        {
            if (_users.Any(u => u.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"a user named '{username.Trim()}' already exists");

            var user = new UserAccount
            {
                Id = NewId(),
                Username = username.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim(),
                // passwordless is read-only by definition, and carries no password
                Role = passwordless ? RoleRead : NormalizeRole(role),
                Enabled = enabled,
                Passwordless = passwordless,
                PasswordHash = passwordless || string.IsNullOrEmpty(password) ? "" : HashPassword(password),
            };
            _users.Add(user);
            SaveOrRollback(() => _users.Remove(user));
            Log.Info("auth", $"user created: {user.Username} ({user.Role}"
                             + (passwordless ? ", passwordless" : "") + ")");
            return user;
        }
    }

    /// <summary>
    /// Turns the deliberately-open, username-only sign-in on or off.
    ///
    /// Turning it on drops any password and pins the account to Read. Turning
    /// it off closes it, which means more than clearing the flag: while the
    /// account was open, ANYONE who could reach the server could sign into it
    /// and then mint themselves a key (POST /api/auth/keys) or set a password
    /// (ChangeOwnPassword takes no current password when the hash is empty).
    ///
    /// Leaving those in place meant the door stayed open to whoever had taken
    /// one, and the administrator had no way of knowing. So the credentials the
    /// account granted itself go with the flag, and the account is left with
    /// none until an administrator sets one.
    /// </summary>
    public void SetPasswordless(UserAccount user, bool value)
    {
        lock (_lock)
        {
            var oldPasswordless = user.Passwordless;
            var oldHash = user.PasswordHash;
            var oldRole = user.Role;
            var oldKeys = user.Keys;
            var oldGen = user.CredentialGeneration;

            user.Passwordless = value;
            user.CredentialGeneration++;
            var revoked = 0;
            if (value)
            {
                user.PasswordHash = "";
                user.Role = RoleRead;
            }
            else
            {
                // Anything the account handed itself while it was open. A new
                // list, so a rollback restores the old one intact rather than
                // an emptied copy of it.
                revoked = user.Keys.Count;
                user.Keys = new List<ApiKeyRecord>();
                user.PasswordHash = "";
            }
            SaveOrRollback(() =>
            {
                user.Passwordless = oldPasswordless;
                user.PasswordHash = oldHash;
                user.Role = oldRole;
                user.Keys = oldKeys;
                user.CredentialGeneration = oldGen;
            });
            Log.Info("auth", value
                ? $"passwordless enabled for {user.Username}"
                : $"passwordless disabled for {user.Username} — cleared its password and revoked "
                  + $"{revoked} key(s) it had been given while open; set a password to let it back in");
        }
    }

    /// <summary>The enabled, passwordless account by that name, or null.</summary>
    public UserAccount? FindPasswordless(string? username) => FindPasswordless(username, out _);

    /// <param name="generation">
    /// The account's credential generation, read under the same lock as its
    /// passwordless flag - what a passwordless sign-in stamps on its session.
    /// Read any later and a sign-in that found the account open could open its
    /// session after the account was closed, stamped with the new generation,
    /// and outlive the closing (see AuthService.LiveUser).
    /// </param>
    public UserAccount? FindPasswordless(string? username, out int generation)
    {
        lock (_lock)
        {
            var user = FindByName(username);
            generation = user?.CredentialGeneration ?? 0;
            return user is { Enabled: true, Passwordless: true } ? user : null;
        }
    }

    /// <summary>
    /// Records a successful sign-in time (used by the passwordless path).
    ///
    /// Once a minute at most, as VerifyPassword and VerifyKey both do. A
    /// passwordless account signs in on its name alone, so an unauthenticated
    /// loop of sign-ins to a 'guest' account was a loop of full users.json
    /// rewrites under the store lock — every one of them stamping a time
    /// nobody reads more than once a minute anyway.
    /// </summary>
    public void TouchLogin(UserAccount user)
    {
        lock (_lock)
        {
            if (user.LastLoginUtc is null || DateTime.UtcNow - user.LastLoginUtc.Value > TimeSpan.FromMinutes(1))
            {
                user.LastLoginUtc = DateTime.UtcNow;
                TrySaveQuietly("last sign-in time");
            }
        }
    }

    /// <summary>
    /// Saves, and treats failure as a lost nicety rather than a failed
    /// operation.
    ///
    /// For the things that are recorded ABOUT a successful action rather than
    /// being the action — a last-sign-in stamp, a key's last-used time. Save
    /// itself rethrows, which is right when the caller is creating an account
    /// or changing a password: silently not storing those is worse than
    /// failing. It is wrong for a timestamp, where the only effect of throwing
    /// is to undo something that already happened.
    /// </summary>
    private void TrySaveQuietly(string what)
    {
        try { Save(); }
        catch (Exception ex)
        {
            Log.Warn("auth", $"could not record the {what}: {ex.Message} — "
                             + "the sign-in itself is unaffected");
        }
    }

    /// <summary>
    /// Anything unrecognised becomes read — the least dangerous reading of a
    /// typo. "user" is the pre-three-tier name for read and is still
    /// accepted, so an existing users.json keeps working.
    /// </summary>
    public static string NormalizeRole(string? role) => (role?.Trim().ToLowerInvariant()) switch
    {
        // the spellings a person or an older config might reasonably use
        RoleServerAdmin or "server-admin" or "server admin" or "serveradministrator" => RoleServerAdmin,
        RoleAdmin => RoleAdmin,
        RoleEdit => RoleEdit,
        _ => RoleRead,
    };

    public static AccessLevel LevelOf(string? role) => NormalizeRole(role) switch
    {
        RoleServerAdmin => AccessLevel.ServerAdmin,
        RoleAdmin => AccessLevel.Admin,
        RoleEdit => AccessLevel.Edit,
        _ => AccessLevel.Read,
    };

    /// <summary>The human name for a role, for the dashboard and for logs.</summary>
    public static string RoleLabel(string? role) => NormalizeRole(role) switch
    {
        RoleServerAdmin => "Server Admin",
        RoleAdmin => "Admin",
        RoleEdit => "Edit",
        _ => "Read",
    };

    /// <summary>
    /// Applies the supplied fields. Refuses any edit that would leave the
    /// server with no enabled administrator — that is an unrecoverable
    /// lockout, only fixable by hand-editing users.json.
    /// </summary>
    /// <param name="finalPasswordless">
    /// What the account's passwordless flag will be once this whole edit is
    /// applied, when the caller is turning it off (or on) in the same request.
    /// Null means "unchanged". A passwordless account is pinned to Read, and it
    /// used to be pinned by reading <c>user.Passwordless</c> here — which is
    /// still true at this point, because the caller applies the flag change
    /// AFTER Update. So unticking passwordless and choosing 'edit' in one save
    /// set the role while the account still read as passwordless, the role was
    /// forced back to Read, and the account ended up Read with a password: a
    /// second save was needed to get the role that had just been chosen. Taking
    /// the FINAL state pins the role only when the account will actually stay
    /// open.
    /// </param>
    public void Update(UserAccount user, string? username, string? displayName, string? role, bool? enabled,
                       bool? finalPasswordless = null)
    {
        lock (_lock)
        {
            // a passwordless account is read-only, whatever role was asked for
            var willBePasswordless = finalPasswordless ?? user.Passwordless;
            var newRole = willBePasswordless ? RoleRead
                        : role is null ? user.Role : NormalizeRole(role);
            var newEnabled = enabled ?? user.Enabled;
            var stillAdmin = newEnabled && LevelOf(newRole) >= AccessLevel.Admin;
            if (!stillAdmin && user.IsAdmin && user.Enabled && !_users.Any(u =>
                    u.Id != user.Id && u.Enabled && u.IsAdmin))
                throw new InvalidOperationException("this is the last enabled administrator");
            // The same rule one tier up. Counting plain admins let the only
            // Server Admin step down to Admin whenever another admin existed -
            // and then nobody could see the log or the transcoder, or make a
            // Server Admin again, because granting that tier takes one.
            var stillServerAdmin = newEnabled && LevelOf(newRole) >= AccessLevel.ServerAdmin;
            if (!stillServerAdmin && user.IsServerAdmin && user.Enabled && !_users.Any(u =>
                    u.Id != user.Id && u.Enabled && u.IsServerAdmin))
                throw new InvalidOperationException("this is the last enabled Server Admin — make someone else one first");

            var oldUsername = user.Username;
            var oldDisplay = user.DisplayName;
            var oldRole = user.Role;
            var oldEnabled = user.Enabled;

            if (username is not null)
            {
                var name = username.Trim();
                if (_users.Any(u => u.Id != user.Id && u.Username.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"a user named '{name}' already exists");
                user.Username = name;
            }
            if (displayName is not null) user.DisplayName = displayName.Trim();
            user.Role = newRole;
            user.Enabled = newEnabled;
            SaveOrRollback(() =>
            {
                user.Username = oldUsername;
                user.DisplayName = oldDisplay;
                user.Role = oldRole;
                user.Enabled = oldEnabled;
            });
        }
    }

    public void Delete(UserAccount user)
    {
        lock (_lock)
        {
            if (user.Enabled && user.IsAdmin && !_users.Any(u => u.Id != user.Id && u.Enabled && u.IsAdmin))
                throw new InvalidOperationException("this is the last enabled administrator");
            if (user.Enabled && user.IsServerAdmin && !_users.Any(u => u.Id != user.Id && u.Enabled && u.IsServerAdmin))
                throw new InvalidOperationException("this is the last enabled Server Admin — make someone else one first");
            var index = _users.FindIndex(u => u.Id == user.Id);
            if (index < 0) return;
            var removed = _users[index];
            _users.RemoveAt(index);
            // Back into its own place if the write fails, not appended: order is
            // what the Users list shows, and a delete that could not be written
            // must leave nothing changed at all.
            SaveOrRollback(() => _users.Insert(Math.Min(index, _users.Count), removed));
            Log.Info("auth", $"user removed: {user.Username}");
        }
    }

    // ---- keys ----
    //
    // Wire format: jmk_<keyId>_<secret>. The id half is a plaintext lookup
    // handle; the secret half is 256 bits of CSPRNG output, so a plain
    // SHA-256 digest is enough to store it (there is nothing to guess and
    // nothing to dictionary-attack — unlike a human-chosen password).

    public const string KeyPrefix = "jmk_";

    public (string secret, ApiKeyRecord record) CreateKey(UserAccount user, string? label, TimeSpan? lifetime = null)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var full = $"{KeyPrefix}{id}_{secret}";
        var record = new ApiKeyRecord
        {
            Id = id,
            Label = string.IsNullOrWhiteSpace(label) ? "key" : label.Trim(),
            Hash = Sha256Hex(secret),
            ExpiresUtc = lifetime is TimeSpan t ? DateTime.UtcNow + t : null,
        };
        lock (_lock)
        {
            user.Keys.Add(record);
            SaveOrRollback(() => user.Keys.Remove(record));
        }
        Log.Info("auth", $"key issued for {user.Username}: {record.Label} ({id})");
        return (full, record);
    }

    /// <summary>Whether this account still has that key, unexpired.</summary>
    public bool KeyAlive(UserAccount user, string keyId)
    {
        lock (_lock) return user.Keys.Any(k => k.Id == keyId && !k.Expired);
    }

    /// <summary>The id half of a presented key (jmk_&lt;id&gt;_&lt;secret&gt;), or null if it is not shaped like one.</summary>
    public static string? KeyIdOf(string? presented)
    {
        if (presented is null || !presented.StartsWith(KeyPrefix, StringComparison.Ordinal)) return null;
        var rest = presented[KeyPrefix.Length..];
        var split = rest.IndexOf('_');
        return split > 0 ? rest[..split] : null;
    }

    public bool RevokeKey(UserAccount user, string keyId)
    {
        lock (_lock)
        {
            var gone = user.Keys.Where(k => k.Id == keyId).ToList();
            if (gone.Count == 0) return false;
            user.Keys.RemoveAll(k => k.Id == keyId);
            // The dashboard's Keys list promises a revoke takes effect at once,
            // and a 500 that silently left the key working — to reappear at the
            // next restart — is exactly the trust that breaks. So the key goes
            // back if the write fails, and the caller sees the failure.
            SaveOrRollback(() => user.Keys.AddRange(gone));
            Log.Info("auth", $"key revoked for {user.Username}: {keyId}");
            return true;
        }
    }

    /// <summary>
    /// Resolves a presented key to its owner, or null. Expired keys, keys on
    /// disabled accounts, and malformed strings all fail the same way.
    /// </summary>
    public UserAccount? VerifyKey(string? presented)
    {
        if (string.IsNullOrWhiteSpace(presented) || !presented.StartsWith(KeyPrefix, StringComparison.Ordinal))
            return null;
        var rest = presented[KeyPrefix.Length..];
        var split = rest.IndexOf('_');
        if (split <= 0 || split == rest.Length - 1) return null;
        var id = rest[..split];
        var secret = rest[(split + 1)..];
        var digest = Sha256Hex(secret);

        lock (_lock)
        {
            foreach (var user in _users)
            {
                foreach (var key in user.Keys)
                {
                    if (key.Id != id) continue;
                    if (!FixedTimeEqualsHex(key.Hash, digest)) return null;
                    if (key.Expired || !user.Enabled) return null;
                    // touching the timestamp on every request would rewrite
                    // users.json constantly; a minute's granularity is plenty
                    if (key.LastUsedUtc is null || DateTime.UtcNow - key.LastUsedUtc.Value > TimeSpan.FromMinutes(1))
                    {
                        key.LastUsedUtc = DateTime.UtcNow;
                        try { Save(); } catch { /* a stat update is not worth failing the request */ }
                    }
                    return user;
                }
            }
        }
        return null;
    }

    private static bool FixedTimeEqualsHex(string a, string b)
    {
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
