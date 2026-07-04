using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using SetNet.Persistence;

namespace SetNet.Persistence.MongoDB
{
    /// <summary>
    /// A <see cref="IDocumentStore{T}"/> backed by MongoDB (<c>MongoDB.Driver</c>). Each value is stored as a native BSON
    /// document (under <c>value</c>, keyed by <c>_id</c>), so your POCO can carry any <b>custom fields</b> with no schema
    /// change — and, being real BSON, those fields stay <b>queryable and indexable</b> in Mongo (e.g. an index on
    /// <c>value.vipUntil</c>). Upsert via <c>ReplaceOne(IsUpsert)</c>.
    /// </summary>
    /// <typeparam name="T">The document type (stored as BSON).</typeparam>
    public sealed class MongoDocumentStore<T> : IDocumentStore<T>
    {
        private readonly IMongoCollection<Wrapper> _col;

        /// <summary>Creates the store over a collection (default = <c>typeof(T).Name</c>).</summary>
        /// <param name="database">The Mongo database.</param>
        /// <param name="collectionName">Collection name; defaults to the type name.</param>
        public MongoDocumentStore(IMongoDatabase database, string? collectionName = null)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            _col = database.GetCollection<Wrapper>(collectionName ?? typeof(T).Name);
        }

        /// <inheritdoc/>
        public async Task<T?> GetAsync(string key)
        {
            var id = key ?? "";
            var doc = await _col.Find(x => x.Id == id).FirstOrDefaultAsync();
            return doc == null ? default : doc.Value;
        }

        /// <inheritdoc/>
        public async Task SetAsync(string key, T value)
        {
            var w = new Wrapper { Id = key ?? "", Value = value };
            await _col.ReplaceOneAsync(x => x.Id == w.Id, w, new ReplaceOptions { IsUpsert = true });
        }

        /// <inheritdoc/>
        public async Task<bool> RemoveAsync(string key)
        {
            var id = key ?? "";
            var r = await _col.DeleteOneAsync(x => x.Id == id);
            return r.DeletedCount > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> ExistsAsync(string key)
        {
            var id = key ?? "";
            return await _col.Find(x => x.Id == id).Limit(1).CountDocumentsAsync() > 0;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<string>> KeysAsync()
            => await _col.Find(FilterDefinition<Wrapper>.Empty).Project(x => x.Id).ToListAsync();

        /// <inheritdoc/>
        public async Task<IReadOnlyList<T>> AllAsync()
        {
            var all = await _col.Find(FilterDefinition<Wrapper>.Empty).ToListAsync();
            var list = new List<T>(all.Count);
            foreach (var w in all)
                if (w.Value != null) list.Add(w.Value);
            return list;
        }

        /// <summary>The stored envelope: the key as <c>_id</c>, the value as a nested BSON document.</summary>
        public sealed class Wrapper
        {
            /// <summary>The document key (<c>_id</c>).</summary>
            [BsonId] public string Id { get; set; } = "";
            /// <summary>The value document.</summary>
            public T Value { get; set; } = default!;
        }
    }

    /// <summary>A <see cref="ISnapshotStore"/> storing opaque blobs in a MongoDB collection.</summary>
    public sealed class MongoSnapshotStore : ISnapshotStore
    {
        private readonly IMongoCollection<SnapWrapper> _col;

        /// <summary>Creates the store over a collection (default <c>setnet_snapshots</c>).</summary>
        public MongoSnapshotStore(IMongoDatabase database, string collectionName = "setnet_snapshots")
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            _col = database.GetCollection<SnapWrapper>(collectionName);
        }

        /// <inheritdoc/>
        public async Task SaveAsync(string name, byte[] data)
        {
            var w = new SnapWrapper { Id = name ?? "", Data = data ?? Array.Empty<byte>() };
            await _col.ReplaceOneAsync(x => x.Id == w.Id, w, new ReplaceOptions { IsUpsert = true });
        }

        /// <inheritdoc/>
        public async Task<byte[]?> LoadAsync(string name)
        {
            var id = name ?? "";
            var w = await _col.Find(x => x.Id == id).FirstOrDefaultAsync();
            return w?.Data;
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteAsync(string name)
        {
            var id = name ?? "";
            var r = await _col.DeleteOneAsync(x => x.Id == id);
            return r.DeletedCount > 0;
        }

        /// <summary>The stored envelope for a snapshot.</summary>
        public sealed class SnapWrapper
        {
            /// <summary>The snapshot name (<c>_id</c>).</summary>
            [BsonId] public string Id { get; set; } = "";
            /// <summary>The blob.</summary>
            public byte[] Data { get; set; } = Array.Empty<byte>();
        }
    }
}
