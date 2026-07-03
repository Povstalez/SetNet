using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.Dialogue
{
    /// <summary>Command operations (client → server) within the Dialogue channel.</summary>
    internal enum DialogueOp : ushort { Start = 1, Choose = 2 }

    /// <summary>Thrown when a dialogue operation fails.</summary>
    public sealed class DialogueException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public DialogueException(string message) : base(message) { }
    }

    /// <summary>The player's world while walking a dialogue: their key, DI services, and a scratch blackboard for guards/actions.</summary>
    public sealed class DialogueContext
    {
        /// <summary>The player walking the dialogue.</summary>
        public string PlayerKey { get; }
        /// <summary>App services (quests, inventory, flags…) a guard or action may consult.</summary>
        public IServiceProvider? Services { get; }
        /// <summary>Per-conversation scratch state.</summary>
        public IDictionary<string, object> Blackboard { get; } = new Dictionary<string, object>();

        /// <summary>Creates a context.</summary>
        public DialogueContext(string playerKey, IServiceProvider? services) { PlayerKey = playerKey; Services = services; }
    }

    /// <summary>One selectable reply: its text, where it leads, an optional guard that hides it, and an optional side-effect.</summary>
    public sealed class DialogueChoice
    {
        /// <summary>The choice text shown to the player.</summary>
        public string Text { get; }
        /// <summary>The node id this choice leads to (null ends the conversation).</summary>
        public string? Next { get; }
        /// <summary>Optional guard — the choice is hidden when it returns false.</summary>
        public Func<DialogueContext, bool>? Condition { get; }
        /// <summary>Optional side-effect run when the choice is taken (grant a quest, set a flag…).</summary>
        public Action<DialogueContext>? OnChosen { get; }

        /// <summary>Creates a choice.</summary>
        public DialogueChoice(string text, string? next, Func<DialogueContext, bool>? condition = null, Action<DialogueContext>? onChosen = null)
        { Text = text; Next = next; Condition = condition; OnChosen = onChosen; }
    }

    /// <summary>One dialogue node: an id, the text the NPC says, and the choices offered.</summary>
    public sealed class DialogueNode
    {
        /// <summary>Unique node id within its tree.</summary>
        public string Id { get; }
        /// <summary>What the NPC says at this node.</summary>
        public string Text { get; }
        /// <summary>The choices offered (a node with none is a terminal line).</summary>
        public IReadOnlyList<DialogueChoice> Choices { get; }

        /// <summary>Creates a node.</summary>
        public DialogueNode(string id, string text, IReadOnlyList<DialogueChoice>? choices = null)
        { Id = id; Text = text; Choices = choices ?? Array.Empty<DialogueChoice>(); }
    }

    /// <summary>A complete dialogue: its nodes and where it starts. Build one with <see cref="Create"/>.</summary>
    public sealed class DialogueTree
    {
        private readonly Dictionary<string, DialogueNode> _nodes;
        /// <summary>The id of the node the conversation opens on.</summary>
        public string StartNodeId { get; }

        private DialogueTree(Dictionary<string, DialogueNode> nodes, string start) { _nodes = nodes; StartNodeId = start; }

        /// <summary>The node with an id, or null.</summary>
        public DialogueNode? Get(string id) => id != null && _nodes.TryGetValue(id, out var n) ? n : null;

        /// <summary>Starts a fluent builder.</summary>
        public static Builder Create() => new Builder();

        /// <summary>Fluent builder for a <see cref="DialogueTree"/>.</summary>
        public sealed class Builder
        {
            private readonly Dictionary<string, DialogueNode> _nodes = new Dictionary<string, DialogueNode>();
            private string? _start;

            /// <summary>Adds a node (the first added is the start unless <see cref="Start"/> is set).</summary>
            public Builder Node(string id, string text, params DialogueChoice[] choices)
            {
                _nodes[id] = new DialogueNode(id, text, choices);
                _start ??= id;
                return this;
            }

            /// <summary>Sets the start node.</summary>
            public Builder Start(string id) { _start = id; return this; }

            /// <summary>Builds the tree.</summary>
            public DialogueTree Build()
            {
                if (_start == null) throw new InvalidOperationException("Dialogue has no nodes.");
                return new DialogueTree(new Dictionary<string, DialogueNode>(_nodes), _start);
            }
        }
    }

    /// <summary>Settings for the dialogue hub.</summary>
    public sealed class DialogueOptions
    {
        /// <summary>Maps a connected peer to its stable player key.</summary>
        public Func<BasePeer, string> PlayerKey { get; set; } = peer => peer.CurrentPeerInfo.Id.ToString();
        /// <summary>Services exposed to guards/actions via <see cref="DialogueContext.Services"/>.</summary>
        public IServiceProvider? Services { get; set; }
    }

    // ---- wire ----

    internal static class DialogueCodec
    {
        // Node view sent to the client: nodeId, text, isEnd, then the VISIBLE choices (index + text).
        public static byte[] EncodeView(string nodeId, string text, bool isEnd, IReadOnlyList<(int index, string text)> choices)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(nodeId ?? "");
            w.Write(text ?? "");
            w.Write(isEnd);
            w.Write(choices.Count);
            foreach (var c in choices) { w.Write(c.index); w.Write(c.text ?? ""); }
            return ms.ToArray();
        }

        public static DialogueNodeView DecodeView(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            var id = r.ReadString();
            var text = r.ReadString();
            var isEnd = r.ReadBoolean();
            var count = r.ReadInt32();
            var choices = new List<DialogueChoiceView>(count);
            for (var i = 0; i < count; i++) choices.Add(new DialogueChoiceView(r.ReadInt32(), r.ReadString()));
            return new DialogueNodeView(id, text, isEnd, choices);
        }
    }

    /// <summary>A choice as seen by the client: the index to pass to <see cref="DialogueClient.ChooseAsync"/> and its text.</summary>
    public sealed class DialogueChoiceView
    {
        /// <summary>The index to pass back to choose this reply.</summary>
        public int Index { get; }
        /// <summary>The choice text.</summary>
        public string Text { get; }
        /// <summary>Creates a view.</summary>
        public DialogueChoiceView(int index, string text) { Index = index; Text = text; }
    }

    /// <summary>A dialogue node as seen by the client: the current line and the visible choices (or an end marker).</summary>
    public sealed class DialogueNodeView
    {
        /// <summary>The node id.</summary>
        public string NodeId { get; }
        /// <summary>The NPC's line.</summary>
        public string Text { get; }
        /// <summary>True when the conversation has ended (no more choices).</summary>
        public bool IsEnd { get; }
        /// <summary>The choices currently offered.</summary>
        public IReadOnlyList<DialogueChoiceView> Choices { get; }
        /// <summary>Creates a view.</summary>
        public DialogueNodeView(string nodeId, string text, bool isEnd, IReadOnlyList<DialogueChoiceView> choices)
        { NodeId = nodeId; Text = text; IsEnd = isEnd; Choices = choices; }
    }

    // ---- client ----

    /// <summary>Client-side dialogue driver (from <see cref="DialogueClientExtensions.UseDialogue"/>).</summary>
    public sealed class DialogueClient
    {
        private readonly BaseClient _client;

        internal DialogueClient(BaseClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

        /// <summary>Opens a dialogue by id and returns its first node.</summary>
        public async Task<DialogueNodeView> StartAsync(string dialogueId)
        {
            try
            {
                var body = await _client.RequestRawAsync(Channels.Dialogue, (ushort)DialogueOp.Start, System.Text.Encoding.UTF8.GetBytes(dialogueId)).ConfigureAwait(false);
                return DialogueCodec.DecodeView(body);
            }
            catch (ProtocolException ex) { throw new DialogueException(ex.Message); }
            catch (TimeoutException) { throw new DialogueException("Dialogue start timed out."); }
        }

        /// <summary>Takes the choice at <paramref name="choiceIndex"/> (from the current node's <see cref="DialogueNodeView.Choices"/>) and returns the next node.</summary>
        public async Task<DialogueNodeView> ChooseAsync(int choiceIndex)
        {
            try
            {
                var buf = BitConverter.GetBytes(choiceIndex);
                var body = await _client.RequestRawAsync(Channels.Dialogue, (ushort)DialogueOp.Choose, buf).ConfigureAwait(false);
                return DialogueCodec.DecodeView(body);
            }
            catch (ProtocolException ex) { throw new DialogueException(ex.Message); }
            catch (TimeoutException) { throw new DialogueException("Dialogue choose timed out."); }
        }
    }

    // ---- server ----

    /// <summary>Server-side dialogue hub (from <see cref="DialogueServerExtensions.UseDialogue"/>): define trees, drive per-player conversations.</summary>
    public sealed class DialogueServer
    {
        private static readonly ConcurrentDictionary<BaseServer, DialogueServer> Servers = new ConcurrentDictionary<BaseServer, DialogueServer>();

        private readonly DialogueOptions _options;
        private readonly ConcurrentDictionary<string, DialogueTree> _trees = new ConcurrentDictionary<string, DialogueTree>();
        // Active conversation per player: (treeId, currentNodeId).
        private readonly ConcurrentDictionary<string, (string treeId, string nodeId)> _active = new ConcurrentDictionary<string, (string, string)>();

        internal DialogueServer(DialogueOptions options) => _options = options;

        internal static DialogueServer Enable(BaseServer server, DialogueOptions? options)
            => Servers.GetOrAdd(server, s =>
            {
                var hub = new DialogueServer(options ?? new DialogueOptions());
                s.PeerDisconnected += peer => hub._active.TryRemove(hub._options.PlayerKey(peer), out _);
                return hub;
            });

        internal static DialogueServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>Registers a dialogue tree under an id.</summary>
        public DialogueServer Define(string dialogueId, DialogueTree tree)
        {
            _trees[dialogueId] = tree ?? throw new ArgumentNullException(nameof(tree));
            return this;
        }

        private DialogueContext Ctx(string playerKey) => new DialogueContext(playerKey, _options.Services);

        // Filters a node's choices to those whose guard passes, keeping the ORIGINAL choice index for OnChosen/Next lookup.
        private List<(int index, string text)> VisibleChoices(DialogueNode node, DialogueContext ctx)
        {
            var list = new List<(int, string)>();
            for (var i = 0; i < node.Choices.Count; i++)
            {
                var c = node.Choices[i];
                if (c.Condition == null || c.Condition(ctx)) list.Add((i, c.Text));
            }
            return list;
        }

        internal async Task HandleStartAsync(ChannelRequest request)
        {
            var playerKey = _options.PlayerKey(request.Peer);
            var dialogueId = System.Text.Encoding.UTF8.GetString(request.RawBody);
            if (!_trees.TryGetValue(dialogueId, out var tree)) throw new ProtocolException($"unknown dialogue '{dialogueId}'");
            var node = tree.Get(tree.StartNodeId)!;
            _active[playerKey] = (dialogueId, node.Id);
            var visible = VisibleChoices(node, Ctx(playerKey));
            await request.ReplyRawAsync(DialogueCodec.EncodeView(node.Id, node.Text, visible.Count == 0, visible)).ConfigureAwait(false);
        }

        internal async Task HandleChooseAsync(ChannelRequest request)
        {
            var playerKey = _options.PlayerKey(request.Peer);
            if (!_active.TryGetValue(playerKey, out var state)) throw new ProtocolException("no active dialogue");
            if (!_trees.TryGetValue(state.treeId, out var tree)) throw new ProtocolException("dialogue no longer defined");
            var node = tree.Get(state.nodeId);
            if (node == null) throw new ProtocolException("dialogue node missing");

            var ctx = Ctx(playerKey);
            var choiceIndex = request.RawBody.Length >= 4 ? BitConverter.ToInt32(request.RawBody, 0) : -1;
            var visible = VisibleChoices(node, ctx);
            if (choiceIndex < 0 || choiceIndex >= visible.Count) throw new ProtocolException("invalid choice");

            var originalIndex = visible[choiceIndex].index;
            var chosen = node.Choices[originalIndex];
            chosen.OnChosen?.Invoke(ctx);

            var next = chosen.Next != null ? tree.Get(chosen.Next) : null;
            if (next == null)
            {
                _active.TryRemove(playerKey, out _);   // conversation ended
                await request.ReplyRawAsync(DialogueCodec.EncodeView("", "", true, Array.Empty<(int, string)>())).ConfigureAwait(false);
                return;
            }

            _active[playerKey] = (state.treeId, next.Id);
            var nextVisible = VisibleChoices(next, ctx);
            await request.ReplyRawAsync(DialogueCodec.EncodeView(next.Id, next.Text, nextVisible.Count == 0, nextVisible)).ConfigureAwait(false);
        }
    }

    /// <summary>Auto-discovered channel service for dialogue.</summary>
    [ProtocolChannel(Channels.Dialogue)]
    public sealed class DialogueChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = DialogueServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("dialogue is not configured on this server");
            return request.Op switch
            {
                (ushort)DialogueOp.Start => hub.HandleStartAsync(request),
                (ushort)DialogueOp.Choose => hub.HandleChooseAsync(request),
                _ => throw new ProtocolException($"unknown dialogue op {request.Op}"),
            };
        }
    }

    /// <summary>Attaches the dialogue hub to a server.</summary>
    public static class DialogueServerExtensions
    {
        /// <summary>Enables the server-side dialogue hub; returns it so you can <c>Define</c> trees.</summary>
        public static DialogueServer UseDialogue(this BaseServer server, DialogueOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return DialogueServer.Enable(server, options);
        }
    }

    /// <summary>Attaches a dialogue driver to a client.</summary>
    public static class DialogueClientExtensions
    {
        /// <summary>Enables client-side dialogue; returns the driver (<c>StartAsync</c>/<c>ChooseAsync</c>).</summary>
        public static DialogueClient UseDialogue(this BaseClient client) => new DialogueClient(client);
    }

    /// <summary>One-time bootstrap so the dialogue channel service is discovered. Call at startup.</summary>
    public static class DialogueRuntime
    {
        /// <summary>Ensures the dialogue layer is discoverable.</summary>
        public static void Enable() { _ = typeof(DialogueChannelService); }
    }
}
