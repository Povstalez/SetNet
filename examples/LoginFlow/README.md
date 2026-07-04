# LoginFlow example

The full **L2-style entry flow** in one process, tying four modules together:

```
client ──login──▶ LoginServer ──verify──▶ SetNet.Accounts
client ──list───▶ LoginServer            (game-server list)
client ─select──▶ LoginServer ── issues one-time token ──▶ shared token store
[game] ── consumes token ──▶ SetNet.CharacterStore ── character select ──▶ ENTER WORLD (+ SetNet.GameData)
```

- **`SetNet.Accounts`** — one account (`alice`) is seeded; the login node authenticates against it (PBKDF2).
- **`SetNet.LoginServer`** — the client talks to the login node **over a real (in-memory) SetNet connection**: `LoginAsync` → `ServerListAsync` → `SelectServerAsync` → a one-time token + `host:port`.
- **`SetNet.CharacterStore`** — the "game server" consumes the token, lists the account's characters (creates a starter if empty), and the chosen character carries a **custom `VipUntil` field** (no schema change).
- **`SetNet.GameData`** — a starter item is looked up from a loaded table.

The client↔login hop is real wire; the game-server side (token validation + character select) runs in-process here for clarity — in a real deployment the client connects to the advertised game node and its handshake handler runs exactly those calls.

## Run

```bash
dotnet run --project examples/LoginFlow
```

```
client: login as alice / secret →
    Ok
client: server list →
    [bartz] Bartz      1240/2000  (good)  game1.example.com:7777
    [sieghardt] Sieghardt  1990/2000  (busy)  game2.example.com:7777
client: select 'bartz' →
    ok=True  connect to game1.example.com:7777  token=8f92cf49…
[game] token valid → account 007166e2… for server 'bartz'
[game] character select:
    slot 0: Archer     class=5  VIP until 2026-08-02
[game] ENTER WORLD as 'Archer'.
[game] starter item from GameData: Short Sword (grade 1).
```

## Toward production

Swap the memory stores for a durable backend behind the same interfaces — e.g. `SetNet.Persistence.Postgres` for accounts/characters and a Redis `ILoginTokenStore` so the token works across separate login and game processes.
