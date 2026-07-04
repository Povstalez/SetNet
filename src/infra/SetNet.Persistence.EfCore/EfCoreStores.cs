using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SetNet.Persistence;

namespace SetNet.Persistence.EfCore
{
    /// <summary>A JSON key/value row: one document, tagged with its logical collection (usually the CLR type name).</summary>
    public sealed class DocumentRecord
    {
        /// <summary>Logical collection (per <c>IDocumentStore&lt;T&gt;</c>) — usually the type's full name.</summary>
        public string Collection { get; set; } = "";
        /// <summary>The document key.</summary>
        public string Key { get; set; } = "";
        /// <summary>The value, serialized as JSON.</summary>
        public string Json { get; set; } = "";
    }

    /// <summary>An opaque snapshot blob.</summary>
    public sealed class SnapshotRecord
    {
        /// <summary>Snapshot name (primary key).</summary>
        public string Name { get; set; } = "";
        /// <summary>The blob.</summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// The <see cref="DbContext"/> used by the EF Core stores: two tables — <c>SetNetDocuments</c> (key/value JSON, keyed
    /// by collection + key) and <c>SetNetSnapshots</c> (opaque blobs). Provider-agnostic: configure it with any EF Core
    /// provider (SQLite, PostgreSQL, SQL Server, …) via <see cref="DbContextOptions"/>.
    /// </summary>
    public sealed class SetNetPersistenceContext : DbContext
    {
        /// <summary>Creates the context from provider-configured options.</summary>
        public SetNetPersistenceContext(DbContextOptions<SetNetPersistenceContext> options) : base(options) { }

        /// <summary>The key/value document table.</summary>
        public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();
        /// <summary>The snapshot table.</summary>
        public DbSet<SnapshotRecord> Snapshots => Set<SnapshotRecord>();

        /// <inheritdoc/>
        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<DocumentRecord>(e =>
            {
                e.ToTable("SetNetDocuments");
                e.HasKey(x => new { x.Collection, x.Key });
                e.Property(x => x.Json).IsRequired();
            });
            b.Entity<SnapshotRecord>(e =>
            {
                e.ToTable("SetNetSnapshots");
                e.HasKey(x => x.Name);
                e.Property(x => x.Data).IsRequired();
            });
        }
    }

    /// <summary>
    /// A <see cref="IDocumentStore{T}"/> backed by EF Core. Values are stored as JSON in the shared <c>SetNetDocuments</c>
    /// table, tagged by a per-type collection — so custom fields need no schema change. A fresh <see cref="DbContext"/> is
    /// created per operation (EF contexts are cheap and not thread-safe).
    /// </summary>
    /// <typeparam name="T">The document type (serialized to JSON).</typeparam>
    public sealed class EfCoreDocumentStore<T> : IDocumentStore<T>
    {
        private readonly DbContextOptions<SetNetPersistenceContext> _options;
        private readonly string _collection;
        private readonly JsonSerializerOptions? _json;

        /// <summary>Creates the store and ensures the schema exists (<see cref="DatabaseFacade.EnsureCreated()"/>).</summary>
        /// <param name="options">Provider-configured options (e.g. from <c>new DbContextOptionsBuilder&lt;SetNetPersistenceContext&gt;().UseSqlite(cs)</c>).</param>
        /// <param name="collection">Logical collection name; defaults to <c>typeof(T).FullName</c>.</param>
        /// <param name="json">Optional JSON options.</param>
        /// <param name="ensureCreated">When true (default), creates the schema on first construction.</param>
        public EfCoreDocumentStore(DbContextOptions<SetNetPersistenceContext> options, string? collection = null,
            JsonSerializerOptions? json = null, bool ensureCreated = true)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _collection = collection ?? typeof(T).FullName ?? typeof(T).Name;
            _json = json;
            if (ensureCreated)
            {
                using var ctx = new SetNetPersistenceContext(_options);
                ctx.Database.EnsureCreated();
            }
        }

        /// <inheritdoc/>
        public async Task<T?> GetAsync(string key)
        {
            await using var ctx = new SetNetPersistenceContext(_options);
            var rec = await ctx.Documents.FindAsync(_collection, key ?? "");
            return rec == null ? default : JsonSerializer.Deserialize<T>(rec.Json, _json);
        }

        /// <inheritdoc/>
        public async Task SetAsync(string key, T value)
        {
            var json = JsonSerializer.Serialize(value, _json);
            await using var ctx = new SetNetPersistenceContext(_options);
            var rec = await ctx.Documents.FindAsync(_collection, key ?? "");
            if (rec == null)
                ctx.Documents.Add(new DocumentRecord { Collection = _collection, Key = key ?? "", Json = json });
            else
                rec.Json = json;
            await ctx.SaveChangesAsync();
        }

        /// <inheritdoc/>
        public async Task<bool> RemoveAsync(string key)
        {
            await using var ctx = new SetNetPersistenceContext(_options);
            var rec = await ctx.Documents.FindAsync(_collection, key ?? "");
            if (rec == null) return false;
            ctx.Documents.Remove(rec);
            await ctx.SaveChangesAsync();
            return true;
        }

        /// <inheritdoc/>
        public async Task<bool> ExistsAsync(string key)
        {
            await using var ctx = new SetNetPersistenceContext(_options);
            var k = key ?? "";
            return await ctx.Documents.AnyAsync(d => d.Collection == _collection && d.Key == k);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<string>> KeysAsync()
        {
            await using var ctx = new SetNetPersistenceContext(_options);
            return await ctx.Documents.Where(d => d.Collection == _collection).Select(d => d.Key).ToListAsync();
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<T>> AllAsync()
        {
            await using var ctx = new SetNetPersistenceContext(_options);
            var jsons = await ctx.Documents.Where(d => d.Collection == _collection).Select(d => d.Json).ToListAsync();
            var list = new List<T>();
            foreach (var j in jsons)
            {
                var v = JsonSerializer.Deserialize<T>(j, _json);
                if (v != null) list.Add(v);
            }
            return list;
        }
    }

    /// <summary>A <see cref="ISnapshotStore"/> backed by EF Core (the <c>SetNetSnapshots</c> blob table).</summary>
    public sealed class EfCoreSnapshotStore : ISnapshotStore
    {
        private readonly DbContextOptions<SetNetPersistenceContext> _options;

        /// <summary>Creates the store and ensures the schema exists.</summary>
        public EfCoreSnapshotStore(DbContextOptions<SetNetPersistenceContext> options, bool ensureCreated = true)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (ensureCreated)
            {
                using var ctx = new SetNetPersistenceContext(_options);
                ctx.Database.EnsureCreated();
            }
        }

        /// <inheritdoc/>
        public async Task SaveAsync(string name, byte[] data)
        {
            await using var ctx = new SetNetPersistenceContext(_options);
            var rec = await ctx.Snapshots.FindAsync(name ?? "");
            if (rec == null)
                ctx.Snapshots.Add(new SnapshotRecord { Name = name ?? "", Data = data ?? Array.Empty<byte>() });
            else
                rec.Data = data ?? Array.Empty<byte>();
            await ctx.SaveChangesAsync();
        }

        /// <inheritdoc/>
        public async Task<byte[]?> LoadAsync(string name)
        {
            await using var ctx = new SetNetPersistenceContext(_options);
            var rec = await ctx.Snapshots.FindAsync(name ?? "");
            return rec?.Data;
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteAsync(string name)
        {
            await using var ctx = new SetNetPersistenceContext(_options);
            var rec = await ctx.Snapshots.FindAsync(name ?? "");
            if (rec == null) return false;
            ctx.Snapshots.Remove(rec);
            await ctx.SaveChangesAsync();
            return true;
        }
    }
}
