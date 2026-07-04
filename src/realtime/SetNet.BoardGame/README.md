# SetNet.BoardGame

A **turn-based board / card game framework** for SetNet, with a complete engine for **Durak (Подкидной дурак)**.

- **Card primitives** — `Card` / `Suit` / `Rank`, `Decks.Standard36/52`, deterministic `Shuffle`.
- **Server-authoritative contract** — `ITurnGame<TState, TMove, TView>`: the framework never trusts a client; it asks the game to validate (`LegalMoves`/`Apply`) and to produce a **per-player redacted `View`**, so hidden information (a player's hand) is only ever revealed to its owner.
- **Headless host** — `TurnGameHost<…>` runs a match (drive it from a bot, a test, or a networked hub).
- **Durak engine** — `DurakGame` implements the contract: 36-card deck, trump from the bottom card, deal 6, attack / defend / throw-in / take / Бито, refill to 6, last player holding cards is the **durak**.

## Durak in a few lines

```csharp
using SetNet.BoardGame;

var game = new DurakGame(2);                       // 2..6 players
var state = game.Start(new[] { "Alice", "Bob" }, seed: 7);

while (game.Outcome(state) is null)
{
    var seat = game.CurrentSeat(state);            // whose turn
    var view = game.View(state, seat);             // ← this seat sees ONLY its own hand
    var move = ChooseMove(game, state, seat, view);// your bot / your player
    state = game.Apply(state, seat, move);         // validated by the engine (throws GameException if illegal)
}

var outcome = game.Outcome(state)!;                // Winners / Losers (the durak)
```

Moves: `DurakMove.Attack(card)`, `Defend(index, card)`, `Take()`, `Done()`. The engine exposes `LegalMoves(state, seat)` so a client/bot only ever offers valid choices, and the server re-validates every `Apply`.

### Hidden information

`View(state, seat)` returns a `DurakView` with the viewer's **own hand in full** but everyone else only by **count** (`HandCounts`), plus the public table, trump, deck size and whose turn it is. That's the piece that makes networked card games safe — you send each player only their own view.

## Writing another game

Implement `ITurnGame<TState, TMove, TView>` (seats, `Start`, `CurrentSeat`, `LegalMoves`, `Apply`, `View`, `Outcome`) and drive it with `TurnGameHost`. Durak is the reference implementation.

## Notes / simplifications

Durak here is 2..6 players with **attacker-only throw-ins** and a **sequential bout** (the attacker adds a card only once the table is fully beaten) — rule-legal and deterministic. "Perevodnoy" (bouncing) is not implemented. Deterministic given the shuffle seed, so games are reproducible and unit-testable.

Depends only on `SetNet`. No wire protocol yet — the engine is transport-agnostic; a networked BoardGame hub (tables + per-player view push over `Channels.BoardGame`) is the natural next layer.

See **`examples/Durak`** for a full game played by two bots. **License:** MIT.
