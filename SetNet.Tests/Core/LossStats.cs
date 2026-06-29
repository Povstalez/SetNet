using System.Threading;

namespace SetNet.Tests;

/// <summary>Shared counters for the UDP loss / Both-mode routing verification scenarios.</summary>
public static class LossStats
{
    private static int _reliable;
    private static int _unreliable;

    public static int ReliableReceived => Volatile.Read(ref _reliable);
    public static int UnreliableReceived => Volatile.Read(ref _unreliable);
    public static int Received => ReliableReceived + UnreliableReceived;

    public static void Increment(bool reliable)
    {
        if (reliable) Interlocked.Increment(ref _reliable);
        else Interlocked.Increment(ref _unreliable);
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _reliable, 0);
        Interlocked.Exchange(ref _unreliable, 0);
    }
}
