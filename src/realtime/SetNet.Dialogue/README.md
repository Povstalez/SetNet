<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Dialogue

**Server-authoritative branching dialogue for [SetNet](https://www.nuget.org/packages/SetNet) — conditional choices, side-effects, walked entirely on the server.**

Build a `DialogueTree` of nodes, each with the NPC's line and a set of choices; a choice can carry an optional guard that hides it and an optional side-effect that fires when it's taken (grant a quest, set a flag). The client sees only the visible choices and picks one by index — all branching, guarding and state live server-side, so the client can't skip to a node it hasn't earned. Pairs naturally with [`SetNet.NPC`](https://www.nuget.org/packages/SetNet.NPC) capability hand-off (`"dialogue:<id>"`). Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Dialogue
```

## Usage

Call `DialogueRuntime.Enable()` once at startup so the channel service is discovered.

```csharp
// server — define a tree, then register it
DialogueRuntime.Enable();
var tree = DialogueTree.Create()
    .Node("start", "Well met, traveller. What brings you here?",
        new DialogueChoice("I seek the old ruins.", next: "ruins"),
        new DialogueChoice("I'll take that quest.", next: "quest",
            condition: ctx => !HasQuest(ctx.PlayerKey),        // hidden once accepted
            onChosen: ctx => GiveQuest(ctx.PlayerKey)))         // side-effect
    .Node("ruins", "Follow the river north.")                   // no choices → ends here
    .Node("quest", "Slay the wolves and return.")
    .Build();
server.UseDialogue().Define("elder", tree);

// client — walk it
DialogueRuntime.Enable();
var dialogue = client.UseDialogue();
DialogueNodeView node = await dialogue.StartAsync("elder");
while (!node.IsEnd)
{
    Render(node.Text, node.Choices);                 // each choice has .Index and .Text
    node = await dialogue.ChooseAsync(node.Choices[pick].Index);
}
```

## API

**Tree:** `DialogueTree.Create()` → `Builder` — `Node(id, text, params DialogueChoice[])`, `Start(id)`, `Build()`.
`DialogueChoice(text, next, condition?, onChosen?)` where `condition`/`onChosen` take a `DialogueContext` (`PlayerKey`, `Services`, `Blackboard`).
**Server:** `server.UseDialogue(DialogueOptions?)` → `DialogueServer` — `Define(dialogueId, DialogueTree)`.
**Client:** `client.UseDialogue()` → `DialogueClient` — `StartAsync(dialogueId)` / `ChooseAsync(choiceIndex)` → `DialogueNodeView` (`NodeId`, `Text`, `IsEnd`, `Choices`).
**Options:** `DialogueOptions.PlayerKey`, `DialogueOptions.Services`. `DialogueRuntime.Enable()` — one-time bootstrap.

## Notes

- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Dialogue` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. The control protocol is hand-framed `byte[]`; a failed op surfaces as `DialogueException`.
- **Server-authoritative.** The server tracks each player's current node; `ChooseAsync` is validated against the *visible* choices at that node, so guards can't be bypassed and a client can't jump to an arbitrary node.
- **Guards run per-view.** A choice's `Condition` is evaluated every time its node is shown — resolve app state (quests, inventory, flags) from `DialogueContext.Services` (an `IServiceProvider` you set via `DialogueOptions.Services`), and stash per-conversation scratch in `Blackboard`.
- **Ends cleanly.** A node with no choices (or a chosen choice whose `Next` is null) ends the conversation; the final `DialogueNodeView` has `IsEnd == true`. A disconnect clears the active conversation.
- **Pairs with NPC.** Have a [`SetNet.NPC`](https://www.nuget.org/packages/SetNet.NPC) behaviour return capability `"dialogue:elder"`, then the client calls `StartAsync("elder")` — the NPC decides *when* to talk, this module decides *what* is said.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
