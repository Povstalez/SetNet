// SetNet test harness — runs in-process transport scenarios by name.
//   dotnet run --project SetNet.Tests -- tcp     (default)
//   dotnet run --project SetNet.Tests -- udp
//   dotnet run --project SetNet.Tests -- both

using SetNet.Core.Transport;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Tests;

// The core library bundles no serializer; register the MessagePack adapter once at startup.
SetNetSerializer.Use(new MessagePackNetSerializer());

var scenario = args.Length > 0 ? args[0].ToLowerInvariant() : "tcp";

switch (scenario)
{
    case "frag":
        Scenarios.RunFragmentationTest();
        break;
    case "tls":
        await Scenarios.RunTlsEchoAsync();
        break;
    case "bench":
        await Bench.RunAsync();
        break;
    case "tcp":
        await Scenarios.RunTcpEchoAsync();
        break;
    case "udp":
        await Scenarios.RunUdpEchoAsync(DeliveryMethod.Unreliable);
        await Scenarios.RunUdpEchoAsync(DeliveryMethod.Reliable);
        break;
    case "loss":
        await Scenarios.RunUdpLossAsync();
        break;
    case "both":
        await Scenarios.RunBothAsync();
        break;
    case "idle":
        await Scenarios.RunBothIdleAsync();
        break;
    case "deadlock":
        await Scenarios.RunDeadlockAsync();
        break;
    default:
        Console.WriteLine($"Unknown scenario '{scenario}'. Available: tcp, udp, loss, both, idle, deadlock");
        break;
}
