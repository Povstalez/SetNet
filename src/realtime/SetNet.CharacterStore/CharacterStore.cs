using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Persistence;

namespace SetNet.CharacterStore
{
    /// <summary>Thrown when a character operation fails (slot limit, duplicate name, missing character…).</summary>
    public sealed class CharacterException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public CharacterException(string message) : base(message) { }
    }

    /// <summary>
    /// The base character. Subclass it to add <b>any custom fields</b> — stats, appearance, a per-character
    /// <c>VipUntil</c> — with no schema change (the whole object is serialized by the backing <see cref="IDocumentStore{T}"/>),
    /// or drop loose values into <see cref="Extra"/>.
    /// </summary>
    public abstract class CharacterBase
    {
        /// <summary>Stable character id (assigned on create).</summary>
        public string Id { get; set; } = "";
        /// <summary>The owning account id (see <c>SetNet.Accounts</c>).</summary>
        public string AccountId { get; set; } = "";
        /// <summary>Display name.</summary>
        public string Name { get; set; } = "";
        /// <summary>Character-select slot index.</summary>
        public int Slot { get; set; }
        /// <summary>Unix seconds when the character was created.</summary>
        public long CreatedUnix { get; set; }
        /// <summary>Whether the character is soft-deleted (hidden, restorable within the window).</summary>
        public bool Deleted { get; set; }
        /// <summary>Unix seconds when the character was soft-deleted (0 if not).</summary>
        public long DeletedAtUnix { get; set; }
        /// <summary>Free-form custom fields (for values you don't want a strong type for).</summary>
        public Dictionary<string, object> Extra { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>Options for <see cref="CharacterServer{TChar}"/>.</summary>
    public sealed class CharacterOptions
    {
        /// <summary>Max characters per account (excludes soft-deleted). Default 7.</summary>
        public int MaxPerAccount { get; set; } = 7;
        /// <summary>How long a soft-deleted character can be restored, in seconds (0 = forever). Default 7 days.</summary>
        public long RestoreWindowSeconds { get; set; } = 7 * 24 * 3600;
        /// <summary>Enforce globally unique names (case-insensitive). Default true.</summary>
        public bool EnforceUniqueName { get; set; } = true;
    }

    /// <summary>
    /// Server-side character service over any <see cref="IDocumentStore{T}"/>. Generic over your character type, so
    /// <b>custom fields need no schema change</b>. Handles per-account slot limits, optional unique names, and soft-delete
    /// with a restore window.
    /// <para><b>Scale note:</b> list/uniqueness scan the store (<c>AllAsync</c>). Fine for modest counts and tests; for a
    /// large world, back it with a DB store and add your own indexed queries, or keep a secondary index.</para>
    /// </summary>
    /// <typeparam name="TChar">Your character type (subclass <see cref="CharacterBase"/>).</typeparam>
    public sealed class CharacterServer<TChar> where TChar : CharacterBase, new()
    {
        private readonly IDocumentStore<TChar> _store;
        private readonly CharacterOptions _options;

        /// <summary>Creates the service over a character store.</summary>
        public CharacterServer(IDocumentStore<TChar> store, CharacterOptions? options = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _options = options ?? new CharacterOptions();
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>
        /// Creates a character for an account. Fills <see cref="CharacterBase.Id"/>/<see cref="CharacterBase.AccountId"/>/
        /// <see cref="CharacterBase.CreatedUnix"/>; validates the slot limit and (optionally) name uniqueness. Set your
        /// custom fields on <paramref name="template"/> before calling.
        /// </summary>
        public async Task<TChar> CreateAsync(string accountId, TChar template)
        {
            if (string.IsNullOrEmpty(accountId)) throw new CharacterException("accountId is required");
            if (template == null) throw new ArgumentNullException(nameof(template));
            if (string.IsNullOrWhiteSpace(template.Name)) throw new CharacterException("name is required");

            var existing = await ListAsync(accountId).ConfigureAwait(false);
            if (existing.Count >= _options.MaxPerAccount)
                throw new CharacterException($"account is at its character limit ({_options.MaxPerAccount})");
            if (_options.EnforceUniqueName && await IsNameTakenAsync(template.Name).ConfigureAwait(false))
                throw new CharacterException($"name '{template.Name}' is already taken");

            template.Id = Guid.NewGuid().ToString("N");
            template.AccountId = accountId;
            template.CreatedUnix = Now();
            template.Deleted = false;
            template.DeletedAtUnix = 0;
            await _store.SetAsync(template.Id, template).ConfigureAwait(false);
            return template;
        }

        /// <summary>Lists an account's characters (excludes soft-deleted unless <paramref name="includeDeleted"/>).</summary>
        public async Task<IReadOnlyList<TChar>> ListAsync(string accountId, bool includeDeleted = false)
        {
            var all = await _store.AllAsync().ConfigureAwait(false);
            return all.Where(c => c.AccountId == accountId && (includeDeleted || !c.Deleted))
                      .OrderBy(c => c.Slot).ToList();
        }

        /// <summary>Gets a character by id.</summary>
        public Task<TChar?> GetAsync(string characterId) => _store.GetAsync(characterId);

        /// <summary>Persists changes you made to a character (stats, position, custom fields…).</summary>
        public Task SaveAsync(TChar character)
            => _store.SetAsync(character?.Id ?? throw new ArgumentNullException(nameof(character)), character);

        /// <summary>True if a (non-deleted) character with this name exists (case-insensitive).</summary>
        public async Task<bool> IsNameTakenAsync(string name)
        {
            var norm = (name ?? "").Trim().ToLowerInvariant();
            var all = await _store.AllAsync().ConfigureAwait(false);
            return all.Any(c => !c.Deleted && c.Name.Trim().ToLowerInvariant() == norm);
        }

        /// <summary>Renames a character (respects the unique-name policy).</summary>
        public async Task RenameAsync(string characterId, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) throw new CharacterException("name is required");
            var c = await _store.GetAsync(characterId).ConfigureAwait(false) ?? throw new CharacterException("character not found");
            if (_options.EnforceUniqueName && await IsNameTakenAsync(newName).ConfigureAwait(false))
                throw new CharacterException($"name '{newName}' is already taken");
            c.Name = newName;
            await _store.SetAsync(characterId, c).ConfigureAwait(false);
        }

        /// <summary>Soft-deletes a character (hidden from lists, restorable within the window).</summary>
        public async Task<bool> SoftDeleteAsync(string characterId)
        {
            var c = await _store.GetAsync(characterId).ConfigureAwait(false);
            if (c == null || c.Deleted) return false;
            c.Deleted = true; c.DeletedAtUnix = Now();
            await _store.SetAsync(characterId, c).ConfigureAwait(false);
            return true;
        }

        /// <summary>Restores a soft-deleted character if still inside the restore window.</summary>
        public async Task<bool> RestoreAsync(string characterId)
        {
            var c = await _store.GetAsync(characterId).ConfigureAwait(false);
            if (c == null || !c.Deleted) return false;
            if (_options.RestoreWindowSeconds > 0 && Now() - c.DeletedAtUnix > _options.RestoreWindowSeconds) return false;
            c.Deleted = false; c.DeletedAtUnix = 0;
            await _store.SetAsync(characterId, c).ConfigureAwait(false);
            return true;
        }

        /// <summary>Permanently removes a character.</summary>
        public Task<bool> PurgeAsync(string characterId) => _store.RemoveAsync(characterId);
    }
}
