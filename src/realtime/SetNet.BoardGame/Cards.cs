using System;
using System.Collections.Generic;

namespace SetNet.BoardGame
{
    /// <summary>The four suits. ♣ ♦ ♥ ♠.</summary>
    public enum Suit : byte
    {
        /// <summary>Clubs ♣.</summary>
        Clubs = 0,
        /// <summary>Diamonds ♦.</summary>
        Diamonds = 1,
        /// <summary>Hearts ♥.</summary>
        Hearts = 2,
        /// <summary>Spades ♠.</summary>
        Spades = 3
    }

    /// <summary>Card ranks. Values are the comparison order (Ace high). A 36-card deck uses Six..Ace.</summary>
    public enum Rank : byte
    {
        /// <summary>2.</summary>
        Two = 2,
        /// <summary>3.</summary>
        Three = 3,
        /// <summary>4.</summary>
        Four = 4,
        /// <summary>5.</summary>
        Five = 5,
        /// <summary>6.</summary>
        Six = 6,
        /// <summary>7.</summary>
        Seven = 7,
        /// <summary>8.</summary>
        Eight = 8,
        /// <summary>9.</summary>
        Nine = 9,
        /// <summary>10.</summary>
        Ten = 10,
        /// <summary>Jack.</summary>
        Jack = 11,
        /// <summary>Queen.</summary>
        Queen = 12,
        /// <summary>King.</summary>
        King = 13,
        /// <summary>Ace (high).</summary>
        Ace = 14
    }

    /// <summary>A playing card: a rank and a suit. Value type, equatable.</summary>
    public readonly struct Card : IEquatable<Card>
    {
        /// <summary>The rank.</summary>
        public Rank Rank { get; }
        /// <summary>The suit.</summary>
        public Suit Suit { get; }

        /// <summary>Creates a card.</summary>
        public Card(Rank rank, Suit suit) { Rank = rank; Suit = suit; }

        /// <inheritdoc/>
        public bool Equals(Card other) => Rank == other.Rank && Suit == other.Suit;
        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is Card c && Equals(c);
        /// <inheritdoc/>
        public override int GetHashCode() => ((int)Rank << 3) | (int)Suit;
        /// <summary>Equality.</summary>
        public static bool operator ==(Card a, Card b) => a.Equals(b);
        /// <summary>Inequality.</summary>
        public static bool operator !=(Card a, Card b) => !a.Equals(b);

        private static string RankText(Rank r) => r switch
        {
            Rank.Ten => "10",
            Rank.Jack => "J",
            Rank.Queen => "Q",
            Rank.King => "K",
            Rank.Ace => "A",
            _ => ((int)r).ToString(),
        };

        private static string SuitText(Suit s) => s switch
        {
            Suit.Clubs => "♣",
            Suit.Diamonds => "♦",
            Suit.Hearts => "♥",
            Suit.Spades => "♠",
            _ => "?",
        };

        /// <summary>A short human-readable label, e.g. <c>"10♥"</c>.</summary>
        public override string ToString() => RankText(Rank) + SuitText(Suit);
    }

    /// <summary>Deck helpers: build standard decks and shuffle them deterministically.</summary>
    public static class Decks
    {
        /// <summary>A fresh 36-card deck (Six..Ace × four suits) in a fixed order.</summary>
        public static List<Card> Standard36()
        {
            var d = new List<Card>(36);
            for (var s = 0; s < 4; s++)
                for (var r = (int)Rank.Six; r <= (int)Rank.Ace; r++)
                    d.Add(new Card((Rank)r, (Suit)s));
            return d;
        }

        /// <summary>A fresh 52-card deck (Two..Ace × four suits) in a fixed order.</summary>
        public static List<Card> Standard52()
        {
            var d = new List<Card>(52);
            for (var s = 0; s < 4; s++)
                for (var r = (int)Rank.Two; r <= (int)Rank.Ace; r++)
                    d.Add(new Card((Rank)r, (Suit)s));
            return d;
        }

        /// <summary>In-place Fisher–Yates shuffle with the given RNG (seed it for reproducible games/tests).</summary>
        public static void Shuffle(IList<Card> cards, Random rng)
        {
            if (cards == null) throw new ArgumentNullException(nameof(cards));
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            for (var i = cards.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (cards[i], cards[j]) = (cards[j], cards[i]);
            }
        }
    }
}
