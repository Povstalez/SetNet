namespace Matchmaking.Shared;

/// <summary>
/// Shared constants for the matchmaking demo. The matchmaking DTOs (<c>MatchRequest</c>, <c>MatchResult</c>) live in
/// the <c>SetNet.Matchmaking</c> module itself, so the only thing both ends need to agree on here is the queue name.
/// </summary>
public static class MatchmakingDemo
{
    /// <summary>The queue clients enter — only players in the same queue match together.</summary>
    public const string Queue = "ranked";
}
