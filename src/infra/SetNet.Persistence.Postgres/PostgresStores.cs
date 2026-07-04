using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using SetNet.Persistence;

namespace SetNet.Persistence.Postgres
{
    /// <summary>
    /// A <see cref="IDocumentStore{T}"/> backed by PostgreSQL (Npgsql). Each value is serialized to JSON and stored in a
    /// <b>JSONB</b> column, so your POCO can carry any <b>custom fields</b> with no schema change — and, unlike an opaque
    /// blob, those fields stay <b>queryable and indexable in SQL</b> (e.g. <c>WHERE doc-&gt;&gt;'vipUntil' &gt; now()</c>,
    /// or a GIN index on <c>doc</c>). Upsert uses native <c>INSERT … ON CONFLICT</c>.
    /// </summary>
    /// <typeparam name="T">The document type (serialized to JSON/JSONB).</typeparam>
    public sealed class PostgresDocumentStore<T> : IDocumentStore<T>
    {
        private readonly string _connectionString;
        private readonly string _table;
        private readonly JsonSerializerOptions? _json;

        /// <summary>Creates the store and ensures its table exists.</summary>
        /// <param name="connectionString">A PostgreSQL connection string.</param>
        /// <param name="table">Table name (trusted constant — interpolated into SQL). Default <c>setnet_documents</c>.</param>
        /// <param name="json">Optional JSON options.</param>
        public PostgresDocumentStore(string connectionString, string table = "setnet_documents", JsonSerializerOptions? json = null)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _table = table ?? throw new ArgumentNullException(nameof(table));
            _json = json;
            using var c = new NpgsqlConnection(_connectionString);
            c.Open();
            using var cmd = new NpgsqlCommand($"CREATE TABLE IF NOT EXISTS {_table} (id text PRIMARY KEY, doc jsonb NOT NULL)", c);
            cmd.ExecuteNonQuery();
        }

        /// <inheritdoc/>
        public async Task<T?> GetAsync(string key)
        {
            await using var c = new NpgsqlConnection(_connectionString);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"SELECT doc::text FROM {_table} WHERE id = @id", c);
            cmd.Parameters.AddWithValue("id", key ?? "");
            var result = await cmd.ExecuteScalarAsync();
            return result is string s ? JsonSerializer.Deserialize<T>(s, _json) : default;
        }

        /// <inheritdoc/>
        public async Task SetAsync(string key, T value)
        {
            var doc = JsonSerializer.Serialize(value, _json);
            await using var c = new NpgsqlConnection(_connectionString);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO {_table} (id, doc) VALUES (@id, @doc) ON CONFLICT (id) DO UPDATE SET doc = EXCLUDED.doc", c);
            cmd.Parameters.AddWithValue("id", key ?? "");
            cmd.Parameters.Add(new NpgsqlParameter("doc", NpgsqlDbType.Jsonb) { Value = doc });
            await cmd.ExecuteNonQueryAsync();
        }

        /// <inheritdoc/>
        public async Task<bool> RemoveAsync(string key)
        {
            await using var c = new NpgsqlConnection(_connectionString);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DELETE FROM {_table} WHERE id = @id", c);
            cmd.Parameters.AddWithValue("id", key ?? "");
            return await cmd.ExecuteNonQueryAsync() > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> ExistsAsync(string key)
        {
            await using var c = new NpgsqlConnection(_connectionString);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"SELECT 1 FROM {_table} WHERE id = @id", c);
            cmd.Parameters.AddWithValue("id", key ?? "");
            return await cmd.ExecuteScalarAsync() != null;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<string>> KeysAsync()
        {
            var list = new List<string>();
            await using var c = new NpgsqlConnection(_connectionString);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"SELECT id FROM {_table}", c);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(r.GetString(0));
            return list;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<T>> AllAsync()
        {
            var list = new List<T>();
            await using var c = new NpgsqlConnection(_connectionString);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"SELECT doc::text FROM {_table}", c);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var v = JsonSerializer.Deserialize<T>(r.GetString(0), _json);
                if (v != null) list.Add(v);
            }
            return list;
        }
    }

    /// <summary>A <see cref="ISnapshotStore"/> storing opaque blobs in a PostgreSQL <c>BYTEA</c> column.</summary>
    public sealed class PostgresSnapshotStore : ISnapshotStore
    {
        private readonly string _connectionString;
        private readonly string _table;

        /// <summary>Creates the store and ensures its table exists.</summary>
        public PostgresSnapshotStore(string connectionString, string table = "setnet_snapshots")
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _table = table ?? throw new ArgumentNullException(nameof(table));
            using var c = new NpgsqlConnection(_connectionString);
            c.Open();
            using var cmd = new NpgsqlCommand($"CREATE TABLE IF NOT EXISTS {_table} (id text PRIMARY KEY, data bytea NOT NULL)", c);
            cmd.ExecuteNonQuery();
        }

        /// <inheritdoc/>
        public async Task SaveAsync(string name, byte[] data)
        {
            await using var c = new NpgsqlConnection(_connectionString);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO {_table} (id, data) VALUES (@id, @data) ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data", c);
            cmd.Parameters.AddWithValue("id", name ?? "");
            cmd.Parameters.AddWithValue("data", data ?? Array.Empty<byte>());
            await cmd.ExecuteNonQueryAsync();
        }

        /// <inheritdoc/>
        public async Task<byte[]?> LoadAsync(string name)
        {
            await using var c = new NpgsqlConnection(_connectionString);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"SELECT data FROM {_table} WHERE id = @id", c);
            cmd.Parameters.AddWithValue("id", name ?? "");
            var result = await cmd.ExecuteScalarAsync();
            return result as byte[];
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteAsync(string name)
        {
            await using var c = new NpgsqlConnection(_connectionString);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DELETE FROM {_table} WHERE id = @id", c);
            cmd.Parameters.AddWithValue("id", name ?? "");
            return await cmd.ExecuteNonQueryAsync() > 0;
        }
    }
}
