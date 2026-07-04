# SetNet.Persistence.EfCore

Entity Framework Core-backed `IDocumentStore<T>` / `ISnapshotStore` for [`SetNet.Persistence`](https://www.nuget.org/packages/SetNet.Persistence).

**Provider-agnostic** — you pass `DbContextOptions` configured for whatever EF Core provider you use (SQLite, PostgreSQL, SQL Server…). Values are stored as JSON in a shared key/value table tagged by a per-type collection, so **custom fields need no schema change**.

```csharp
using SetNet.Persistence.EfCore;
using Microsoft.EntityFrameworkCore;

var options = new DbContextOptionsBuilder<SetNetPersistenceContext>()
    .UseSqlite("Data Source=game.db")           // or .UseNpgsql(...), .UseSqlServer(...)
    .Options;

var store = new EfCoreDocumentStore<CharacterData>(options);   // schema auto-created (EnsureCreated)
await store.SetAsync("char:1", new CharacterData { Name = "Archer", Level = 80, VipUntil = DateTime.UtcNow.AddDays(30) });
var c = await store.GetAsync("char:1");

var snaps = new EfCoreSnapshotStore(options);
await snaps.SaveAsync("world", worldBlob);
```

Two tables: `SetNetDocuments` (`Collection` + `Key` → JSON) and `SetNetSnapshots` (`Name` → blob). A fresh `DbContext` is created per operation (EF contexts are cheap and not thread-safe). For real migrations, add these entities to your own context instead of relying on `EnsureCreated`.

Depends on `SetNet.Persistence` + `Microsoft.EntityFrameworkCore.Relational`. Targets `net8.0`.

## License
MIT
