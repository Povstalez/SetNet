# SetNet.GameData

Static **game-data tables** for SetNet: load item / skill / npc / spawn definitions from JSON into fast id-keyed lookups, **hot-reloadable** at runtime. Rows are your own POCOs, so every table can carry **any custom columns**. No wire protocol; usable server- and client-side.

```csharp
using SetNet.GameData;

public sealed class ItemDef                     // your row type — any columns you like
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Grade { get; set; }
    public bool Tradable { get; set; }
}

var data = new GameDataRegistry();

var items = data.LoadFile<int, ItemDef>("items", "data/items.json", r => r.Id);   // or LoadJson(name, jsonString, …)
var sword = items.Get(1);                        // O(1) lookup
foreach (var it in items.All) { /* … */ }

// live-reload after editing data/items.json on disk:
data.Reload();                                   // re-reads every file-backed table, atomically swaps them in
var fresh = data.Get<int, ItemDef>("items");
```

## From a database (Postgres, EF Core, Dapper, Mongo…)

Tables aren't limited to files — `Load` takes any `IEnumerable<TRow>`, and `LoadFrom` takes a reloadable source that `Reload()` re-runs (re-queries the DB):

```csharp
// from PostgreSQL via SetNet.Persistence.Postgres (or any IDocumentStore):
var itemStore = new PostgresDocumentStore<ItemDef>(cs, "items");
data.LoadFrom<int, ItemDef>("items",
    () => itemStore.AllAsync().GetAwaiter().GetResult(), r => r.Id);

// or straight from Dapper:
data.LoadFrom<int, ItemDef>("items",
    () => conn.Query<ItemDef>("SELECT * FROM items"), r => r.Id);

data.Reload();   // re-queries the DB and swaps the table in atomically (great for a "reload game data" admin button)
```

So GameData keeps its fast in-memory lookups, but the *source of truth* can be JSON files **or** a database — your choice per table.

`DataTable<TId,TRow>`: `Get`/`TryGet`/`Contains`/`All`/`Ids`/`Count`. `GameDataRegistry`: `Load` (any enumerable) / `LoadFrom` (reloadable source, e.g. DB) / `LoadJson` / `LoadFile` / `Get` / `Has` / `Names` / `Reload` (+ `Reloaded` event). JSON parsing is lenient by default (case-insensitive, comments + trailing commas allowed); pass your own `JsonSerializerOptions` to change it.

Depends only on `System.Text.Json` (no DB dependency — you supply the query).

## License
MIT
