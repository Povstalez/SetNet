using Xunit;

// The tick scheduler exposes an ambient, process-global TickHost.Current that the game-loop systems auto-subscribe to.
// Running test collections in parallel would let one test's TickHost.Current bleed into another's system construction,
// so the whole assembly runs serially. (Many collections here also use real sockets, which serialize better anyway.)
[assembly: CollectionBehavior(DisableTestParallelization = true)]
