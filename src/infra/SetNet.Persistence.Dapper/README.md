# SetNet.Persistence.Dapper

Dapper-backed `IDocumentStore<T>` / `ISnapshotStore` for [`SetNet.Persistence`](https://www.nuget.org/packages/SetNet.Persistence) — store any POCO as JSON in a key/value table on **any ADO.NET database** (SQLite, PostgreSQL, MySQL, SQL Server…).

**Custom fields need no schema change** — the whole object is serialized, so a `CharacterData` with a per-character `VipUntil` just works. Upsert is portable (delete + insert in a transaction), so no dialect-specific `ON CONFLICT`/`MERGE`.

```csharp
using SetNet.Persistence.Dapper;
using Microsoft.Data.Sqlite;

public sealed class CharacterData
{
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public DateTime? VipUntil { get; set; }                 // custom field — no migration
    public Dictionary<string, object> Extra { get; set; } = new();
}

var store = new DapperDocumentStore<CharacterData>(
    () => new SqliteConnection("Data Source=game.db"));     // any ADO.NET connection factory

await store.SetAsync("char:1", new CharacterData { Name = "Archer", Level = 80, VipUntil = DateTime.UtcNow.AddDays(30) });
var c = await store.GetAsync("char:1");
```

`new DapperDocumentStore<T>(connectionFactory, table = "setnet_documents", json?, ensureTableSql?)` — pass `ensureTableSql` for engines without `CREATE TABLE IF NOT EXISTS` (e.g. SQL Server). `DapperSnapshotStore` stores opaque blobs the same way. Table names are interpolated into SQL, so keep them trusted constants; keys/values are always parameterized.

Depends on `SetNet.Persistence` + `Dapper`. Targets `net8.0`.

## License
MIT
