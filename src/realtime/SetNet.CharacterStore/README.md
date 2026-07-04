# SetNet.CharacterStore

Server-side **character store** for SetNet. A generic `CharacterServer<TChar>` over any [`SetNet.Persistence`](https://www.nuget.org/packages/SetNet.Persistence) `IDocumentStore`.

- create / list-by-account / get / save / rename,
- per-account **slot limit**, optional **globally-unique names**,
- **soft-delete with a restore window** + hard purge,
- **custom fields with no schema change** — subclass `CharacterBase` (or use its `Extra` bag).

```csharp
using SetNet.CharacterStore;
using SetNet.Persistence;

public sealed class MyCharacter : CharacterBase
{
    public int ClassId { get; set; }
    public DateTime? VipUntil { get; set; }        // ← per-character custom field
}

var chars = new CharacterServer<MyCharacter>(
    new MemoryDocumentStore<MyCharacter>(),         // swap for a DB store
    new CharacterOptions { MaxPerAccount = 7 });

var c = await chars.CreateAsync("acc-1", new MyCharacter { Name = "Archer", Slot = 0, VipUntil = DateTime.UtcNow.AddDays(30) });
var mine = await chars.ListAsync("acc-1");          // for the character-select screen
await chars.SoftDeleteAsync(c.Id);                  // restorable within the window
await chars.RestoreAsync(c.Id);
```

**Scale note:** `ListAsync`/`IsNameTakenAsync` scan the store (`AllAsync`) — fine for modest counts and tests; for a large world back it with a DB store and add indexed queries (or a secondary index).

Depends on `SetNet` + `SetNet.Persistence`. Pairs with `SetNet.Accounts` (the `AccountId`) and `SetNet.LoginServer` (the login flow before character-select).

## License
MIT
