# SetNet.Persistence.MongoDB

MongoDB-backed `IDocumentStore<T>` / `ISnapshotStore` for [`SetNet.Persistence`](https://www.nuget.org/packages/SetNet.Persistence) via **MongoDB.Driver**.

Each value is stored as a **native BSON document** (under `value`, keyed by `_id`), so custom fields need no schema change **and** stay queryable/indexable in Mongo (e.g. an index on `value.VipUntil`).

```csharp
using SetNet.Persistence.MongoDB;
using MongoDB.Driver;

var db = new MongoClient("mongodb://localhost:27017").GetDatabase("game");

var store = new MongoDocumentStore<CharacterData>(db);           // collection defaults to type name
await store.SetAsync("char:1", new CharacterData { Name = "Archer", Level = 80, VipUntil = DateTime.UtcNow.AddDays(30) });
var c = await store.GetAsync("char:1");

var snaps = new MongoSnapshotStore(db);
await snaps.SaveAsync("world", worldBlob);
```

`new MongoDocumentStore<T>(database, collectionName?)` upserts via `ReplaceOne(IsUpsert)`. `MongoSnapshotStore` stores opaque blobs.

Depends on `SetNet.Persistence` + `MongoDB.Driver`. Targets `net8.0`.

## License
MIT
