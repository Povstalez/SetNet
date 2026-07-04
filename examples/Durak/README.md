# Durak example

A full game of **Подкидной дурак** played by two bots on the [`SetNet.BoardGame`](https://www.nuget.org/packages/SetNet.BoardGame) engine.

- **Server-authoritative:** each bot decides using **only its own `DurakView`** (its hand + the public table + card counts) — it never sees the opponent's cards. The engine validates every `Apply`.
- **Deterministic:** pass a seed to reproduce a deal.

```bash
dotnet run --project examples/Durak            # default seed
dotnet run --project examples/Durak -- 42      # a specific deal
```

Sample tail:

```
Alice  attacks: Attack 7♥
   table: 7♥/6♦   deck:14  hands: Alice=5 Bob=5     # 6♦ is trump — it beats the 7♥
...
   — table cleared —   deck:0  hands: Alice=8 Bob=0

🏆 Bob wins. 🃏 Alice is the DURAK.
```

The same engine drives a networked game: send each player their `View` and accept `DurakMove`s validated by `Apply` (a `SetNet.BoardGame` hub on `Channels.BoardGame` is the natural next layer). This demo runs headless so you can watch a whole game in one process.
