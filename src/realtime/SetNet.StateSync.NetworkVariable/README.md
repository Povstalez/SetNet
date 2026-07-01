# SetNet.StateSync.NetworkVariable

**Typed, change-tracked variables for [SetNet.StateSync](https://www.nuget.org/packages/SetNet.StateSync).**

Instead of juggling field indices and polling `view.GetFloat(2)`, bind a `NetworkVariable<T>` to a replicated field and read `Value` or subscribe to `Changed`.

```csharp
var health = view.Watch<float>(2);
health.Changed += hp => UpdateHealthBar(hp);

// once per frame, after ClientReplication.Update():
health.Poll();          // raises Changed when the interpolated value changes
float now = health.Value;
```

Supports `float`, `double`, `int`, `long`, `bool`, `string`, `Vec2`, `Vec3`, `Quat`.

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
