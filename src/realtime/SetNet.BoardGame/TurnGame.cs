using System;
using System.Collections.Generic;

namespace SetNet.BoardGame
{
    /// <summary>Thrown when an illegal move is attempted.</summary>
    public sealed class GameException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public GameException(string message) : base(message) { }
    }

    /// <summary>The result of a finished game: who won and who lost (seat indices).</summary>
    public sealed class GameOutcome
    {
        /// <summary>Seats that won.</summary>
        public IReadOnlyList<int> Winners { get; }
        /// <summary>Seats that lost (in Durak, the single "durak").</summary>
        public IReadOnlyList<int> Losers { get; }
        /// <summary>Optional note (e.g. "draw").</summary>
        public string? Note { get; }

        /// <summary>Creates an outcome.</summary>
        public GameOutcome(IReadOnlyList<int> winners, IReadOnlyList<int> losers, string? note = null)
        {
            Winners = winners; Losers = losers; Note = note;
        }
    }

    /// <summary>
    /// The server-authoritative contract for a turn-based game. The framework never trusts a client: it asks the game to
    /// <see cref="LegalMoves"/> / <see cref="Apply"/> (validate + advance) and to produce a <b>per-player redacted
    /// <see cref="View"/></b> — so hidden information (a player's hand) is only ever revealed to its owner. State is
    /// treated as immutable: <see cref="Apply"/> returns a new state.
    /// </summary>
    /// <typeparam name="TState">The full (authoritative) game state.</typeparam>
    /// <typeparam name="TMove">A move a player can make.</typeparam>
    /// <typeparam name="TView">One player's redacted view of the state.</typeparam>
    public interface ITurnGame<TState, TMove, TView>
    {
        /// <summary>How many seats (players) the game needs.</summary>
        int Seats { get; }

        /// <summary>Creates the initial state for the given players (in seat order), using <paramref name="seed"/> for shuffling.</summary>
        TState Start(IReadOnlyList<string> players, int seed);

        /// <summary>The seat whose turn it is, or -1 if the game is over.</summary>
        int CurrentSeat(TState state);

        /// <summary>Every legal move for <paramref name="seat"/> right now (empty if it isn't their turn).</summary>
        IReadOnlyList<TMove> LegalMoves(TState state, int seat);

        /// <summary>Validates and applies a move, returning the new state; throws <see cref="GameException"/> if illegal.</summary>
        TState Apply(TState state, int seat, TMove move);

        /// <summary>The redacted view of the state for <paramref name="seat"/> (hides other players' hidden info).</summary>
        TView View(TState state, int seat);

        /// <summary>The outcome if the game is finished, else null.</summary>
        GameOutcome? Outcome(TState state);
    }

    /// <summary>
    /// A headless match runner over an <see cref="ITurnGame{TState,TMove,TView}"/>: holds the current state, exposes the
    /// per-seat view + whose turn it is, and applies moves. Used by bots/tests and by a networked hub. Not thread-safe;
    /// drive it from one place.
    /// </summary>
    public sealed class TurnGameHost<TState, TMove, TView>
    {
        private readonly ITurnGame<TState, TMove, TView> _game;

        /// <summary>The current authoritative state.</summary>
        public TState State { get; private set; }

        /// <summary>Raised after every applied move (the mover's seat).</summary>
        public event Action<int, TMove>? Moved;
        /// <summary>Raised once when the game finishes.</summary>
        public event Action<GameOutcome>? Finished;

        /// <summary>Starts a match for the given players.</summary>
        public TurnGameHost(ITurnGame<TState, TMove, TView> game, IReadOnlyList<string> players, int seed)
        {
            _game = game ?? throw new ArgumentNullException(nameof(game));
            State = _game.Start(players, seed);
        }

        /// <summary>The seat to move, or -1 if the game is over.</summary>
        public int CurrentSeat => _game.CurrentSeat(State);
        /// <summary>The outcome, or null if still playing.</summary>
        public GameOutcome? Outcome => _game.Outcome(State);
        /// <summary>Whether the game has finished.</summary>
        public bool IsOver => Outcome != null;

        /// <summary>The legal moves for a seat right now.</summary>
        public IReadOnlyList<TMove> LegalMoves(int seat) => _game.LegalMoves(State, seat);
        /// <summary>The redacted view for a seat.</summary>
        public TView ViewFor(int seat) => _game.View(State, seat);

        /// <summary>Applies a move by a seat (validated by the game); raises <see cref="Moved"/> and, if it ends, <see cref="Finished"/>.</summary>
        public void Move(int seat, TMove move)
        {
            State = _game.Apply(State, seat, move);
            Moved?.Invoke(seat, move);
            var outcome = _game.Outcome(State);
            if (outcome != null) Finished?.Invoke(outcome);
        }
    }
}
