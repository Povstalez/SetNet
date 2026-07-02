using System.Collections.Generic;
using Godot;
using SetNet.StateSync;

namespace SetNet.StateSync.Godot
{
    /// <summary>
    /// Replicates an <c>AnimationPlayer</c>'s current animation and its playback position (interpolated). Non-owner
    /// clients follow the server: when the animation name changes they <c>Play</c> it, and they seek to the replicated
    /// position each frame. Set <see cref="Player"/> or leave it to auto-find an <c>AnimationPlayer</c> child of the object.
    /// </summary>
    [GlobalClass]
    public partial class NetworkAnimationPlayer : Node, INetworkComponent
    {
        /// <summary>The animation player to sync. Defaults to the first <c>AnimationPlayer</c> under the parent object.</summary>
        [Export] public AnimationPlayer? Player { get; set; }

        private NetworkObject? _obj;

        /// <inheritdoc/>
        public override void _Ready()
        {
            _obj = GetParent() as NetworkObject;
            if (Player == null && _obj != null)
                foreach (var child in _obj.GetChildren())
                    if (child is AnimationPlayer ap) { Player = ap; break; }
        }

        /// <inheritdoc/>
        public void DeclareFields(List<FieldDef> fields)
        {
            fields.Add(new FieldDef(FieldType.String));                     // current animation name
            fields.Add(new FieldDef(FieldType.Float, interpolate: true));   // playback position (seconds)
        }

        /// <inheritdoc/>
        public void Serialize(NetworkWriter writer)
        {
            writer.WriteString(Player != null ? Player.CurrentAnimation : "");
            writer.WriteFloat(Player != null ? Player.CurrentAnimationPosition : 0.0);
        }

        /// <inheritdoc/>
        public void Deserialize(NetworkReader reader)
        {
            var anim = reader.ReadString();
            var position = reader.ReadFloat();

            if (Player == null) return;
            if (_obj != null && _obj.IsOwner) return;   // owner drives its own animator

            if (!string.IsNullOrEmpty(anim) && Player.CurrentAnimation != anim) Player.Play(anim);
            Player.Seek(position, update: false);
        }
    }
}
