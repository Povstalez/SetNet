using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;

namespace SetNet.Persistence
{
    /// <summary>
    /// A keyed document store for a value type — the durable-state seam module stores plug into. Back inventories,
    /// wallets, progression, save-games, etc. with something persistent instead of in-process dictionaries, then swap
    /// implementations (memory ↔ JSON file ↔ your own Redis/DB) without touching callers.
    /// </summary>
    public interface IDocumentStore<T>
    {
        /// <summary>Gets a document by key (default(T)/null when absent).</summary>
        Task<T?> GetAsync(string key);
        /// <summary>Stores (or replaces) a document.</summary>
        Task SetAsync(string key, T value);
        /// <summary>Removes a document; true if it existed.</summary>
        Task<bool> RemoveAsync(string key);
        /// <summary>Whether a key exists.</summary>
        Task<bool> ExistsAsync(string key);
        /// <summary>All keys.</summary>
        Task<IReadOnlyList<string>> KeysAsync();
        /// <summary>All stored documents.</summary>
        Task<IReadOnlyList<T>> AllAsync();
    }

    /// <summary>An in-process document store. Fine for tests / single-node; not durable.</summary>
    public sealed class MemoryDocumentStore<T> : IDocumentStore<T>
    {
        private readonly ConcurrentDictionary<string, T> _docs = new ConcurrentDictionary<string, T>();

        /// <inheritdoc/>
        public Task<T?> GetAsync(string key) => Task.FromResult(_docs.TryGetValue(key ?? "", out var v) ? v : default);
        /// <inheritdoc/>
        public Task SetAsync(string key, T value) { _docs[key ?? ""] = value; return Task.CompletedTask; }
        /// <inheritdoc/>
        public Task<bool> RemoveAsync(string key) => Task.FromResult(_docs.TryRemove(key ?? "", out _));
        /// <inheritdoc/>
        public Task<bool> ExistsAsync(string key) => Task.FromResult(_docs.ContainsKey(key ?? ""));
        /// <inheritdoc/>
        public Task<IReadOnlyList<string>> KeysAsync() => Task.FromResult<IReadOnlyList<string>>(new List<string>(_docs.Keys));
        /// <inheritdoc/>
        public Task<IReadOnlyList<T>> AllAsync() => Task.FromResult<IReadOnlyList<T>>(new List<T>(_docs.Values));
    }

    /// <summary>
    /// A document store persisted to a single JSON file (a <c>key → value</c> map), loaded on construction and rewritten
    /// on every mutation. Simple and human-readable — good for small/medium data and dev; swap for a database at scale.
    /// </summary>
    public sealed class JsonFileDocumentStore<T> : IDocumentStore<T>
    {
        private readonly string _path;
        private readonly JsonSerializerOptions _json;
        private readonly object _gate = new object();
        private readonly Dictionary<string, T> _docs;

        /// <summary>Opens (or creates) a store at <paramref name="path"/>.</summary>
        public JsonFileDocumentStore(string path, JsonSerializerOptions? jsonOptions = null)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _json = jsonOptions ?? new JsonSerializerOptions { WriteIndented = true };
            _docs = Load();
        }

        private Dictionary<string, T> Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var map = JsonSerializer.Deserialize<Dictionary<string, T>>(json, _json);
                    if (map != null) return map;
                }
            }
            catch { /* corrupt/absent → start empty */ }
            return new Dictionary<string, T>();
        }

        private void Save()
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_docs, _json));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);   // atomic-ish replace
        }

        /// <inheritdoc/>
        public Task<T?> GetAsync(string key) { lock (_gate) return Task.FromResult(_docs.TryGetValue(key ?? "", out var v) ? v : default); }
        /// <inheritdoc/>
        public Task SetAsync(string key, T value) { lock (_gate) { _docs[key ?? ""] = value; Save(); } return Task.CompletedTask; }
        /// <inheritdoc/>
        public Task<bool> RemoveAsync(string key) { lock (_gate) { if (_docs.Remove(key ?? "")) { Save(); return Task.FromResult(true); } return Task.FromResult(false); } }
        /// <inheritdoc/>
        public Task<bool> ExistsAsync(string key) { lock (_gate) return Task.FromResult(_docs.ContainsKey(key ?? "")); }
        /// <inheritdoc/>
        public Task<IReadOnlyList<string>> KeysAsync() { lock (_gate) return Task.FromResult<IReadOnlyList<string>>(new List<string>(_docs.Keys)); }
        /// <inheritdoc/>
        public Task<IReadOnlyList<T>> AllAsync() { lock (_gate) return Task.FromResult<IReadOnlyList<T>>(new List<T>(_docs.Values)); }
    }

    /// <summary>An opaque byte-blob store — for world snapshots, save-game blobs, serialized module state, etc.</summary>
    public interface ISnapshotStore
    {
        /// <summary>Saves a named snapshot.</summary>
        Task SaveAsync(string name, byte[] data);
        /// <summary>Loads a named snapshot (null when absent).</summary>
        Task<byte[]?> LoadAsync(string name);
        /// <summary>Deletes a named snapshot; true if it existed.</summary>
        Task<bool> DeleteAsync(string name);
    }

    /// <summary>An in-process snapshot store.</summary>
    public sealed class MemorySnapshotStore : ISnapshotStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _snaps = new ConcurrentDictionary<string, byte[]>();
        /// <inheritdoc/>
        public Task SaveAsync(string name, byte[] data) { _snaps[name ?? ""] = data ?? Array.Empty<byte>(); return Task.CompletedTask; }
        /// <inheritdoc/>
        public Task<byte[]?> LoadAsync(string name) => Task.FromResult(_snaps.TryGetValue(name ?? "", out var d) ? d : null);
        /// <inheritdoc/>
        public Task<bool> DeleteAsync(string name) => Task.FromResult(_snaps.TryRemove(name ?? "", out _));
    }

    /// <summary>A snapshot store that writes each snapshot to a file under a directory (<c>&lt;name&gt;.snapshot</c>).</summary>
    public sealed class FileSnapshotStore : ISnapshotStore
    {
        private readonly string _directory;
        /// <summary>Creates a store rooted at <paramref name="directory"/>.</summary>
        public FileSnapshotStore(string directory) { _directory = directory ?? throw new ArgumentNullException(nameof(directory)); Directory.CreateDirectory(directory); }

        private string PathFor(string name) => Path.Combine(_directory, SafeName(name) + ".snapshot");
        private static string SafeName(string name) => string.Join("_", (name ?? "snapshot").Split(Path.GetInvalidFileNameChars()));

        /// <inheritdoc/>
        public async Task SaveAsync(string name, byte[] data)
        {
            var path = PathFor(name);
            using var fs = File.Create(path);
            await fs.WriteAsync(data ?? Array.Empty<byte>(), 0, data?.Length ?? 0).ConfigureAwait(false);
        }
        /// <inheritdoc/>
        public async Task<byte[]?> LoadAsync(string name)
        {
            var path = PathFor(name);
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var ms = new MemoryStream();
            await fs.CopyToAsync(ms).ConfigureAwait(false);
            return ms.ToArray();
        }
        /// <inheritdoc/>
        public Task<bool> DeleteAsync(string name)
        {
            var path = PathFor(name);
            if (File.Exists(path)) { File.Delete(path); return Task.FromResult(true); }
            return Task.FromResult(false);
        }
    }
}
