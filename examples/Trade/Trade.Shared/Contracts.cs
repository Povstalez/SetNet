namespace Trade.Shared;

/// <summary>
/// Shared constants for the trade demo. The Trade and Inventory modules ship their own hand-framed wire protocols,
/// so this example needs no custom message DTOs — only the names/counts of the starter items the server grants on
/// connect, kept here so both ends agree on what there is to trade.
/// </summary>
public static class Starter
{
    /// <summary>Every player is granted these stacks on connect, so there is always something to put on the table.</summary>
    public static readonly (string ItemId, long Count)[] Kit =
    {
        ("gold", 100),
        ("sword", 3),
        ("potion", 10),
    };
}
