using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SetNet.Persistence;

namespace SetNet.Accounts
{
    /// <summary>Thrown when an account operation fails (e.g. a duplicate username).</summary>
    public sealed class AccountException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public AccountException(string message) : base(message) { }
    }

    /// <summary>
    /// The base account. Subclass it to add <b>any custom fields</b> (a donor tier, a referral code, a 2FA secret…) with
    /// no schema change — the whole object is serialized by the backing <see cref="IDocumentStore{T}"/> — or drop loose
    /// values into <see cref="Extra"/> when you don't want a strong type.
    /// </summary>
    public abstract class AccountBase
    {
        /// <summary>Stable account id (assigned on register; referenced by characters as their account key).</summary>
        public string Id { get; set; } = "";
        /// <summary>Login name (as entered; the lookup index is case-insensitive).</summary>
        public string Username { get; set; } = "";
        /// <summary>Password hash (opaque; produced by the <see cref="IPasswordHasher"/>).</summary>
        public string PasswordHash { get; set; } = "";
        /// <summary>Password salt (opaque).</summary>
        public string PasswordSalt { get; set; } = "";
        /// <summary>Whether the account is banned (blocks authentication).</summary>
        public bool Banned { get; set; }
        /// <summary>Optional ban reason.</summary>
        public string? BanReason { get; set; }
        /// <summary>Unix seconds when the account was created.</summary>
        public long CreatedUnix { get; set; }
        /// <summary>Unix seconds of the last successful authentication.</summary>
        public long LastLoginUnix { get; set; }
        /// <summary>Free-form custom fields (for values you don't want a strong type for).</summary>
        public System.Collections.Generic.Dictionary<string, object> Extra { get; set; } = new System.Collections.Generic.Dictionary<string, object>();
    }

    /// <summary>Hashes and verifies passwords. Swap for argon2/bcrypt by implementing this.</summary>
    public interface IPasswordHasher
    {
        /// <summary>Produces a (hash, salt) pair for a new/changed password.</summary>
        (string hash, string salt) Hash(string password);
        /// <summary>Verifies a password against a stored hash+salt (constant-time).</summary>
        bool Verify(string password, string hash, string salt);
    }

    /// <summary>Default PBKDF2 (SHA-256, 100k iterations) password hasher.</summary>
    public sealed class Pbkdf2PasswordHasher : IPasswordHasher
    {
        private const int Iterations = 100_000, SaltSize = 16, HashSize = 32;

        /// <inheritdoc/>
        public (string hash, string salt) Hash(string password)
        {
            var salt = new byte[SaltSize];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            return (Convert.ToBase64String(Derive(password ?? "", salt)), Convert.ToBase64String(salt));
        }

        /// <inheritdoc/>
        public bool Verify(string password, string hash, string salt)
        {
            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt)) return false;
            byte[] saltBytes, expected;
            try { saltBytes = Convert.FromBase64String(salt); expected = Convert.FromBase64String(hash); }
            catch (FormatException) { return false; }
            var actual = Derive(password ?? "", saltBytes);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        private static byte[] Derive(string password, byte[] salt)
        {
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
            return pbkdf2.GetBytes(HashSize);
        }
    }

    /// <summary>The outcome of an authentication attempt.</summary>
    public enum AccountAuthStatus
    {
        /// <summary>Credentials valid.</summary>
        Ok,
        /// <summary>No such username.</summary>
        UnknownUser,
        /// <summary>Wrong password.</summary>
        WrongPassword,
        /// <summary>Account is banned.</summary>
        Banned
    }

    /// <summary>The result of <see cref="AccountServer{TAccount}.AuthenticateAsync"/>.</summary>
    /// <typeparam name="TAccount">The account type.</typeparam>
    public sealed class AccountAuthResult<TAccount> where TAccount : AccountBase
    {
        /// <summary>The status.</summary>
        public AccountAuthStatus Status { get; }
        /// <summary>The account (only when <see cref="Status"/> is <see cref="AccountAuthStatus.Ok"/>).</summary>
        public TAccount? Account { get; }
        /// <summary>True when authentication succeeded.</summary>
        public bool Ok => Status == AccountAuthStatus.Ok;

        /// <summary>Creates a result.</summary>
        public AccountAuthResult(AccountAuthStatus status, TAccount? account = null) { Status = status; Account = account; }
    }

    /// <summary>Options for <see cref="AccountServer{TAccount}"/>.</summary>
    public sealed class AccountOptions
    {
        /// <summary>Password hasher (default PBKDF2).</summary>
        public IPasswordHasher Hasher { get; set; } = new Pbkdf2PasswordHasher();
    }

    /// <summary>
    /// Server-side account service over any <see cref="IDocumentStore{T}"/>. Accounts are keyed by a stable id; a
    /// case-insensitive username → id index makes login lookups direct. Generic over your account type, so <b>custom
    /// fields need no schema change</b>.
    /// </summary>
    /// <typeparam name="TAccount">Your account type (subclass <see cref="AccountBase"/>).</typeparam>
    public sealed class AccountServer<TAccount> where TAccount : AccountBase, new()
    {
        private readonly IDocumentStore<TAccount> _accounts;
        private readonly IDocumentStore<string> _usernameIndex;
        private readonly IPasswordHasher _hasher;

        /// <summary>Creates the service.</summary>
        /// <param name="accounts">Store keyed by account id.</param>
        /// <param name="usernameIndex">Store mapping lower-cased username → account id (use the SAME backing as <paramref name="accounts"/> for durability).</param>
        /// <param name="options">Optional options (hasher).</param>
        public AccountServer(IDocumentStore<TAccount> accounts, IDocumentStore<string> usernameIndex, AccountOptions? options = null)
        {
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            _usernameIndex = usernameIndex ?? throw new ArgumentNullException(nameof(usernameIndex));
            _hasher = (options ?? new AccountOptions()).Hasher;
        }

        private static string Norm(string username) => (username ?? "").Trim().ToLowerInvariant();
        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>Registers a new account. Throws <see cref="AccountException"/> if the username is taken.</summary>
        /// <param name="username">Login name.</param>
        /// <param name="password">Plaintext password (hashed before storage).</param>
        /// <param name="init">Optional initializer to set your custom fields on the new account.</param>
        public async Task<TAccount> RegisterAsync(string username, string password, Action<TAccount>? init = null)
        {
            if (string.IsNullOrWhiteSpace(username)) throw new AccountException("username is required");
            var norm = Norm(username);
            if (await _usernameIndex.ExistsAsync(norm).ConfigureAwait(false)) throw new AccountException("username already taken");

            var (hash, salt) = _hasher.Hash(password ?? "");
            var acc = new TAccount
            {
                Id = Guid.NewGuid().ToString("N"),
                Username = username,
                PasswordHash = hash,
                PasswordSalt = salt,
                CreatedUnix = Now(),
            };
            init?.Invoke(acc);

            await _accounts.SetAsync(acc.Id, acc).ConfigureAwait(false);
            await _usernameIndex.SetAsync(norm, acc.Id).ConfigureAwait(false);
            return acc;
        }

        /// <summary>Verifies credentials; updates <see cref="AccountBase.LastLoginUnix"/> on success.</summary>
        public async Task<AccountAuthResult<TAccount>> AuthenticateAsync(string username, string password)
        {
            var id = await _usernameIndex.GetAsync(Norm(username)).ConfigureAwait(false);
            if (id == null) return new AccountAuthResult<TAccount>(AccountAuthStatus.UnknownUser);
            var acc = await _accounts.GetAsync(id).ConfigureAwait(false);
            if (acc == null) return new AccountAuthResult<TAccount>(AccountAuthStatus.UnknownUser);
            if (acc.Banned) return new AccountAuthResult<TAccount>(AccountAuthStatus.Banned, acc);
            if (!_hasher.Verify(password ?? "", acc.PasswordHash, acc.PasswordSalt))
                return new AccountAuthResult<TAccount>(AccountAuthStatus.WrongPassword);

            acc.LastLoginUnix = Now();
            await _accounts.SetAsync(acc.Id, acc).ConfigureAwait(false);
            return new AccountAuthResult<TAccount>(AccountAuthStatus.Ok, acc);
        }

        /// <summary>Gets an account by id.</summary>
        public Task<TAccount?> GetAsync(string accountId) => _accounts.GetAsync(accountId);

        /// <summary>Finds an account by (case-insensitive) username.</summary>
        public async Task<TAccount?> FindByUsernameAsync(string username)
        {
            var id = await _usernameIndex.GetAsync(Norm(username)).ConfigureAwait(false);
            return id == null ? default : await _accounts.GetAsync(id).ConfigureAwait(false);
        }

        /// <summary>True if the username exists.</summary>
        public Task<bool> ExistsAsync(string username) => _usernameIndex.ExistsAsync(Norm(username));

        /// <summary>Persists changes you made to an account object (e.g. custom fields).</summary>
        public Task SaveAsync(TAccount account) => _accounts.SetAsync(account?.Id ?? throw new ArgumentNullException(nameof(account)), account);

        /// <summary>Bans an account.</summary>
        public async Task<bool> BanAsync(string accountId, string? reason = null)
        {
            var acc = await _accounts.GetAsync(accountId).ConfigureAwait(false);
            if (acc == null) return false;
            acc.Banned = true; acc.BanReason = reason;
            await _accounts.SetAsync(accountId, acc).ConfigureAwait(false);
            return true;
        }

        /// <summary>Lifts a ban.</summary>
        public async Task<bool> UnbanAsync(string accountId)
        {
            var acc = await _accounts.GetAsync(accountId).ConfigureAwait(false);
            if (acc == null) return false;
            acc.Banned = false; acc.BanReason = null;
            await _accounts.SetAsync(accountId, acc).ConfigureAwait(false);
            return true;
        }

        /// <summary>Sets a new password.</summary>
        public async Task<bool> SetPasswordAsync(string accountId, string newPassword)
        {
            var acc = await _accounts.GetAsync(accountId).ConfigureAwait(false);
            if (acc == null) return false;
            var (hash, salt) = _hasher.Hash(newPassword ?? "");
            acc.PasswordHash = hash; acc.PasswordSalt = salt;
            await _accounts.SetAsync(accountId, acc).ConfigureAwait(false);
            return true;
        }
    }
}
