# SetNet.Persistence.Postgres

PostgreSQL-backed `IDocumentStore<T>` / `ISnapshotStore` for [`SetNet.Persistence`](https://www.nuget.org/packages/SetNet.Persistence) via **Npgsql**.

Each value is stored in a **JSONB** column, so custom fields need no schema change — **and**, unlike an opaque blob, they stay **queryable and indexable in SQL**:

```sql
-- find VIP characters straight from the DB
SELECT id FROM setnet_documents WHERE (doc->>'VipUntil')::timestamptz > now();
-- and index it
CREATE INDEX ON setnet_documents ((doc->>'VipUntil'));
```

```csharp
using SetNet.Persistence.Postgres;

var store = new PostgresDocumentStore<CharacterData>("Host=localhost;Username=game;Password=…;Database=game");
await store.SetAsync("char:1", new CharacterData { Name = "Archer", Level = 80, VipUntil = DateTime.UtcNow.AddDays(30) });
var c = await store.GetAsync("char:1");
```

`new PostgresDocumentStore<T>(connectionString, table = "setnet_documents", json?)` — auto-creates `(id text pk, doc jsonb)`, upserts via `INSERT … ON CONFLICT`. `PostgresSnapshotStore` uses a `BYTEA` column. Table names are trusted constants; ids/values are parameterized.

Depends on `SetNet.Persistence` + `Npgsql`. Targets `net8.0`.

## License
MIT
