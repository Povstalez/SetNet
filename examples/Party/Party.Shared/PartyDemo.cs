namespace Party.Shared;

/// <summary>
/// Shared constants for the party demo. The party DTOs (<c>PartyInfo</c>, <c>PartyMember</c>) live in the
/// <c>SetNet.Party</c> module itself, so there is nothing to serialize across the wire here — this marker just
/// gives both the server and client a common project to reference.
/// </summary>
public static class PartyDemo
{
    /// <summary>Default TCP port the demo server listens on.</summary>
    public const int DefaultPort = 5301;
}
