using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Inventory;
using SetNet.Protocol;

namespace SetNet.Quests
{
    /// <summary>Command operations (client → server) within the Quests protocol channel.</summary>
    internal enum QuestOp : ushort { Accept = 1, Abandon = 2, Claim = 3, List = 4 }

    /// <summary>Push events (server → client) within the Quests protocol channel.</summary>
    internal enum QuestEvt : ushort { Updated = 10 }

    /// <summary>Thrown when a quest operation fails (unknown quest, not accepted, not complete, timeout).</summary>
    public sealed class QuestException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public QuestException(string message) : base(message) { }
    }

    /// <summary>A single trackable objective within a quest.</summary>
    public sealed class QuestObjective
    {
        /// <summary>Objective key that game logic reports progress against (e.g. "kill_goblin").</summary>
        public string Key { get; set; } = "";

        /// <summary>How much progress completes it.</summary>
        public long Required { get; set; } = 1;

        /// <summary>Creates an empty objective.</summary>
        public QuestObjective() { }

        /// <summary>Creates an objective requiring <paramref name="required"/> of <paramref name="key"/>.</summary>
        public QuestObjective(string key, long required) { Key = key; Required = required; }
    }

    /// <summary>A quest definition: objectives to complete and item rewards on claim.</summary>
    public sealed class QuestDefinition
    {
        /// <summary>Unique quest id.</summary>
        public string Id { get; set; } = "";

        /// <summary>The objectives that must all be met.</summary>
        public List<QuestObjective> Objectives { get; set; } = new List<QuestObjective>();

        /// <summary>Item rewards granted on claim.</summary>
        public List<ItemStack> Rewards { get; set; } = new List<ItemStack>();

        /// <summary>Creates an empty definition.</summary>
        public QuestDefinition() { }

        /// <summary>Creates a definition with the given id, objectives, and rewards.</summary>
        public QuestDefinition(string id, IEnumerable<QuestObjective> objectives, IEnumerable<ItemStack> rewards)
        { Id = id; Objectives = new List<QuestObjective>(objectives); Rewards = new List<ItemStack>(rewards); }
    }

    /// <summary>One objective's progress in a player's view of a quest.</summary>
    public sealed class QuestObjectiveProgress
    {
        /// <summary>The objective key.</summary>
        public string Key { get; set; } = "";

        /// <summary>Current progress.</summary>
        public long Current { get; set; }

        /// <summary>Required progress.</summary>
        public long Required { get; set; }
    }

    /// <summary>A player's view of an accepted quest.</summary>
    public sealed class QuestView
    {
        /// <summary>The quest id.</summary>
        public string QuestId { get; set; } = "";

        /// <summary>Per-objective progress.</summary>
        public List<QuestObjectiveProgress> Objectives { get; set; } = new List<QuestObjectiveProgress>();

        /// <summary>True when every objective is met (claimable).</summary>
        public bool Completable { get; set; }

        /// <summary>True once the reward has been claimed.</summary>
        public bool Claimed { get; set; }
    }

    // ---- store ----

    /// <summary>A player's persisted quest record: accepted quest id, per-objective progress, and claim flag.</summary>
    public sealed class QuestRecord
    {
        /// <summary>The quest id.</summary>
        public string QuestId { get; set; } = "";

        /// <summary>Per-objective progress counts.</summary>
        public Dictionary<string, long> Progress { get; set; } = new Dictionary<string, long>();

        /// <summary>Whether the reward has been claimed.</summary>
        public bool Claimed { get; set; }
    }

    /// <summary>Persistence for accepted quests. Default <see cref="MemoryQuestStore"/> (in-process); swap for Redis/DB.</summary>
    public interface IQuestStore
    {
        /// <summary>All accepted quest records for a player.</summary>
        Task<IReadOnlyList<QuestRecord>> ListAsync(string playerKey);

        /// <summary>One record, or null when not accepted.</summary>
        Task<QuestRecord?> GetAsync(string playerKey, string questId);

        /// <summary>Creates/updates a record.</summary>
        Task UpsertAsync(string playerKey, QuestRecord record);

        /// <summary>Removes a record (abandon); false when absent.</summary>
        Task<bool> RemoveAsync(string playerKey, string questId);
    }

    /// <summary>In-process quest store.</summary>
    public sealed class MemoryQuestStore : IQuestStore
    {
        private readonly ConcurrentDictionary<string, Dictionary<string, QuestRecord>> _players = new ConcurrentDictionary<string, Dictionary<string, QuestRecord>>();
        private Dictionary<string, QuestRecord> Box(string key) => _players.GetOrAdd(key ?? "", _ => new Dictionary<string, QuestRecord>());

        /// <inheritdoc/>
        public Task<IReadOnlyList<QuestRecord>> ListAsync(string playerKey)
        {
            var box = Box(playerKey);
            lock (box) return Task.FromResult<IReadOnlyList<QuestRecord>>(new List<QuestRecord>(box.Values));
        }

        /// <inheritdoc/>
        public Task<QuestRecord?> GetAsync(string playerKey, string questId)
        {
            var box = Box(playerKey);
            lock (box) return Task.FromResult(box.TryGetValue(questId, out var r) ? r : null);
        }

        /// <inheritdoc/>
        public Task UpsertAsync(string playerKey, QuestRecord record)
        {
            var box = Box(playerKey);
            lock (box) box[record.QuestId] = record;
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<bool> RemoveAsync(string playerKey, string questId)
        {
            var box = Box(playerKey);
            lock (box) return Task.FromResult(box.Remove(questId));
        }
    }

    // ---- wire ----

    internal static class QuestCodec
    {
        public static byte[] EncodeCommand(string questId)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(questId ?? "");
            return ms.ToArray();
        }

        public static string DecodeCommand(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return r.ReadString();
        }

        public static byte[] EncodeReply(List<QuestView> views)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            WriteViews(w, views);
            return ms.ToArray();
        }

        public static List<QuestView> DecodeReply(byte[] data)
        {
            if (data == null || data.Length == 0) return new List<QuestView>();
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return ReadViews(r);
        }

        public static byte[] EncodeEvent(QuestView view)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            WriteViews(w, new List<QuestView> { view });
            return ms.ToArray();
        }

        public static QuestView DecodeEvent(byte[] data)
        {
            if (data == null || data.Length == 0) return new QuestView();
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var views = ReadViews(r);
            return views.Count > 0 ? views[0] : new QuestView();
        }

        private static void WriteViews(BinaryWriter w, List<QuestView> views)
        {
            w.Write(views.Count);
            foreach (var v in views)
            {
                w.Write(v.QuestId ?? "");
                w.Write(v.Completable);
                w.Write(v.Claimed);
                w.Write(v.Objectives.Count);
                foreach (var o in v.Objectives) { w.Write(o.Key ?? ""); w.Write(o.Current); w.Write(o.Required); }
            }
        }

        private static List<QuestView> ReadViews(BinaryReader r)
        {
            var count = r.ReadInt32();
            var list = new List<QuestView>(count);
            for (var i = 0; i < count; i++)
            {
                var v = new QuestView { QuestId = r.ReadString(), Completable = r.ReadBoolean(), Claimed = r.ReadBoolean() };
                var oc = r.ReadInt32();
                for (var j = 0; j < oc; j++) v.Objectives.Add(new QuestObjectiveProgress { Key = r.ReadString(), Current = r.ReadInt64(), Required = r.ReadInt64() });
                list.Add(v);
            }
            return list;
        }
    }

    /// <summary>
    /// Client-side quest driver, attached by <see cref="QuestClientExtensions.UseQuests"/>. Accept and abandon
    /// quests, claim rewards once complete, and watch objective progress via <see cref="Updated"/>. Progress itself
    /// is reported by server game logic — the client only accepts, claims, and observes. Rides the unified protocol
    /// on the <see cref="Channels.Quests"/> channel.
    /// </summary>
    public sealed class QuestClient
    {
        private readonly BaseClient _client;
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        /// <summary>Raised when an accepted quest's progress or completion changes.</summary>
        public event Action<QuestView>? Updated;

        internal QuestClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _subscriptions.Add(_client.OnRaw(Channels.Quests, (ushort)QuestEvt.Updated,
                body => Updated?.Invoke(QuestCodec.DecodeEvent(body))));
        }

        /// <summary>Accepts a quest by id.</summary>
        public Task AcceptAsync(string questId) => Send(QuestOp.Accept, questId);

        /// <summary>Abandons an accepted quest (progress is discarded).</summary>
        public Task AbandonAsync(string questId) => Send(QuestOp.Abandon, questId);

        /// <summary>Claims a completed quest's rewards (throws <see cref="QuestException"/> if not complete).</summary>
        public Task ClaimAsync(string questId) => Send(QuestOp.Claim, questId);

        /// <summary>Lists this player's accepted quests and their progress.</summary>
        public async Task<IReadOnlyList<QuestView>> ListAsync()
            => await SendRaw(QuestOp.List, "").ConfigureAwait(false);

        private async Task Send(QuestOp op, string questId) { await SendRaw(op, questId).ConfigureAwait(false); }

        private async Task<List<QuestView>> SendRaw(QuestOp op, string questId)
        {
            try
            {
                var body = await _client.RequestRawAsync(Channels.Quests, (ushort)op, QuestCodec.EncodeCommand(questId)).ConfigureAwait(false);
                return QuestCodec.DecodeReply(body);
            }
            catch (ProtocolException ex) { throw new QuestException(ex.Message); }
            catch (TimeoutException) { throw new QuestException("Quest command timed out."); }
        }
    }

    /// <summary>
    /// Server-side quest hub, attached by <see cref="QuestServerExtensions.UseQuests"/>. Holds quest definitions;
    /// game logic reports progress with <see cref="ProgressAsync"/> (fanned out to every accepted quest that shares
    /// the objective key), fires <see cref="QuestCompleted"/> when all objectives are first met, and grants item
    /// rewards on claim through the shared <see cref="InventoryServer"/>.
    /// </summary>
    public sealed class QuestServer
    {
        private static readonly ConcurrentDictionary<BaseServer, QuestServer> Servers = new ConcurrentDictionary<BaseServer, QuestServer>();

        private readonly InventoryServer _inventory;
        private readonly IQuestStore _store;
        private readonly ConcurrentDictionary<string, QuestDefinition> _defs = new ConcurrentDictionary<string, QuestDefinition>();

        /// <summary>Raised the first time a player meets all of a quest's objectives (args: player key, quest id).</summary>
        public event Action<string, string>? QuestCompleted;

        internal QuestServer(InventoryServer inventory, IQuestStore store) { _inventory = inventory; _store = store; }

        internal static QuestServer Enable(BaseServer server, InventoryServer inventory, IQuestStore? store)
            => Servers.GetOrAdd(server, _ => new QuestServer(inventory, store ?? new MemoryQuestStore()));

        internal static QuestServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>Registers (or replaces) a quest definition.</summary>
        public QuestServer Define(QuestDefinition def)
        {
            if (def == null || string.IsNullOrEmpty(def.Id)) throw new ArgumentException("Quest needs a non-empty id.", nameof(def));
            _defs[def.Id] = def;
            return this;
        }

        /// <summary>Accepts a quest for a player (no-op if already accepted).</summary>
        public async Task<(bool Ok, string Error)> AcceptAsync(string playerKey, string questId)
        {
            if (!_defs.ContainsKey(questId ?? "")) return (false, "No such quest.");
            if (await _store.GetAsync(playerKey, questId!).ConfigureAwait(false) != null) return (true, "");
            var record = new QuestRecord { QuestId = questId!, Progress = new Dictionary<string, long>(), Claimed = false };
            await _store.UpsertAsync(playerKey, record).ConfigureAwait(false);
            await PushView(playerKey, questId!).ConfigureAwait(false);
            return (true, "");
        }

        /// <summary>Abandons a quest for a player.</summary>
        public async Task<(bool Ok, string Error)> AbandonAsync(string playerKey, string questId)
        {
            await _store.RemoveAsync(playerKey, questId ?? "").ConfigureAwait(false);
            return (true, "");
        }

        /// <summary>
        /// Reports <paramref name="amount"/> progress on objective <paramref name="objectiveKey"/> for a player.
        /// Every accepted, unclaimed quest with that objective advances (capped at the requirement); fires
        /// <see cref="QuestCompleted"/> the first time a quest becomes fully met, and pushes the updated view.
        /// </summary>
        public async Task ProgressAsync(string playerKey, string objectiveKey, long amount = 1)
        {
            if (amount <= 0) return;
            var records = await _store.ListAsync(playerKey).ConfigureAwait(false);
            foreach (var record in records)
            {
                if (record.Claimed || !_defs.TryGetValue(record.QuestId, out var def)) continue;
                QuestObjective? objective = null;
                foreach (var o in def.Objectives) if (o.Key == objectiveKey) { objective = o; break; }
                if (objective == null) continue;

                var wasComplete = IsComplete(def, record);
                record.Progress.TryGetValue(objectiveKey, out var cur);
                record.Progress[objectiveKey] = Math.Min(objective.Required, cur + amount);
                await _store.UpsertAsync(playerKey, record).ConfigureAwait(false);

                if (!wasComplete && IsComplete(def, record)) QuestCompleted?.Invoke(playerKey, record.QuestId);
                await PushView(playerKey, record.QuestId).ConfigureAwait(false);
            }
        }

        /// <summary>Claims a completed quest's rewards (grants items, marks claimed).</summary>
        public async Task<(bool Ok, string Error)> ClaimAsync(string playerKey, string questId)
        {
            var record = await _store.GetAsync(playerKey, questId ?? "").ConfigureAwait(false);
            if (record == null) return (false, "Quest not accepted.");
            if (!_defs.TryGetValue(record.QuestId, out var def)) return (false, "Unknown quest.");
            if (record.Claimed) return (false, "Already claimed.");
            if (!IsComplete(def, record)) return (false, "Quest not complete.");

            foreach (var reward in def.Rewards) await _inventory.GrantAsync(playerKey, reward.ItemId, reward.Count).ConfigureAwait(false);
            record.Claimed = true;
            await _store.UpsertAsync(playerKey, record).ConfigureAwait(false);
            await PushView(playerKey, record.QuestId).ConfigureAwait(false);
            return (true, "");
        }

        /// <summary>Builds the per-player views for every accepted quest.</summary>
        public async Task<List<QuestView>> ViewsAsync(string playerKey)
        {
            var records = await _store.ListAsync(playerKey).ConfigureAwait(false);
            var views = new List<QuestView>();
            foreach (var record in records)
                if (_defs.TryGetValue(record.QuestId, out var def)) views.Add(ToView(def, record));
            return views;
        }

        private bool IsComplete(QuestDefinition def, QuestRecord record)
        {
            foreach (var o in def.Objectives)
            {
                record.Progress.TryGetValue(o.Key, out var cur);
                if (cur < o.Required) return false;
            }
            return true;
        }

        private QuestView ToView(QuestDefinition def, QuestRecord record)
        {
            var view = new QuestView { QuestId = def.Id, Claimed = record.Claimed, Completable = IsComplete(def, record) };
            foreach (var o in def.Objectives)
            {
                record.Progress.TryGetValue(o.Key, out var cur);
                view.Objectives.Add(new QuestObjectiveProgress { Key = o.Key, Current = cur, Required = o.Required });
            }
            return view;
        }

        private async Task PushView(string playerKey, string questId)
        {
            var peer = _inventory.PeerFor(playerKey);
            if (peer == null) return;
            var record = await _store.GetAsync(playerKey, questId).ConfigureAwait(false);
            if (record == null || !_defs.TryGetValue(questId, out var def)) return;
            try { await peer.PublishRawAsync(Channels.Quests, (ushort)QuestEvt.Updated, QuestCodec.EncodeEvent(ToView(def, record))).ConfigureAwait(false); } catch { }
        }

        internal async Task HandleAsync(ChannelRequest request)
        {
            var questId = QuestCodec.DecodeCommand(request.RawBody);
            var playerKey = _inventory.KeyOf(request.Peer);
            switch ((QuestOp)request.Op)
            {
                case QuestOp.Accept:
                {
                    var (ok, err) = await AcceptAsync(playerKey, questId).ConfigureAwait(false);
                    if (!ok) throw new ProtocolException(err);
                    await request.ReplyRawAsync(QuestCodec.EncodeReply(new List<QuestView>())).ConfigureAwait(false);
                    break;
                }
                case QuestOp.Abandon:
                {
                    var (ok, err) = await AbandonAsync(playerKey, questId).ConfigureAwait(false);
                    if (!ok) throw new ProtocolException(err);
                    await request.ReplyRawAsync(QuestCodec.EncodeReply(new List<QuestView>())).ConfigureAwait(false);
                    break;
                }
                case QuestOp.Claim:
                {
                    var (ok, err) = await ClaimAsync(playerKey, questId).ConfigureAwait(false);
                    if (!ok) throw new ProtocolException(err);
                    await request.ReplyRawAsync(QuestCodec.EncodeReply(new List<QuestView>())).ConfigureAwait(false);
                    break;
                }
                case QuestOp.List:
                    await request.ReplyRawAsync(QuestCodec.EncodeReply(await ViewsAsync(playerKey).ConfigureAwait(false))).ConfigureAwait(false);
                    break;
                default:
                    throw new ProtocolException("Unknown quest operation.");
            }
        }
    }

    /// <summary>Auto-discovered channel service for quest commands.</summary>
    [ProtocolChannel(Channels.Quests)]
    public sealed class QuestChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = QuestServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("quests are not configured on this server");
            return hub.HandleAsync(request);
        }
    }

    /// <summary>Attaches the quest hub to a server by composition.</summary>
    public static class QuestServerExtensions
    {
        /// <summary>Enables server-side quests; returns the hub (define/accept/progress/claim). Pass the <see cref="InventoryServer"/> from <c>UseInventory</c>.</summary>
        public static QuestServer UseQuests(this BaseServer server, InventoryServer inventory, IQuestStore? store = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            return QuestServer.Enable(server, inventory, store);
        }
    }

    /// <summary>Attaches a quest driver to a client by composition.</summary>
    public static class QuestClientExtensions
    {
        /// <summary>Enables client-side quests; returns the driver (accept/abandon/claim/list + <c>Updated</c>).</summary>
        public static QuestClient UseQuests(this BaseClient client) => new QuestClient(client);
    }

    /// <summary>One-time bootstrap so the quest channel service is discovered. Call at startup.</summary>
    public static class QuestRuntime
    {
        /// <summary>Ensures the quest layer is discoverable.</summary>
        public static void Enable() { _ = typeof(QuestChannelService); }
    }
}
