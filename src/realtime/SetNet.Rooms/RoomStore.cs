using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SetNet.Core;

namespace SetNet.Rooms
{
    /// <summary>
    /// A server-side room: a join code, a capacity, and its live member connections. Members are live peers on
    /// this node, so a room is node-local (a cross-node/cluster room store would coordinate metadata separately).
    /// </summary>
    public sealed class Room
    {
        /// <summary>The room's join code.</summary>
        public string Code { get; }

        /// <summary>Max players (0 = unlimited).</summary>
        public int MaxPlayers { get; }

        /// <summary>Member peers, keyed by peer id.</summary>
        internal ConcurrentDictionary<Guid, BasePeer> Members { get; } = new ConcurrentDictionary<Guid, BasePeer>();

        /// <summary>Current member count.</summary>
        public int Count => Members.Count;

        /// <summary>True if the room is at capacity.</summary>
        public bool IsFull => MaxPlayers > 0 && Members.Count >= MaxPlayers;

        /// <summary>Creates a room.</summary>
        public Room(string code, int maxPlayers)
        {
            Code = code;
            MaxPlayers = maxPlayers;
        }
    }

    /// <summary>
    /// Persistence/registry for rooms. The default is <see cref="MemoryRoomStore"/> (in-process). Methods are async
    /// so a backing store can do I/O. Implementations must be thread-safe.
    /// </summary>
    public interface IRoomStore
    {
        /// <summary>Creates a new room with a unique join code.</summary>
        Task<Room> CreateAsync(int maxPlayers);

        /// <summary>Finds a room by code, or null if it doesn't exist.</summary>
        Task<Room?> GetAsync(string code);

        /// <summary>Removes a room (e.g. when it becomes empty).</summary>
        Task RemoveAsync(Room room);
    }

    /// <summary>Default in-process room registry, keyed by join code.</summary>
    public sealed class MemoryRoomStore : IRoomStore
    {
        // Unambiguous alphabet (no 0/O/1/I) for human-friendly join codes.
        private static readonly char[] Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

        private readonly ConcurrentDictionary<string, Room> _rooms = new ConcurrentDictionary<string, Room>();

        /// <inheritdoc/>
        public Task<Room> CreateAsync(int maxPlayers)
        {
            while (true)
            {
                var room = new Room(GenerateCode(), maxPlayers);
                if (_rooms.TryAdd(room.Code, room))
                    return Task.FromResult(room);
                // extremely rare collision — retry with a new code
            }
        }

        /// <inheritdoc/>
        public Task<Room?> GetAsync(string code)
            => Task.FromResult(_rooms.TryGetValue(code ?? "", out var room) ? room : null);

        /// <inheritdoc/>
        public Task RemoveAsync(Room room)
        {
            _rooms.TryRemove(room.Code, out _);
            return Task.CompletedTask;
        }

        private static string GenerateCode()
        {
            var bytes = Guid.NewGuid().ToByteArray();
            var chars = new char[6];
            for (var i = 0; i < 6; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
            return new string(chars);
        }
    }
}
