using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Inventory;

namespace SetNet.Crafting
{
    /// <summary>Reserved wire types for the crafting service. Don't reuse these ids for application messages.</summary>
    public static class CraftingTypes
    {
        /// <summary>Client → server: craft/list command.</summary>
        public const ushort Command = ushort.MaxValue - 67;   // 65468

        /// <summary>Server → client: correlated reply.</summary>
        public const ushort Reply = ushort.MaxValue - 68;     // 65467
    }

    internal enum CraftOp : byte { Craft = 0, List = 1 }

    /// <summary>Thrown when crafting fails (unknown recipe, missing ingredients, timeout).</summary>
    public sealed class CraftingException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public CraftingException(string message) : base(message) { }
    }

    /// <summary>An item quantity used as a crafting input or output.</summary>
    public sealed class ItemAmount
    {
        /// <summary>The item id.</summary>
        public string ItemId { get; set; } = "";

        /// <summary>The quantity.</summary>
        public long Count { get; set; }

        /// <summary>Creates an empty amount (for serialization).</summary>
        public ItemAmount() { }

        /// <summary>Creates <paramref name="count"/> × <paramref name="itemId"/>.</summary>
        public ItemAmount(string itemId, long count) { ItemId = itemId; Count = count; }
    }

    /// <summary>A crafting recipe: consume the inputs, produce the outputs.</summary>
    public sealed class Recipe
    {
        /// <summary>Unique recipe id.</summary>
        public string Id { get; set; } = "";

        /// <summary>Items consumed per craft.</summary>
        public List<ItemAmount> Inputs { get; set; } = new List<ItemAmount>();

        /// <summary>Items produced per craft.</summary>
        public List<ItemAmount> Outputs { get; set; } = new List<ItemAmount>();

        /// <summary>Creates an empty recipe.</summary>
        public Recipe() { }

        /// <summary>Creates a recipe with the given id, inputs and outputs.</summary>
        public Recipe(string id, IEnumerable<ItemAmount> inputs, IEnumerable<ItemAmount> outputs)
        {
            Id = id;
            Inputs = new List<ItemAmount>(inputs);
            Outputs = new List<ItemAmount>(outputs);
        }
    }

    // ---- wire ----

    internal static class CraftCodec
    {
        public static byte[] EncodeCommand(int corr, CraftOp op, string recipeId, int times)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(corr); w.Write((byte)op); w.Write(recipeId ?? ""); w.Write(times);
            return ms.ToArray();
        }

        public static (int Corr, CraftOp Op, string RecipeId, int Times) DecodeCommand(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return (r.ReadInt32(), (CraftOp)r.ReadByte(), r.ReadString(), r.ReadInt32());
        }

        public static byte[] EncodeReply(int corr, bool ok, string error, List<Recipe> recipes)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(corr); w.Write(ok); w.Write(error ?? "");
            w.Write(recipes.Count);
            foreach (var rec in recipes)
            {
                w.Write(rec.Id ?? "");
                WriteAmounts(w, rec.Inputs);
                WriteAmounts(w, rec.Outputs);
            }
            return ms.ToArray();
        }

        public static (int Corr, bool Ok, string Error, List<Recipe> Recipes) DecodeReply(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var corr = r.ReadInt32(); var ok = r.ReadBoolean(); var err = r.ReadString();
            var count = r.ReadInt32();
            var list = new List<Recipe>(count);
            for (var i = 0; i < count; i++)
                list.Add(new Recipe { Id = r.ReadString(), Inputs = ReadAmounts(r), Outputs = ReadAmounts(r) });
            return (corr, ok, err, list);
        }

        private static void WriteAmounts(BinaryWriter w, List<ItemAmount> a)
        {
            w.Write(a.Count);
            foreach (var x in a) { w.Write(x.ItemId ?? ""); w.Write(x.Count); }
        }

        private static List<ItemAmount> ReadAmounts(BinaryReader r)
        {
            var count = r.ReadInt32();
            var list = new List<ItemAmount>(count);
            for (var i = 0; i < count; i++) list.Add(new ItemAmount(r.ReadString(), r.ReadInt64()));
            return list;
        }
    }

    internal static class CraftRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<(bool Ok, string Error, List<Recipe> Recipes)>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<(bool, string, List<Recipe>)>>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<(bool, string, List<Recipe>)> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, (bool, string, List<Recipe>) result) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(result); }
    }

    /// <summary>
    /// Client-side crafting driver, attached by <see cref="CraftingClientExtensions.UseCrafting"/>. Requests a craft
    /// (the server validates and consumes ingredients from your inventory, then grants the outputs) and browses the
    /// recipe book. Inventory changes arrive through your <c>SetNet.Inventory</c> subscription.
    /// </summary>
    public sealed class CraftingClient
    {
        private readonly BaseClient _client;

        internal CraftingClient(BaseClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

        /// <summary>Crafts <paramref name="times"/> of a recipe; throws <see cref="CraftingException"/> if ingredients are missing.</summary>
        public async Task CraftAsync(string recipeId, int times = 1)
        {
            var (ok, error, _) = await Send(CraftOp.Craft, recipeId, Math.Max(1, times)).ConfigureAwait(false);
            if (!ok) throw new CraftingException(error);
        }

        /// <summary>Lists the server's recipe book.</summary>
        public async Task<IReadOnlyList<Recipe>> ListAsync()
        {
            var (ok, error, recipes) = await Send(CraftOp.List, "", 0).ConfigureAwait(false);
            if (!ok) throw new CraftingException(error);
            return recipes;
        }

        private async Task<(bool Ok, string Error, List<Recipe> Recipes)> Send(CraftOp op, string recipeId, int times)
        {
            var id = CraftRegistry.NextId();
            var tcs = new TaskCompletionSource<(bool, string, List<Recipe>)>(TaskCreationOptions.RunContinuationsAsynchronously);
            CraftRegistry.Register(id, tcs);
            try
            {
                await _client.SendAsync(CraftingTypes.Command, CraftCodec.EncodeCommand(id, op, recipeId, times), DeliveryMethod.Reliable).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try { return await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new CraftingException("Craft command timed out."); }
                }
            }
            finally { CraftRegistry.Remove(id); }
        }
    }

    /// <summary>
    /// Server-side crafting hub, attached by <see cref="CraftingServerExtensions.UseCrafting"/>. Holds the recipe
    /// book and performs crafts atomically through the shared <see cref="InventoryServer"/>: all inputs are revoked
    /// first (rolled back on any shortfall) and only then are the outputs granted.
    /// </summary>
    public sealed class CraftingServer
    {
        private static readonly ConcurrentDictionary<BaseServer, CraftingServer> Servers = new ConcurrentDictionary<BaseServer, CraftingServer>();

        private readonly InventoryServer _inventory;
        private readonly ConcurrentDictionary<string, Recipe> _recipes = new ConcurrentDictionary<string, Recipe>();

        internal CraftingServer(InventoryServer inventory) => _inventory = inventory;

        internal static CraftingServer Enable(BaseServer server, InventoryServer inventory)
            => Servers.GetOrAdd(server, _ => new CraftingServer(inventory));

        internal static CraftingServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>Registers (or replaces) a recipe.</summary>
        public CraftingServer Define(Recipe recipe)
        {
            if (recipe == null || string.IsNullOrEmpty(recipe.Id)) throw new ArgumentException("Recipe needs a non-empty id.", nameof(recipe));
            _recipes[recipe.Id] = recipe;
            return this;
        }

        /// <summary>Crafts <paramref name="times"/> of a recipe for a player key; returns false with a reason on failure.</summary>
        public async Task<(bool Ok, string Error)> CraftAsync(string playerKey, string recipeId, int times)
        {
            if (!_recipes.TryGetValue(recipeId ?? "", out var recipe)) return (false, "No such recipe.");
            if (times < 1) return (false, "Invalid craft count.");

            // Revoke all inputs first; compensate everything taken if any single revoke can't be satisfied.
            var taken = new List<(string Item, long Count)>();
            foreach (var input in recipe.Inputs)
            {
                var need = input.Count * times;
                if (await _inventory.TryRevokeAsync(playerKey, input.ItemId, need).ConfigureAwait(false)) taken.Add((input.ItemId, need));
                else
                {
                    foreach (var t in taken) await _inventory.GrantAsync(playerKey, t.Item, t.Count).ConfigureAwait(false);
                    return (false, $"Missing ingredient {input.ItemId}.");
                }
            }
            foreach (var output in recipe.Outputs)
                await _inventory.GrantAsync(playerKey, output.ItemId, output.Count * times).ConfigureAwait(false);
            return (true, "");
        }

        internal async Task OnCommand(BasePeer peer, byte[] data)
        {
            var (corr, op, recipeId, times) = CraftCodec.DecodeCommand(data);
            if (op == CraftOp.List)
            {
                await Reply(peer, corr, true, "", new List<Recipe>(_recipes.Values)).ConfigureAwait(false);
                return;
            }
            var (ok, error) = await CraftAsync(_inventory.KeyOf(peer), recipeId, times).ConfigureAwait(false);
            await Reply(peer, corr, ok, error, new List<Recipe>()).ConfigureAwait(false);
        }

        private static Task Reply(BasePeer peer, int corr, bool ok, string error, List<Recipe> recipes)
        {
            try { return peer.SendAsync(CraftingTypes.Reply, CraftCodec.EncodeReply(corr, ok, error, recipes), DeliveryMethod.Reliable); }
            catch { return Task.CompletedTask; }
        }
    }

    /// <summary>Auto-discovered server handler for crafting commands.</summary>
    [MessageHandler(CraftingTypes.Command)]
    public sealed class CraftingCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var hub = CraftingServer.For(peer.CurrentPeerInfo.Server);
            return hub?.OnCommand(peer, data) ?? Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for correlated crafting replies.</summary>
    [MessageHandler(CraftingTypes.Reply)]
    public sealed class CraftingReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var (corr, ok, err, recipes) = CraftCodec.DecodeReply(data); CraftRegistry.Complete(corr, (ok, err, recipes)); return Task.CompletedTask; }
    }

    /// <summary>Attaches the crafting hub to a server by composition.</summary>
    public static class CraftingServerExtensions
    {
        /// <summary>Enables server-side crafting; returns the hub (register recipes, craft). Pass the <see cref="InventoryServer"/> from <c>UseInventory</c>.</summary>
        public static CraftingServer UseCrafting(this BaseServer server, InventoryServer inventory)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            return CraftingServer.Enable(server, inventory);
        }
    }

    /// <summary>Attaches a crafting driver to a client by composition.</summary>
    public static class CraftingClientExtensions
    {
        /// <summary>Enables client-side crafting; returns the driver (<c>CraftAsync</c>/<c>ListAsync</c>).</summary>
        public static CraftingClient UseCrafting(this BaseClient client) => new CraftingClient(client);
    }

    /// <summary>One-time bootstrap so the crafting handlers are discovered. Call at startup.</summary>
    public static class CraftingRuntime
    {
        /// <summary>Ensures the crafting layer is discoverable.</summary>
        public static void Enable() { _ = CraftingTypes.Command; }
    }
}
