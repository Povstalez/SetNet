# SetNet.StateSync.LagCompensation

**Server-side lag compensation for [SetNet.StateSync](https://www.nuget.org/packages/SetNet.StateSync).**

Records a short history of every entity's position each tick, then lets you **rewind** the world to where things were at a past moment — so hit detection is fair even though each client sees the world delayed by its interpolation buffer + latency.

```csharp
var lag = new LagCompensator(e => e.GetVec3(0), historyMs: 1000);

// each server tick:
lag.Capture(world.Entities);

// when a client fires, rewind to what THEY saw (~interpolationDelay + RTT/2 ago):
var past = lag.PositionAgo(targetNetId, secondsAgo: 0.1 + rttSeconds / 2);
if (past.HasValue && RayHits(shot, past.Value)) ApplyDamage(...);
```

Positions are interpolated between recorded frames; history older than `historyMs` is dropped.

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
