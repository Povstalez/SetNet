<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Persistence

**A durable-state seam for [SetNet](https://www.nuget.org/packages/SetNet) module stores — one interface, in-memory or JSON-file out of the box, your Redis/DB behind the same shape.**

Most SetNet modules keep their state (inventories, wallets, progression, mail queues, save-games) behind a small store interface that defaults to an in-process dictionary. This package gives those seams something durable: `IDocumentStore<T>` for keyed values and `ISnapshotStore` for opaque blobs, each with a memory implementation for tests and a file implementation for dev — swap in a Redis/DB store later without touching callers. No wire protocol; pure storage.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Persistence
```

## Usage

```csharp
// keyed documents — get/set/remove/enumerate by key
IDocumentStore<PlayerSave> saves = new JsonFileDocumentStore<PlayerSave>("data/saves.json");
await saves.SetAsync(playerKey, new PlayerSave { Level = 12, Gold = 500 });
PlayerSave? save = await saves.GetAsync(playerKey);
IReadOnlyList<string> keys = await saves.KeysAsync();

// opaque blobs — world snapshots, serialized module state
ISnapshotStore snaps = new FileSnapshotStore("data/snapshots");
await snaps.SaveAsync("world", worldBytes);
byte[]? bytes = await snaps.LoadAsync("world");

// tests / single-node: in-process, non-durable
IDocumentStore<PlayerSave> mem = new MemoryDocumentStore<PlayerSave>();
```

## API

**Documents:** `IDocumentStore<T>` — `GetAsync`, `SetAsync`, `RemoveAsync`, `ExistsAsync`, `KeysAsync`, `AllAsync`.
Implementations: `MemoryDocumentStore<T>`, `JsonFileDocumentStore<T>(path, JsonSerializerOptions?)`.
**Snapshots:** `ISnapshotStore` — `SaveAsync`, `LoadAsync`, `DeleteAsync`.
Implementations: `MemorySnapshotStore`, `FileSnapshotStore(directory)`.

## Notes

- **A seam, not a hub.** There's no `UseX()` and no `Runtime.Enable()` — this is plumbing. Pass one of these stores wherever a module accepts its store interface, or implement the module's own store interface on top of an `IDocumentStore<T>`.
- **Memory for tests, file for dev, your own at scale.** `MemoryDocumentStore<T>`/`MemorySnapshotStore` are in-process and non-durable. `JsonFileDocumentStore<T>` keeps a single human-readable `key → value` map, loaded on construction and rewritten (via a temp-file swap) on every mutation — great for small/medium data; move to a database at scale. `FileSnapshotStore` writes one `<name>.snapshot` file per blob.
- **Thread-safe.** The file document store guards reads and writes with a lock; the memory stores use `ConcurrentDictionary`.
- **`System.Text.Json`.** `JsonFileDocumentStore<T>` serializes with `System.Text.Json` (`WriteIndented` by default) — pass your own `JsonSerializerOptions` to control naming, converters, etc. Make sure `T` is round-trippable.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
