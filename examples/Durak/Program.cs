// Durak — a full game of Подкидной дурак played by two bots on the SetNet.BoardGame engine.
//
//   dotnet run --project examples/Durak            # default seed
//   dotnet run --project examples/Durak -- 42      # a specific deal
//
// The engine is server-authoritative: each bot decides using ONLY its own `DurakView` (its hand + public table + card
// counts) — it never sees the opponent's cards. The same engine drives a networked game (a BoardGame hub is the next
// step); here it runs headless so you can watch a whole game.

using SetNet.BoardGame;

var seed = args.Length > 0 && int.TryParse(args[0], out var sd) ? sd : 7;
var names = new[] { "Alice", "Bob" };

var game = new DurakGame(2);
var state = game.Start(names, seed);

Console.WriteLine($"== Durak (seed {seed}) — trump {state.TrumpCard} ==\n");

var move = 0;
while (game.Outcome(state) == null && move++ < 2000)
{
    var seat = game.CurrentSeat(state);
    var view = game.View(state, seat);                  // the bot sees ONLY this
    var chosen = Bot.Pick(game, state, seat, view);

    var role = seat == state.Attacker ? "attacks" : seat == state.Defender ? "defends" : "plays";
    Console.WriteLine($"{names[seat],-6} {role}: {chosen}");

    state = game.Apply(state, seat, chosen);

    if (state.Attacks.Count > 0)
        Console.WriteLine($"   table: {Table(state)}   deck:{state.Deck.Count}  hands: {names[0]}={state.Hands[0].Count} {names[1]}={state.Hands[1].Count}");
    else
        Console.WriteLine($"   — table cleared —   deck:{state.Deck.Count}  hands: {names[0]}={state.Hands[0].Count} {names[1]}={state.Hands[1].Count}\n");
}

var outcome = game.Outcome(state)!;
Console.WriteLine(outcome.Note == "draw"
    ? "\nDraw — both players ran out together."
    : $"\n🏆 {names[outcome.Winners[0]]} wins. 🃏 {names[outcome.Losers[0]]} is the DURAK.");

static string Table(DurakState s)
{
    var parts = new List<string>();
    for (var i = 0; i < s.Attacks.Count; i++)
        parts.Add(s.Defenses[i] is { } d ? $"{s.Attacks[i]}/{d}" : $"{s.Attacks[i]}/·");
    return string.Join("  ", parts);
}

// A simple greedy bot. It plays only from its own view's hand (hidden information respected); the engine still
// validates every move as the authority.
static class Bot
{
    static int Val(Card c, Suit trump) => (c.Suit == trump ? 100 : 0) + (int)c.Rank;

    public static DurakMove Pick(DurakGame g, DurakState s, int seat, DurakView view)
    {
        var moves = g.LegalMoves(s, seat);

        // Defender: beat with the cheapest card, else take.
        var defends = moves.Where(m => m.Kind == DurakMoveKind.Defend).OrderBy(m => Val(m.Card, view.Trump)).ToList();
        if (defends.Count > 0) return defends[0];
        if (moves.Any(m => m.Kind == DurakMoveKind.Take)) return DurakMove.Take();

        // Attacker: throw in a cheap matching low card sometimes (for flavour), otherwise close the bout.
        var attacks = moves.Where(m => m.Kind == DurakMoveKind.Attack).OrderBy(m => Val(m.Card, view.Trump)).ToList();
        var canDone = moves.Any(m => m.Kind == DurakMoveKind.Done);
        if (canDone)
        {
            var cheapThrowIns = attacks.Where(m => m.Card.Suit != view.Trump && (int)m.Card.Rank < 10).ToList();
            if (view.Attacks.Count < 3 && cheapThrowIns.Count > 0) return cheapThrowIns[0];
            return DurakMove.Done();
        }
        return attacks[0];   // opening attack
    }
}
