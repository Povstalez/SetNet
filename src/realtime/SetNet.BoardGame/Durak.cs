using System;
using System.Collections.Generic;
using System.Linq;

namespace SetNet.BoardGame
{
    /// <summary>The kind of a Durak move.</summary>
    public enum DurakMoveKind : byte
    {
        /// <summary>Attacker puts a card down (opening attack, or a throw-in matching a rank already on the table).</summary>
        Attack = 0,
        /// <summary>Defender beats one of the unbeaten attacking cards.</summary>
        Defend = 1,
        /// <summary>Defender gives up and picks up all cards on the table.</summary>
        Take = 2,
        /// <summary>Attacker ends the bout (Бито) once every attack is beaten.</summary>
        Done = 3
    }

    /// <summary>A Durak move.</summary>
    public readonly struct DurakMove
    {
        /// <summary>What kind of move.</summary>
        public DurakMoveKind Kind { get; }
        /// <summary>The card played (for <see cref="DurakMoveKind.Attack"/>/<see cref="DurakMoveKind.Defend"/>).</summary>
        public Card Card { get; }
        /// <summary>Which attacking card to beat, an index into the table (for <see cref="DurakMoveKind.Defend"/>).</summary>
        public int DefendIndex { get; }

        private DurakMove(DurakMoveKind kind, Card card, int index) { Kind = kind; Card = card; DefendIndex = index; }

        /// <summary>An attacking play.</summary>
        public static DurakMove Attack(Card card) => new DurakMove(DurakMoveKind.Attack, card, -1);
        /// <summary>A defending play against attack <paramref name="index"/>.</summary>
        public static DurakMove Defend(int index, Card card) => new DurakMove(DurakMoveKind.Defend, card, index);
        /// <summary>Pick up the table.</summary>
        public static DurakMove Take() => new DurakMove(DurakMoveKind.Take, default, -1);
        /// <summary>End the bout (Бито).</summary>
        public static DurakMove Done() => new DurakMove(DurakMoveKind.Done, default, -1);

        /// <inheritdoc/>
        public override string ToString() => Kind switch
        {
            DurakMoveKind.Attack => $"Attack {Card}",
            DurakMoveKind.Defend => $"Defend #{DefendIndex} with {Card}",
            DurakMoveKind.Take => "Take",
            DurakMoveKind.Done => "Done (Бито)",
            _ => Kind.ToString(),
        };
    }

    /// <summary>The full authoritative Durak state. Clients never see this — only their <see cref="DurakView"/>.</summary>
    public sealed class DurakState
    {
        /// <summary>Player ids by seat.</summary>
        public string[] Players = Array.Empty<string>();
        /// <summary>Number of seats.</summary>
        public int Seats;
        /// <summary>The trump suit.</summary>
        public Suit Trump;
        /// <summary>The trump card (the deck's bottom card, drawn last).</summary>
        public Card TrumpCard;
        /// <summary>The draw pile; index 0 is the top (drawn first), the last element is <see cref="TrumpCard"/>.</summary>
        public List<Card> Deck = new List<Card>();
        /// <summary>Each seat's hand.</summary>
        public List<Card>[] Hands = Array.Empty<List<Card>>();
        /// <summary>The attacking seat.</summary>
        public int Attacker;
        /// <summary>The defending seat.</summary>
        public int Defender;
        /// <summary>Attacking cards currently on the table.</summary>
        public List<Card> Attacks = new List<Card>();
        /// <summary>Beating cards parallel to <see cref="Attacks"/>; null = not yet beaten.</summary>
        public List<Card?> Defenses = new List<Card?>();
        /// <summary>True once the defender has said "take": the attackers may still throw in matching cards before the pickup.</summary>
        public bool Taking;
        /// <summary>Seat that picked up the cards at the MOST RECENT bout end, or -1 if that bout was a clean beat (Бито).
        /// Purely informational (drives the client's "cards fly to the taker" animation); set in EndBout.</summary>
        public int LastTaker = -1;
        /// <summary>Per-seat "I'm done adding to this bout" flag (podkidnoy). Reset whenever a new card is thrown in.</summary>
        public bool[] Passed = Array.Empty<bool>();
        /// <summary>Seats that are out of the game (no cards, empty deck).</summary>
        public bool[] Finished = Array.Empty<bool>();
        /// <summary>The outcome, or null while playing.</summary>
        public GameOutcome? Result;

        /// <summary>Deep-copies the state (moves are applied to a clone).</summary>
        public DurakState Clone() => new DurakState
        {
            Players = Players,                          // immutable after Start
            Seats = Seats,
            Trump = Trump,
            TrumpCard = TrumpCard,
            Deck = new List<Card>(Deck),
            Hands = Hands.Select(h => new List<Card>(h)).ToArray(),
            Attacker = Attacker,
            Defender = Defender,
            Attacks = new List<Card>(Attacks),
            Defenses = new List<Card?>(Defenses),
            Taking = Taking,
            LastTaker = LastTaker,
            Passed = (bool[])Passed.Clone(),
            Finished = (bool[])Finished.Clone(),
            Result = Result,
        };
    }

    /// <summary>One player's redacted view: their own hand in full, everyone else only by count.</summary>
    public sealed class DurakView
    {
        /// <summary>The viewing seat.</summary>
        public int MySeat;
        /// <summary>The viewer's own hand (sorted).</summary>
        public IReadOnlyList<Card> Hand = Array.Empty<Card>();
        /// <summary>The trump suit.</summary>
        public Suit Trump;
        /// <summary>The trump card at the bottom of the deck (visible to all), or null once drawn.</summary>
        public Card? TrumpCard;
        /// <summary>Cards left in the draw pile.</summary>
        public int DeckCount;
        /// <summary>Card counts per seat (hidden hands).</summary>
        public int[] HandCounts = Array.Empty<int>();
        /// <summary>Attacking cards on the table.</summary>
        public IReadOnlyList<Card> Attacks = Array.Empty<Card>();
        /// <summary>Beating cards (null = unbeaten).</summary>
        public IReadOnlyList<Card?> Defenses = Array.Empty<Card?>();
        /// <summary>Attacking seat.</summary>
        public int Attacker;
        /// <summary>Defending seat.</summary>
        public int Defender;
        /// <summary>True while the defender is taking — the attacker may still throw in matching cards.</summary>
        public bool Taking;
        /// <summary>The seat to move (-1 if over).</summary>
        public int ToMove;
        /// <summary>Player ids by seat.</summary>
        public string[] Players = Array.Empty<string>();
        /// <summary>The outcome, or null.</summary>
        public GameOutcome? Result;
    }

    /// <summary>
    /// A complete, deterministic engine for <b>Durak (Подкидной дурак)</b>, implementing the server-authoritative
    /// <see cref="ITurnGame{TState,TMove,TView}"/> contract with per-player hidden hands. 36-card deck, trump from the
    /// bottom card, deal 6, attack/defend/throw-in/take/Бито, refill to 6, last player holding cards is the durak.
    /// <para><b>Simplifications (documented):</b> throw-ins are by the attacker only (no other players piling on), and
    /// the bout is sequential (attacker adds a card only once the table is fully beaten). "Perevodnoy" (bouncing) is not
    /// implemented. These are rule-legal and keep the engine deterministic and easy to reason about.</para>
    /// </summary>
    public sealed class DurakGame : ITurnGame<DurakState, DurakMove, DurakView>
    {
        private const int HandSize = 6;
        private const int MaxTable = 6;

        /// <inheritdoc/>
        public int Seats { get; }

        /// <summary>The deck size in cards: 36 (ranks 6–A, the classic variant) or 52 (ranks 2–A).</summary>
        public int DeckSize { get; }

        /// <summary>Creates a Durak game for 2..6 players (default 2) with a 36- or 52-card deck (default 36).</summary>
        public DurakGame(int seats = 2, int deckSize = 36)
        {
            if (seats < 2 || seats > 6) throw new ArgumentOutOfRangeException(nameof(seats), "Durak is for 2..6 players");
            if (deckSize != 36 && deckSize != 52) throw new ArgumentOutOfRangeException(nameof(deckSize), "Durak deck must be 36 or 52 cards");
            Seats = seats;
            DeckSize = deckSize;
        }

        /// <summary>True if <paramref name="defense"/> beats <paramref name="attack"/> under <paramref name="trump"/>.</summary>
        public static bool Beats(Card attack, Card defense, Suit trump)
        {
            if (defense.Suit == attack.Suit) return defense.Rank > attack.Rank;
            if (defense.Suit == trump) return attack.Suit != trump;   // trump beats any non-trump
            return false;
        }

        /// <inheritdoc/>
        public DurakState Start(IReadOnlyList<string> players, int seed)
        {
            if (players == null) throw new ArgumentNullException(nameof(players));
            if (players.Count != Seats) throw new GameException($"Durak needs {Seats} players, got {players.Count}");

            var deck = DeckSize == 52 ? Decks.Standard52() : Decks.Standard36();
            Decks.Shuffle(deck, new Random(seed));

            var s = new DurakState
            {
                Players = players.ToArray(),
                Seats = Seats,
                Deck = deck,
                Hands = new List<Card>[Seats],
                Finished = new bool[Seats],
                Passed = new bool[Seats],
                Trump = deck[deck.Count - 1].Suit,      // bottom card sets the trump
                TrumpCard = deck[deck.Count - 1],
            };
            for (var i = 0; i < Seats; i++) s.Hands[i] = new List<Card>();

            // Deal 6 each from the top (front).
            for (var round = 0; round < HandSize; round++)
                for (var seat = 0; seat < Seats; seat++)
                    Draw(s, seat);

            s.Attacker = LowestTrumpHolder(s);
            s.Defender = NextActive(s, s.Attacker);
            return s;
        }

        private static void Draw(DurakState s, int seat)
        {
            if (s.Deck.Count == 0) return;
            var c = s.Deck[0];
            s.Deck.RemoveAt(0);
            s.Hands[seat].Add(c);
        }

        private int LowestTrumpHolder(DurakState s)
        {
            var bestSeat = 0;
            Rank best = (Rank)255;
            var found = false;
            for (var seat = 0; seat < Seats; seat++)
                foreach (var c in s.Hands[seat])
                    if (c.Suit == s.Trump && (!found || c.Rank < best))
                    {
                        found = true; best = c.Rank; bestSeat = seat;
                    }
            return bestSeat;
        }

        private int NextActive(DurakState s, int from)
        {
            for (var step = 1; step <= Seats; step++)
            {
                var seat = (from + step) % Seats;
                if (!s.Finished[seat]) return seat;
            }
            return from;
        }

        private static bool AllBeaten(DurakState s)
        {
            for (var i = 0; i < s.Defenses.Count; i++) if (s.Defenses[i] == null) return false;
            return true;
        }

        // Attacks still awaiting a defence (a defender needs one card per unbeaten attack to beat them all).
        private static int UnbeatenCount(DurakState s)
        {
            var n = 0;
            for (var i = 0; i < s.Defenses.Count; i++) if (s.Defenses[i] == null) n++;
            return n;
        }

        // A throw-in is only allowed while the DEFENDER still has enough cards to beat everything unbeaten — you can't
        // pile on more cards than the defender holds (else they're forced to Take through no fault of their own). During
        // a Take the defender is picking everything up anyway, so this cap doesn't apply.
        private static bool DefenderCanTakeAnotherAttack(DurakState s)
            => s.Taking || UnbeatenCount(s) < s.Hands[s.Defender].Count;

        /// <inheritdoc/>
        // A single "primary" mover for turn-based consumers (bots): the defender must respond to unbeaten cards;
        // otherwise it is the first attacker (in turn order from the primary attacker) who still has a legal move.
        // Real multiplayer play is NOT limited to this seat — any seat with a legal move may act (see LegalMoves/Apply).
        public int CurrentSeat(DurakState state)
        {
            if (state.Result != null) return -1;
            if (!state.Taking && !AllBeaten(state) && LegalMoves(state, state.Defender).Count > 0) return state.Defender;
            for (var step = 0; step < state.Seats; step++)
            {
                var seat = (state.Attacker + step) % state.Seats;
                if (LegalMoves(state, seat).Count > 0) return seat;
            }
            return -1;
        }

        // Is this seat an attacker (anyone who isn't the defender and hasn't finished)?
        private static bool IsAttacker(DurakState s, int seat) => seat != s.Defender && !s.Finished[seat];

        // Read the pass flag defensively — a hand-built state (tests) may not have sized the Passed array.
        private static bool HasPassed(DurakState s, int seat) => seat < s.Passed.Length && s.Passed[seat];

        // Can THIS seat still throw a matching-rank card in (room on the table + a matching card in hand)?
        private bool SeatCanThrowIn(DurakState s, int seat)
        {
            if (!IsAttacker(s, seat) || HasPassed(s, seat)) return false;
            if (s.Attacks.Count == 0 || s.Attacks.Count >= MaxTable) return false;
            if (!DefenderCanTakeAnotherAttack(s)) return false; // can't pile on more cards than the defender can beat
            var ranks = TableRanks(s);
            return s.Hands[seat].Any(c => ranks.Contains(c.Rank));
        }

        // Any attacker at all who can still throw a matching card in.
        private bool AnyAttackerCanThrowIn(DurakState s)
        {
            for (var seat = 0; seat < s.Seats; seat++)
                if (SeatCanThrowIn(s, seat)) return true;
            return false;
        }

        // The bout is "closed" (podkidnoy over) only when every attacker has EXPLICITLY passed ("I'm done adding" —
        // the Skip/Бито button). It is deliberately NOT auto-closed just because a player has no matching card to throw:
        // the turn ends when everyone confirms, so a player who can't add anything still taps Skip to end their turn.
        private bool AllAttackersDone(DurakState s)
        {
            for (var seat = 0; seat < s.Seats; seat++)
                if (IsAttacker(s, seat) && !HasPassed(s, seat)) return false;
            return true;
        }

        private static void ResetPasses(DurakState s)
        {
            for (var i = 0; i < s.Passed.Length; i++) s.Passed[i] = false;
        }

        // Ranks currently present on the table (attacks + defenses) — throw-ins must match one of these.
        private static HashSet<Rank> TableRanks(DurakState s)
        {
            var set = new HashSet<Rank>();
            foreach (var c in s.Attacks) set.Add(c.Rank);
            foreach (var c in s.Defenses) if (c != null) set.Add(c.Value.Rank);
            return set;
        }

        /// <inheritdoc/>
        // NOTE: attacker and defender can BOTH have legal moves at the same moment (the attacker piles on matching
        // cards while the defender is still deciding / taking). The bout is no longer strictly one-at-a-time.
        public IReadOnlyList<DurakMove> LegalMoves(DurakState state, int seat)
        {
            var moves = new List<DurakMove>();
            if (state.Result != null) return moves;

            // Defender: beat unbeaten attacks or take (only while not already taking).
            if (seat == state.Defender && !state.Taking && !AllBeaten(state))
            {
                var hand = state.Hands[state.Defender];
                for (var i = 0; i < state.Attacks.Count; i++)
                {
                    if (state.Defenses[i] != null) continue;
                    foreach (var c in hand)
                        if (Beats(state.Attacks[i], c, state.Trump))
                            moves.Add(DurakMove.Defend(i, c));
                }
                moves.Add(DurakMove.Take());
            }

            // Attackers (ANY seat that isn't the defender): the primary attacker opens; then anyone may throw in a
            // matching-rank card (podkidnoy) or pass. The bout closes once every attacker has passed / has nothing left.
            if (IsAttacker(state, seat))
            {
                if (state.Attacks.Count == 0)
                {
                    if (seat == state.Attacker && !state.Taking)
                        foreach (var c in state.Hands[seat]) moves.Add(DurakMove.Attack(c));   // opening: any card
                }
                else if (!HasPassed(state, seat))
                {
                    if (SeatCanThrowIn(state, seat))
                    {
                        var ranks = TableRanks(state);
                        foreach (var c in state.Hands[seat])
                            if (ranks.Contains(c.Rank)) moves.Add(DurakMove.Attack(c));        // throw-in / pile on / on take
                    }
                    moves.Add(DurakMove.Done());   // "I'm done adding" (pass / Бито / finish the take)
                }
            }
            return moves;
        }

        /// <inheritdoc/>
        // Validates the move against the SUBMITTING seat's role (attacker vs defender), not a single "current" seat,
        // so the attacker can throw in while it is nominally the defender's turn.
        public DurakState Apply(DurakState state, int seat, DurakMove move)
        {
            if (state.Result != null) throw new GameException("game is over");
            var s = state.Clone();
            if (s.Passed.Length != s.Seats) s.Passed = new bool[s.Seats]; // tolerate a hand-built state with no pass array

            switch (move.Kind)
            {
                case DurakMoveKind.Attack:
                    if (!IsAttacker(s, seat)) throw new GameException("only an attacker plays attacking cards");
                    if (s.Attacks.Count == 0 && seat != s.Attacker) throw new GameException("only the primary attacker opens the bout");
                    if (s.Passed[seat]) throw new GameException("you already passed this bout");
                    ApplyAttack(s, seat, move.Card);
                    ResetPasses(s);   // a new card is on the table → every attacker gets to react again
                    break;
                case DurakMoveKind.Defend:
                    if (seat != s.Defender) throw new GameException("only the defender beats cards");
                    if (s.Taking) throw new GameException("the defender is already taking");
                    ApplyDefend(s, move.DefendIndex, move.Card);
                    break;
                case DurakMoveKind.Take:
                    if (seat != s.Defender) throw new GameException("only the defender can take");
                    if (s.Taking) throw new GameException("already taking");
                    if (s.Attacks.Count == 0) throw new GameException("nothing to take");
                    // Open a throw-in window if ANY attacker can still add a matching card; otherwise resolve now.
                    if (AnyAttackerCanThrowIn(s)) s.Taking = true;
                    else EndBout(s, defenderTook: true);
                    break;
                case DurakMoveKind.Done:   // "I'm done adding to this bout" (pass / Бито / finish the take)
                    if (!IsAttacker(s, seat)) throw new GameException("only an attacker can pass");
                    if (s.Attacks.Count == 0) throw new GameException("nothing to pass on yet");
                    if (s.Passed[seat]) throw new GameException("you already passed");
                    s.Passed[seat] = true;
                    break;
                default:
                    throw new GameException("unknown move");
            }

            // Close the bout automatically once EVERY attacker is done adding and the table is resolved:
            // all attacks beaten → Бито (successful defence), or the defender is taking → they pick everything up.
            if (s.Result == null && AllAttackersDone(s))
            {
                if (s.Taking) EndBout(s, defenderTook: true);
                else if (s.Attacks.Count > 0 && AllBeaten(s)) EndBout(s, defenderTook: false);
            }
            return s;
        }

        private void ApplyAttack(DurakState s, int seat, Card card)
        {
            if (s.Attacks.Count >= MaxTable) throw new GameException("table is full");
            if (s.Attacks.Count == 0)
            {
                if (s.Taking) throw new GameException("cannot open an attack now");
                // opening attack: any card
            }
            else
            {
                // throw-in (pile on while unbeaten / all-beaten / on take): must match a rank already on the table
                if (!DefenderCanTakeAnotherAttack(s)) throw new GameException("cannot throw in more cards than the defender can beat");
                if (!TableRanks(s).Contains(card.Rank)) throw new GameException("throw-in must match a rank on the table");
            }
            if (!s.Hands[seat].Remove(card)) throw new GameException("you don't hold that card");
            s.Attacks.Add(card);
            s.Defenses.Add(null);
        }

        private void ApplyDefend(DurakState s, int index, Card card)
        {
            if (index < 0 || index >= s.Attacks.Count) throw new GameException("no such attack");
            if (s.Defenses[index] != null) throw new GameException("that attack is already beaten");
            if (!Beats(s.Attacks[index], card, s.Trump)) throw new GameException($"{card} does not beat {s.Attacks[index]}");
            if (!s.Hands[s.Defender].Remove(card)) throw new GameException("defender doesn't hold that card");
            s.Defenses[index] = card;
        }

        private void EndBout(DurakState s, bool defenderTook)
        {
            s.LastTaker = defenderTook ? s.Defender : -1; // capture the taker BEFORE roles rotate (for the client animation)
            if (defenderTook)
            {
                // Defender picks up everything on the table.
                foreach (var c in s.Attacks) s.Hands[s.Defender].Add(c);
                foreach (var c in s.Defenses) if (c != null) s.Hands[s.Defender].Add(c.Value);
            }
            // else: beaten cards are discarded (leave the game).
            s.Attacks = new List<Card>();
            s.Defenses = new List<Card?>();
            s.Taking = false;
            ResetPasses(s);   // fresh bout: nobody has passed yet

            Refill(s);

            // Mark players who ran out with an empty deck.
            for (var i = 0; i < s.Seats; i++)
                if (!s.Finished[i] && s.Hands[i].Count == 0 && s.Deck.Count == 0)
                    s.Finished[i] = true;

            var active = Enumerable.Range(0, s.Seats).Where(i => !s.Finished[i]).ToList();
            if (active.Count <= 1)
            {
                var losers = active;   // the last player holding cards is the durak
                var winners = Enumerable.Range(0, s.Seats).Where(i => !losers.Contains(i)).ToList();
                s.Result = active.Count == 0
                    ? new GameOutcome(Enumerable.Range(0, s.Seats).ToList(), Array.Empty<int>(), "draw")
                    : new GameOutcome(winners, losers);
                return;
            }

            // Assign next roles among active players.
            if (defenderTook)
            {
                // Defender was overwhelmed → the seat AFTER the defender attacks; the defender is skipped this round.
                s.Attacker = NextActive(s, s.Defender);
                s.Defender = NextActive(s, s.Attacker);
            }
            else
            {
                // Successful defence → the defender becomes the new attacker.
                s.Attacker = s.Finished[s.Defender] ? NextActive(s, s.Defender) : s.Defender;
                s.Defender = NextActive(s, s.Attacker);
            }
        }

        private void Refill(DurakState s)
        {
            // Refill order: attacker first, then the rest in turn order, defender last.
            var order = new List<int>();
            for (var step = 0; step < s.Seats; step++)
            {
                var seat = (s.Attacker + step) % s.Seats;
                if (seat == s.Defender) continue;
                if (!s.Finished[seat]) order.Add(seat);
            }
            if (!s.Finished[s.Defender]) order.Add(s.Defender);

            foreach (var seat in order)
                while (s.Hands[seat].Count < HandSize && s.Deck.Count > 0)
                    Draw(s, seat);
        }

        /// <inheritdoc/>
        public DurakView View(DurakState s, int seat)
        {
            var hand = new List<Card>(s.Hands[seat]);
            hand.Sort((a, b) =>
            {
                // trumps last, then by suit, then by rank — just for a tidy display
                var at = a.Suit == s.Trump ? 1 : 0;
                var bt = b.Suit == s.Trump ? 1 : 0;
                if (at != bt) return at - bt;
                if (a.Suit != b.Suit) return a.Suit - b.Suit;
                return a.Rank - b.Rank;
            });

            return new DurakView
            {
                MySeat = seat,
                Hand = hand,
                Trump = s.Trump,
                TrumpCard = s.Deck.Count > 0 ? s.TrumpCard : (Card?)null,
                DeckCount = s.Deck.Count,
                HandCounts = s.Hands.Select(h => h.Count).ToArray(),
                Attacks = new List<Card>(s.Attacks),
                Defenses = new List<Card?>(s.Defenses),
                Attacker = s.Attacker,
                Defender = s.Defender,
                Taking = s.Taking,
                ToMove = CurrentSeat(s),
                Players = s.Players,
                Result = s.Result,
            };
        }

        /// <inheritdoc/>
        public GameOutcome? Outcome(DurakState state) => state.Result;
    }
}
