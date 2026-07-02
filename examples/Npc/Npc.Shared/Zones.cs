namespace Npc.Shared;

/// <summary>The one zone both ends agree on. NPC payloads are opaque bytes, so there are no shared DTOs to declare —
/// this constant is all the client and server need in common.</summary>
public static class Zones
{
    public const string Town = "town";
}
