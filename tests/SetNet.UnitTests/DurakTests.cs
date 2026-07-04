using System.Collections.Generic;
using System.Linq;
using SetNet.BoardGame;
using Xunit;

namespace SetNet.UnitTests
{
    /// <summary>Deterministic tests for the Durak engine (SetNet.BoardGame): dealing, beating rules, bout flow, hidden info, and a full greedy game.</summary>
    public class DurakTests
    {
        private static Card C(Rank r, Suit s) => new Card(r, s);

        [Fact]
        public void Deal_gives_six_each_and_sets_trump_from_the_bottom_card()
        {
            var g = new DurakGame(2);
            var s = g.Start(new[] { "a", "b" }, seed: 1);

            Assert.Equal(6, s.Hands[0].Count);
            Assert.Equal(6, s.Hands[1].Count);
            Assert.Equal(36 - 12, s.Deck.Count);
            Assert.Equal(s.TrumpCard.Suit, s.Trump);
            Assert.Equal(s.Deck[s.Deck.Count - 1], s.TrumpCard);   // trump card is the bottom of the draw pile
        }

        [Fact]
        public void Beats_follows_the_rules()
        {
            var t = Suit.Spades;
            Assert.True(DurakGame.Beats(C(Rank.Seven, Suit.Hearts), C(Rank.Eight, Suit.Hearts), t));   // higher, same suit
            Assert.False(DurakGame.Beats(C(Rank.Eight, Suit.Hearts), C(Rank.Seven, Suit.Hearts), t));  // lower, same suit
            Assert.True(DurakGame.Beats(C(Rank.Ace, Suit.Hearts), C(Rank.Six, Suit.Spades), t));       // trump beats non-trump
            Assert.False(DurakGame.Beats(C(Rank.Six, Suit.Spades), C(Rank.Ace, Suit.Hearts), t));      // non-trump can't beat trump
            Assert.True(DurakGame.Beats(C(Rank.Six, Suit.Spades), C(Rank.Seven, Suit.Spades), t));     // higher trump
            Assert.False(DurakGame.Beats(C(Rank.Seven, Suit.Hearts), C(Rank.Eight, Suit.Clubs), t));   // different non-trump suits
        }

        // Builds a controlled 2-player state (no deck, so no refill noise).
        private static DurakState Table(List<Card> a, List<Card> b, Suit trump = Suit.Spades) => new DurakState
        {
            Players = new[] { "a", "b" },
            Seats = 2,
            Trump = trump,
            TrumpCard = new Card(Rank.Six, trump),
            Deck = new List<Card>(),
            Hands = new[] { a, b },
            Attacks = new List<Card>(),
            Defenses = new List<Card?>(),
            Finished = new bool[2],
            Attacker = 0,
            Defender = 1,
        };

        [Fact]
        public void Attack_defend_done_rotates_roles()
        {
            var g = new DurakGame(2);
            var s = Table(
                new List<Card> { C(Rank.Seven, Suit.Hearts), C(Rank.Nine, Suit.Clubs) },
                new List<Card> { C(Rank.Eight, Suit.Hearts), C(Rank.Ten, Suit.Clubs) });

            s = g.Apply(s, 0, DurakMove.Attack(C(Rank.Seven, Suit.Hearts)));
            Assert.Equal(1, g.CurrentSeat(s));                                  // defender's turn

            s = g.Apply(s, 1, DurakMove.Defend(0, C(Rank.Eight, Suit.Hearts)));
            Assert.Equal(0, g.CurrentSeat(s));                                  // all beaten → attacker

            s = g.Apply(s, 0, DurakMove.Done());
            Assert.Null(g.Outcome(s));
            Assert.Equal(1, s.Attacker);                                        // defender became attacker
            Assert.Equal(0, s.Defender);
            Assert.Empty(s.Attacks);
            Assert.Single(s.Hands[0]);                                          // 7♥/8♥ discarded, one card left each
            Assert.Single(s.Hands[1]);
        }

        [Fact]
        public void Take_puts_the_table_into_the_defenders_hand_and_keeps_attacker()
        {
            var g = new DurakGame(2);
            var s = Table(
                new List<Card> { C(Rank.Ace, Suit.Spades), C(Rank.Nine, Suit.Clubs) },   // attacker
                new List<Card> { C(Rank.Ten, Suit.Clubs) });                             // defender can't beat A♠

            s = g.Apply(s, 0, DurakMove.Attack(C(Rank.Ace, Suit.Spades)));
            s = g.Apply(s, 1, DurakMove.Take());

            Assert.Equal(0, s.Attacker);                                        // attacker unchanged after a take
            Assert.Equal(1, s.Defender);
            Assert.Contains(C(Rank.Ace, Suit.Spades), s.Hands[1]);             // taken card is now in defender's hand
            Assert.Equal(2, s.Hands[1].Count);
            Assert.Empty(s.Attacks);
        }

        [Fact]
        public void Throw_in_must_match_a_rank_on_the_table_and_can_end_the_game()
        {
            var g = new DurakGame(2);
            var s = Table(
                new List<Card> { C(Rank.Seven, Suit.Hearts), C(Rank.Seven, Suit.Clubs), C(Rank.Nine, Suit.Clubs) },
                new List<Card> { C(Rank.Eight, Suit.Hearts), C(Rank.Eight, Suit.Clubs) });

            s = g.Apply(s, 0, DurakMove.Attack(C(Rank.Seven, Suit.Hearts)));
            s = g.Apply(s, 1, DurakMove.Defend(0, C(Rank.Eight, Suit.Hearts)));

            var atkMoves = g.LegalMoves(s, 0);
            Assert.Contains(atkMoves, m => m.Kind == DurakMoveKind.Attack && m.Card == C(Rank.Seven, Suit.Clubs)); // rank 7 on table
            Assert.DoesNotContain(atkMoves, m => m.Kind == DurakMoveKind.Attack && m.Card == C(Rank.Nine, Suit.Clubs)); // rank 9 not

            s = g.Apply(s, 0, DurakMove.Attack(C(Rank.Seven, Suit.Clubs)));
            s = g.Apply(s, 1, DurakMove.Defend(1, C(Rank.Eight, Suit.Clubs)));
            s = g.Apply(s, 0, DurakMove.Done());

            Assert.NotNull(g.Outcome(s));
            Assert.Contains(1, g.Outcome(s)!.Winners);                          // player 1 emptied their hand
            Assert.Contains(0, g.Outcome(s)!.Losers);                           // player 0 still holds 9♣ → durak
        }

        [Fact]
        public void View_reveals_only_the_viewers_hand()
        {
            var g = new DurakGame(2);
            var s = g.Start(new[] { "a", "b" }, seed: 3);

            var mine = g.View(s, 0);
            Assert.Equal(6, mine.Hand.Count);
            Assert.Equal(new[] { 6, 6 }, mine.HandCounts);                      // I see counts for everyone…
            Assert.Equal(new HashSet<Card>(s.Hands[0]), new HashSet<Card>(mine.Hand));   // …but only my own cards
            Assert.Equal(s.Trump, mine.Trump);
        }

        [Fact]
        public void Illegal_move_throws()
        {
            var g = new DurakGame(2);
            var s = g.Start(new[] { "a", "b" }, seed: 5);
            var defender = s.Defender;
            Assert.Throws<GameException>(() => g.Apply(s, defender, DurakMove.Take()));  // not the defender's turn yet
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(42)]
        [InlineData(1000)]
        public void A_full_greedy_game_terminates_with_a_result(int seed)
        {
            var g = new DurakGame(2);
            var s = g.Start(new[] { "a", "b" }, seed);

            var guard = 0;
            while (g.Outcome(s) == null && guard++ < 5000)
            {
                var seat = g.CurrentSeat(s);
                s = g.Apply(s, seat, Greedy(g, s, seat));
            }

            Assert.True(guard < 5000, "game did not terminate");
            Assert.NotNull(g.Outcome(s));
        }

        private static int Val(Card c, Suit trump) => (c.Suit == trump ? 100 : 0) + (int)c.Rank;

        // A greedy bot that always makes progress (attacker never throws in → the bout closes).
        private static DurakMove Greedy(DurakGame g, DurakState s, int seat)
        {
            var moves = g.LegalMoves(s, seat);
            var defend = moves.Where(m => m.Kind == DurakMoveKind.Defend).OrderBy(m => Val(m.Card, s.Trump)).ToList();
            if (defend.Count > 0) return defend[0];
            if (moves.Any(m => m.Kind == DurakMoveKind.Take)) return DurakMove.Take();
            if (moves.Any(m => m.Kind == DurakMoveKind.Done)) return DurakMove.Done();
            return moves.Where(m => m.Kind == DurakMoveKind.Attack).OrderBy(m => Val(m.Card, s.Trump)).First();
        }
    }
}
