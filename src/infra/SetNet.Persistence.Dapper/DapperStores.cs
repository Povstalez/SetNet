using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using SetNet.Persistence;

namespace SetNet.Persistence.Dapper
{
    /// <summary>
    /// A <see cref="IDocumentStore{T}"/> that stores each value as JSON in a two-column key/value table via
    /// <c>Dapper</c>, on <b>any</b> ADO.NET database (SQLite, PostgreSQL, MySQL, SQL Server, …). Because the whole object
    /// is serialized, your POCO can carry <b>any custom fields</b> (e.g. a per-character <c>VipUntil</c>) with no schema
    /// change. Upsert is portable (delete + insert inside a transaction), so no dialect-specific <c>ON CONFLICT</c>/<c>MERGE</c>.
    /// </summary>
    /// <typeparam name="T">The document type (serialized to JSON).</typeparam>
    public sealed class DapperDocumentStore<T> : IDocumentStore<T>
    {
        private readonly Func<IDbConnection> _connect;
        private readonly string _table;
        private readonly JsonSerializerOptions? _json;

        /// <summary>
        /// Creates the store and ensures its table exists.
        /// </summary>
        /// <param name="connectionFactory">Opens a fresh connection per operation (e.g. <c>() =&gt; new SqliteConnection(cs)</c>).</param>
        /// <param name="table">Table name (must be a trusted constant — it is interpolated into SQL). Default <c>setnet_documents</c>.</param>
        /// <param name="json">Optional JSON options.</param>
        /// <param name="ensureTableSql">Override the CREATE-TABLE DDL for engines that don't support <c>IF NOT EXISTS</c> (e.g. SQL Server).</param>
        public DapperDocumentStore(Func<IDbConnection> connectionFactory, string table = "setnet_documents",
            JsonSerializerOptions? json = null, string? ensureTableSql = null)
        {
            _connect = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _table = table ?? throw new ArgumentNullException(nameof(table));
            _json = json;
            using var c = _connect();
            c.Execute(ensureTableSql ?? $"CREATE TABLE IF NOT EXISTS {_table} (Id TEXT PRIMARY KEY, Doc TEXT NOT NULL)");
        }

        /// <inheritdoc/>
        public async Task<T?> GetAsync(string key)
        {
            using var c = _connect();
            var doc = await c.QuerySingleOrDefaultAsync<string?>(
                $"SELECT Doc FROM {_table} WHERE Id = @id", new { id = key ?? "" });
            return doc == null ? default : JsonSerializer.Deserialize<T>(doc, _json);
        }

        /// <inheritdoc/>
        public async Task SetAsync(string key, T value)
        {
            var doc = JsonSerializer.Serialize(value, _json);
            using var c = _connect();
            if (c.State != ConnectionState.Open) c.Open();
            using var tx = c.BeginTransaction();
            await c.ExecuteAsync($"DELETE FROM {_table} WHERE Id = @id", new { id = key ?? "" }, tx);
            await c.ExecuteAsync($"INSERT INTO {_table} (Id, Doc) VALUES (@id, @doc)", new { id = key ?? "", doc }, tx);
            tx.Commit();
        }

        /// <inheritdoc/>
        public async Task<bool> RemoveAsync(string key)
        {
            using var c = _connect();
            return await c.ExecuteAsync($"DELETE FROM {_table} WHERE Id = @id", new { id = key ?? "" }) > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> ExistsAsync(string key)
        {
            using var c = _connect();
            return await c.ExecuteScalarAsync<long>($"SELECT COUNT(1) FROM {_table} WHERE Id = @id", new { id = key ?? "" }) > 0;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<string>> KeysAsync()
        {
            using var c = _connect();
            return (await c.QueryAsync<string>($"SELECT Id FROM {_table}")).ToList();
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<T>> AllAsync()
        {
            using var c = _connect();
            var docs = await c.QueryAsync<string>($"SELECT Doc FROM {_table}");
            var list = new List<T>();
            foreach (var d in docs)
            {
                var v = JsonSerializer.Deserialize<T>(d, _json);
                if (v != null) list.Add(v);
            }
            return list;
        }
    }

    /// <summary>A <see cref="ISnapshotStore"/> storing opaque blobs in a key/blob table via Dapper (any ADO.NET database).</summary>
    public sealed class DapperSnapshotStore : ISnapshotStore
    {
        private readonly Func<IDbConnection> _connect;
        private readonly string _table;

        /// <summary>Creates the store and ensures its table exists.</summary>
        /// <param name="connectionFactory">Opens a fresh connection per operation.</param>
        /// <param name="table">Table name (trusted constant). Default <c>setnet_snapshots</c>.</param>
        /// <param name="ensureTableSql">Override the CREATE-TABLE DDL if needed.</param>
        public DapperSnapshotStore(Func<IDbConnection> connectionFactory, string table = "setnet_snapshots", string? ensureTableSql = null)
        {
            _connect = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _table = table ?? throw new ArgumentNullException(nameof(table));
            using var c = _connect();
            c.Execute(ensureTableSql ?? $"CREATE TABLE IF NOT EXISTS {_table} (Id TEXT PRIMARY KEY, Data BLOB NOT NULL)");
        }

        /// <inheritdoc/>
        public async Task SaveAsync(string name, byte[] data)
        {
            using var c = _connect();
            if (c.State != ConnectionState.Open) c.Open();
            using var tx = c.BeginTransaction();
            await c.ExecuteAsync($"DELETE FROM {_table} WHERE Id = @id", new { id = name ?? "" }, tx);
            await c.ExecuteAsync($"INSERT INTO {_table} (Id, Data) VALUES (@id, @data)", new { id = name ?? "", data = data ?? Array.Empty<byte>() }, tx);
            tx.Commit();
        }

        /// <inheritdoc/>
        public async Task<byte[]?> LoadAsync(string name)
        {
            using var c = _connect();
            return await c.QuerySingleOrDefaultAsync<byte[]?>($"SELECT Data FROM {_table} WHERE Id = @id", new { id = name ?? "" });
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteAsync(string name)
        {
            using var c = _connect();
            return await c.ExecuteAsync($"DELETE FROM {_table} WHERE Id = @id", new { id = name ?? "" }) > 0;
        }
    }
}
