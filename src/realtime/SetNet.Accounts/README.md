# SetNet.Accounts

Server-side **account store + authentication** for SetNet. A generic `AccountServer<TAccount>` over any [`SetNet.Persistence`](https://www.nuget.org/packages/SetNet.Persistence) `IDocumentStore` (memory / file / SQL / Mongo).

- register / authenticate with a pluggable `IPasswordHasher` (**PBKDF2** default, constant-time verify),
- ban / unban, change password,
- **custom fields with no schema change** — subclass `AccountBase` (or use its `Extra` bag).

```csharp
using SetNet.Accounts;
using SetNet.Persistence;

public sealed class MyAccount : AccountBase
{
    public string? ReferralCode { get; set; }     // custom field
}

var accounts = new AccountServer<MyAccount>(
    new MemoryDocumentStore<MyAccount>(),          // ← swap for a DB store in production
    new MemoryDocumentStore<string>());            // username → id index (use the SAME backing)

var acc = await accounts.RegisterAsync("alice", "s3cret", a => a.ReferralCode = "FRIEND42");

var r = await accounts.AuthenticateAsync("ALICE", "s3cret");   // case-insensitive
if (r.Ok) Console.WriteLine(r.Account!.ReferralCode);
```

`AuthenticateAsync` returns `AccountAuthStatus` (`Ok` / `UnknownUser` / `WrongPassword` / `Banned`). Pairs with **[`SetNet.LoginServer`](https://www.nuget.org/packages/SetNet.LoginServer)** — wire `LoginOptions.Authenticate = (u,p) => accounts.AuthenticateAsync(u,p).ContinueWith(...)`.

Depends on `SetNet` + `SetNet.Persistence`.

## License
MIT
