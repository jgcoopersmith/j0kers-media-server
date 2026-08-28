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

    private void Load()
    {
        if (!File.Exists(_file)) return;
        try
        {
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(_file), JsonOpts);
            _users = doc?.Users ?? new List<UserAccount>();
            foreach (var u in _users)
            {
                if (string.IsNullOrEmpty(u.Id)) u.Id = NewId();
                u.Keys ??= new List<ApiKeyRecord>();
            }
            Log.Info("auth", $"loaded {_users.Count} user account(s) from {Path.GetFileName(_file)}");

            // A server with no Server Admin has features nobody can reach: the
            // transcode panel and the log window are that tier's, and only that
            // tier can grant it. Installs made before first-run setup created
            // the owner as Server Admin are in exactly that state, so promote
            // the sole enabled administrator — the person who claimed the
            // server — rather than leaving them locked out of their own box.
            if (_users.Count > 0 && !_users.Any(u => u.Enabled && u.IsServerAdmin))
            {
                var owners = _users.Where(u => u.Enabled && u.IsAdmin).ToList();
                if (owners.Count == 1)
                {
                    owners[0].Role = RoleServerAdmin;
                    Save();
                    Log.Info("auth", $"'{owners[0].Username}' promoted to Server Admin — " +
                                     "this server had no account of that tier");
                }
            }
        }
        catch (Exception ex)
        {
            // Refusing to start would lock the operator out of their own
            // server over a stray comma; refusing to *authenticate* is the
            // safe failure — with no users loaded, nothing validates.
            throw new InvalidOperationException($"users.json is invalid: {ex.Message}");
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(new Document { Users = _users }, JsonOpts);
        var tmp = $"{_file}.{Environment.CurrentManagedThreadId}.tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _file, overwrite: true);
        // password hashes and key digests: this account's business alone
        Services.SecretFile.Protect(_file);
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

        lock (_lock) { user.LastLoginUtc = DateTime.UtcNow; Save(); }
        return user;
    }

    public void SetPassword(UserAccount user, string password)
    {
        lock (_lock)
        {
            user.PasswordHash = HashPassword(password);
            Save();
        }
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
            Save();
            Log.Info("auth", $"user created: {user.Username} ({user.Role}"
                             + (passwordless ? ", passwordless" : "") + ")");
            return user;
        }
    }

    /// <summary>
    /// Turns the deliberately-open, username-only sign-in on or off. Turning it
    /// on drops any password and pins the account to Read; turning it off leaves
    /// it key-only until an administrator sets a password.
    /// </summary>
    public void SetPasswordless(UserAccount user, bool value)
    {
        lock (_lock)
        {
            user.Passwordless = value;
            if (value)
            {
                user.PasswordHash = "";
                user.Role = RoleRead;
            }
            Save();
            Log.Info("auth", $"passwordless {(value ? "enabled" : "disabled")} for {user.Username}");
        }
    }

    /// <summary>The enabled, passwordless account by that name, or null.</summary>
    public UserAccount? FindPasswordless(string? username)
    {
        var user = FindByName(username);
        return user is { Enabled: true, Passwordless: true } ? user : null;
    }

    /// <summary>Records a successful sign-in time (used by the passwordless path).</summary>
    public void TouchLogin(UserAccount user)
    {
        lock (_lock) { user.LastLoginUtc = DateTime.UtcNow; Save(); }
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
    public void Update(UserAccount user, string? username, string? displayName, string? role, bool? enabled)
    {
        lock (_lock)
        {
            // a passwordless account is read-only, whatever role was asked for
            var newRole = user.Passwordless ? RoleRead
                        : role is null ? user.Role : NormalizeRole(role);
            var newEnabled = enabled ?? user.Enabled;
            var stillAdmin = newEnabled && LevelOf(newRole) >= AccessLevel.Admin;
            if (!stillAdmin && user.IsAdmin && user.Enabled && !_users.Any(u =>
                    u.Id != user.Id && u.Enabled && u.IsAdmin))
                throw new InvalidOperationException("this is the last enabled administrator");

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
            Save();
        }
    }

    public void Delete(UserAccount user)
    {
        lock (_lock)
        {
            if (user.Enabled && user.IsAdmin && !_users.Any(u => u.Id != user.Id && u.Enabled && u.IsAdmin))
                throw new InvalidOperationException("this is the last enabled administrator");
            _users.RemoveAll(u => u.Id == user.Id);
            Save();
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
            Save();
        }
        Log.Info("auth", $"key issued for {user.Username}: {record.Label} ({id})");
        return (full, record);
    }

    public bool RevokeKey(UserAccount user, string keyId)
    {
        lock (_lock)
        {
            var removed = user.Keys.RemoveAll(k => k.Id == keyId) > 0;
            if (removed) { Save(); Log.Info("auth", $"key revoked for {user.Username}: {keyId}"); }
            return removed;
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
