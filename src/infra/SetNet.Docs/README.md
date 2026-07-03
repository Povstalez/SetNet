<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Docs

**Auto-generated protocol documentation for [SetNet](https://www.nuget.org/packages/SetNet) — reflect your handlers, channels and RPC methods into Markdown.**

Point it at your loaded assemblies and it discovers every `[MessageHandler]` (type id, direction, payload), every unified-protocol `[ProtocolChannel]` with its `[Op]`/`[Event]` ids, and every `[RpcMethod]`, then renders a `ProtocolReport` you can print, drop into a README, or serve for debugging. Attributes are matched by name, so it needs no reference to the RPC or protocol packages. Tooling only — no wire protocol.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Docs
```

## Usage

Call it after your modules' `Runtime.Enable()` so their handlers and channel services are loaded.

```csharp
// bootstrap your modules first (so their assemblies are loaded)
RoomsRuntime.Enable();
WalletRuntime.Enable();
// ...

// straight to Markdown
string md = ProtocolDocs.GenerateMarkdown();
Console.WriteLine(md);

// or write it to a file
ProtocolDocs.WriteMarkdown("docs/PROTOCOL.md");

// or inspect the structured report
ProtocolReport report = ProtocolDocs.Generate();
foreach (var c in report.Channels)
    Console.WriteLine($"{c.Name} ({c.Channel}): {c.Ops.Count} op(s), {c.Events.Count} event(s)");
```

Produces a table per section, e.g.:

```
## Unified protocol channels

| Channel | Id | Ops | Events | Handled by |
|---|---|---|---|---|
| Wallet | 3 | Deposit(1), Withdraw(2) | Changed(10) | WalletChannelService |
```

## API

`ProtocolDocs.Generate(IEnumerable<Assembly>? = null)` → `ProtocolReport` (defaults to all loaded assemblies).
`ProtocolDocs.GenerateMarkdown(…)` → the report as a Markdown string.
`ProtocolDocs.WriteMarkdown(path, …)` → writes the Markdown to a file (creating the directory).
`ProtocolReport` — `Handlers` (`HandlerDoc`), `Channels` (`ChannelDoc`), `RpcMethods` (`RpcDoc`), plus `ToMarkdown()`.

## Notes

- **Reflection, matched by name.** Handlers, channels and RPC methods are found by attribute *name* (`MessageHandlerAttribute`, `ProtocolChannelAttribute`, `OpAttribute`, `EventAttribute`, `RpcMethodAttribute`) — so `SetNet.Docs` depends only on `SetNet` and still documents the RPC package without referencing it. Channel names are read from `SetNet.Protocol.Channels` when present.
- **Enable first.** A module's handlers/channel services aren't discoverable until its assembly is loaded — call each module's `Runtime.Enable()` before generating, or you'll get an incomplete report.
- **Robust to bad metadata.** Types and attributes that fail to load (test hosts, trimmed assemblies) are skipped rather than throwing, so a report always comes back.
- **Deterministic output.** Handlers and RPC methods are sorted by id and channels by id, so regenerating produces stable diffs — commit `PROTOCOL.md` and let CI keep it honest.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
