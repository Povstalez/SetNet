# SetNet.Rooms.HostMigration

**Host migration for [SetNet.Rooms](https://www.nuget.org/packages/SetNet.Rooms).**

Designates the room's creator as **host** and, when the host leaves or disconnects, promotes the next remaining member and notifies the room — so a peer-authoritative session survives the host dropping.

```csharp
HostMigrationRuntime.Enable();   // startup, both ends

server.UseRooms();
server.UseHostMigration();

var hm = client.UseHostMigration();
hm.HostChanged += (roomCode, newHostId) =>
{
    bool iAmHost = newHostId == rooms.CurrentRoom?.OwnPlayerId;
    // take over authority if you're the new host
};
```

Uses the public `server.RoomHooks()` room-lifecycle events. Node-local, matching Rooms.

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
