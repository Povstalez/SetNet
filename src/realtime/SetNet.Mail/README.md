<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Mail

**In-game mail with item attachments for [SetNet](https://www.nuget.org/packages/SetNet).**

Send a message — with items attached — to any player, online or not. Attachments are **escrowed** from the sender's inventory the moment they're sent (so they can't be duplicated) and land in the recipient's inventory only when **claimed**; deleting an unclaimed message returns the items to the sender, so nothing is ever destroyed. New mail is pushed to online recipients; offline players see it next time they log in. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Inventory
dotnet add package SetNet.Mail
```

## Usage

Call `InventoryRuntime.Enable()` and `MailRuntime.Enable()` once at startup on both ends.

**Server** — pass the inventory hub to enable attachments:

```csharp
InventoryRuntime.Enable(); MailRuntime.Enable();
var inventory = server.UseInventory();
var mail = server.UseMail(inventory: inventory);   // + optional IMailStore / MailOptions

// system mail (rewards, announcements) — attachments are minted:
await mail.SendSystemAsync(playerKey, "Welcome!", body, new[] { new MailAttachment("starter_pack", 1) });
```

**Client** — send, read, claim:

```csharp
InventoryRuntime.Enable(); MailRuntime.Enable();
var mail = client.UseMail();
mail.Received += m => Toast($"New mail from {m.From}: {m.Subject}");

await mail.SendAsync(friendKey, "Here's that sword", body: null,
                     attachments: new[] { new MailAttachment("sword#42", 1) });

foreach (var m in await mail.ListAsync())
    Console.WriteLine($"{(m.Read ? " " : "*")} {m.Subject} — {m.Attachments.Count} attachment(s)");

await mail.ClaimAsync(messageId);   // attachments → your inventory
```

## API

**Server:** `server.UseMail(IMailStore?, InventoryServer? inventory, MailOptions?)` → `MailServer` — `SendSystemAsync(...)` for server-originated mail.

**Client:** `var mail = client.UseMail()` → `MailClient`

| Member | Purpose |
|---|---|
| `Task<string> SendAsync(toKey, subject, body?, attachments?)` | send mail; attachments escrowed from your inventory |
| `Task<IReadOnlyList<MailMessage>> ListAsync()` | your mailbox |
| `Task<MailMessage> ReadAsync(id)` | mark read + fetch |
| `Task ClaimAsync(id)` | attachments → inventory (idempotent) |
| `Task DeleteAsync(id)` | delete (unclaimed attachments returned to sender) |
| `event Action<MailMessage> Received` | new mail while online |

**Options:** `MailOptions.PlayerKey`, `MaxAttachments` (8), `MaxBodyBytes` (64 KB).

`InventoryRuntime.Enable()` + `MailRuntime.Enable()` — one-time bootstrap.

## Notes

- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Mail` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. Serializer-agnostic: the control protocol is hand-framed `byte[]`, your payloads use your `SetNetSerializer`.
- **Attachments need an inventory.** Pass the `InventoryServer` to `UseMail`; without it, messages with attachments are rejected (text-only mail still works). Escrow uses `Inventory.TryRevokeAsync`, so a sender can't attach items they don't have.
- **No dupes, no loss.** Items live in exactly one place at a time: sender's inventory → mail escrow → recipient's inventory (on claim) or back to sender (on delete/unclaimed). System mail mints its attachments (server is the source).
- **Persistence.** The default `MemoryMailStore` is per-process. Implement `IMailStore` over Redis/SQL so mailboxes survive restarts and are readable on whichever node the recipient logs into.
- **Opaque body.** The body is `byte[]` — put text, JSON, or a serialized DTO in it; the mail layer never inspects it.
- **Identity.** Mail addresses players by the same key as [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory) — use a stable key and gate with [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
