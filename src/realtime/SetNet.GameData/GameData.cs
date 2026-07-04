using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SetNet.GameData
{
    /// <summary>
    /// A fast, immutable, id-keyed lookup over a set of data rows (items, skills, npcs, spawns…). Rows are your own POCOs,
    /// so each table can carry <b>any custom columns</b>. Build it via <see cref="GameDataRegistry"/>.
    /// </summary>
    /// <typeparam name="TId">The row id type.</typeparam>
    /// <typeparam name="TRow">The row type (your POCO).</typeparam>
    public sealed class DataTable<TId, TRow> where TId : notnull
    {
        private readonly Dictionary<TId, TRow> _rows;

        /// <summary>Builds the table from rows, keyed by <paramref name="keySelector"/> (last row wins on a duplicate id).</summary>
        public DataTable(IEnumerable<TRow> rows, Func<TRow, TId> keySelector)
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));
            _rows = new Dictionary<TId, TRow>();
            foreach (var r in rows) _rows[keySelector(r)] = r;
        }

        /// <summary>The row with this id, or <c>default</c> if absent.</summary>
        public TRow? Get(TId id) => _rows.TryGetValue(id, out var r) ? r : default;
        /// <summary>Tries to get the row with this id.</summary>
        public bool TryGet(TId id, out TRow row) => _rows.TryGetValue(id, out row!);
        /// <summary>True if a row with this id exists.</summary>
        public bool Contains(TId id) => _rows.ContainsKey(id);
        /// <summary>All rows.</summary>
        public IReadOnlyCollection<TRow> All => _rows.Values;
        /// <summary>All ids.</summary>
        public IReadOnlyCollection<TId> Ids => _rows.Keys;
        /// <summary>Row count.</summary>
        public int Count => _rows.Count;
    }

    /// <summary>
    /// A registry of named <see cref="DataTable{TId,TRow}"/>s loaded from JSON (a file or a string). File-backed tables
    /// are <b>hot-reloadable</b> via <see cref="Reload"/> (re-reads every file registered so far and swaps the tables
    /// atomically). Load once at startup; read anywhere. Thread-safe.
    /// </summary>
    public sealed class GameDataRegistry
    {
        private readonly ConcurrentDictionary<string, object> _tables = new ConcurrentDictionary<string, object>();
        private readonly ConcurrentDictionary<string, Func<object>> _reloaders = new ConcurrentDictionary<string, Func<object>>();
        private readonly JsonSerializerOptions _json;

        /// <summary>Creates a registry. The default JSON options are lenient (case-insensitive, comments and trailing commas allowed).</summary>
        public GameDataRegistry(JsonSerializerOptions? json = null)
        {
            _json = json ?? new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
        }

        /// <summary>Raised after <see cref="Reload"/> swaps in fresh tables.</summary>
        public event Action? Reloaded;

        /// <summary>
        /// Loads a table from <b>any source</b> — a database query, an <c>IDocumentStore.AllAsync()</c>, an API, a list —
        /// and registers it under <paramref name="name"/> (one-shot; not remembered for <see cref="Reload"/>).
        /// </summary>
        public DataTable<TId, TRow> Load<TId, TRow>(string name, IEnumerable<TRow> rows, Func<TRow, TId> keySelector) where TId : notnull
        {
            var table = new DataTable<TId, TRow>(rows, keySelector);
            _tables[name] = table;
            return table;
        }

        /// <summary>
        /// Loads a table from a reloadable <paramref name="source"/> (e.g. a DB query) and registers it under
        /// <paramref name="name"/>; the source is remembered so <see cref="Reload"/> re-runs it (re-queries the DB).
        /// </summary>
        /// <example>
        /// <code>
        /// // from PostgreSQL via SetNet.Persistence.Postgres:
        /// data.LoadFrom&lt;int, ItemDef&gt;("items",
        ///     () =&gt; itemStore.AllAsync().GetAwaiter().GetResult(), r =&gt; r.Id);
        /// // or straight from Dapper:
        /// data.LoadFrom&lt;int, ItemDef&gt;("items",
        ///     () =&gt; conn.Query&lt;ItemDef&gt;("SELECT * FROM items"), r =&gt; r.Id);
        /// data.Reload();   // re-queries the DB, swaps the table in atomically
        /// </code>
        /// </example>
        public DataTable<TId, TRow> LoadFrom<TId, TRow>(string name, Func<IEnumerable<TRow>> source, Func<TRow, TId> keySelector) where TId : notnull
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            object Build() => Load(name, source(), keySelector);
            _reloaders[name] = Build;
            return (DataTable<TId, TRow>)Build();
        }

        /// <summary>Loads a table from a JSON array string and registers it under <paramref name="name"/> (one-shot).</summary>
        public DataTable<TId, TRow> LoadJson<TId, TRow>(string name, string json, Func<TRow, TId> keySelector) where TId : notnull
            => Load(name, JsonSerializer.Deserialize<List<TRow>>(json, _json) ?? new List<TRow>(), keySelector);

        /// <summary>
        /// Loads a table from a JSON file and registers it under <paramref name="name"/>. The file is remembered so a later
        /// <see cref="Reload"/> re-reads it.
        /// </summary>
        public DataTable<TId, TRow> LoadFile<TId, TRow>(string name, string path, Func<TRow, TId> keySelector) where TId : notnull
            => LoadFrom(name, () => JsonSerializer.Deserialize<List<TRow>>(File.ReadAllText(path), _json) ?? new List<TRow>(), keySelector);

        /// <summary>Gets a previously loaded table (throws if the name/type doesn't match).</summary>
        public DataTable<TId, TRow> Get<TId, TRow>(string name) where TId : notnull
        {
            if (!_tables.TryGetValue(name, out var t)) throw new KeyNotFoundException($"No game-data table '{name}' is loaded.");
            return (DataTable<TId, TRow>)t;
        }

        /// <summary>True if a table with this name is loaded.</summary>
        public bool Has(string name) => _tables.ContainsKey(name);

        /// <summary>The names of all loaded tables.</summary>
        public IReadOnlyCollection<string> Names => _tables.Keys.ToArray();

        /// <summary>Re-runs every reloadable source (files and <see cref="LoadFrom"/> DB sources), swaps the tables in, then raises <see cref="Reloaded"/>.</summary>
        public void Reload()
        {
            foreach (var kv in _reloaders) _tables[kv.Key] = kv.Value();
            Reloaded?.Invoke();
        }
    }
}
