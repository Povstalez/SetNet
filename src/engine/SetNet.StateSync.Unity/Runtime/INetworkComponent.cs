using System.Collections.Generic;
using SetNet.StateSync;

namespace SetNet.StateSync.Unity
{
    /// <summary>
    /// A component that contributes replicated fields to a <see cref="NetworkObject"/>. An object's archetype schema is
    /// the ordered concatenation of its network components' fields, so the component order on the prefab must be identical
    /// on the server and every client (it is — they share the prefab). Implement <see cref="DeclareFields"/> to describe
    /// the fields, then read/write them in the same order in <see cref="Serialize"/> (server → wire) and
    /// <see cref="Deserialize"/> (wire → client).
    /// </summary>
    public interface INetworkComponent
    {
        /// <summary>Appends this component's field definitions, in the exact order they are (de)serialized.</summary>
        void DeclareFields(List<FieldDef> fields);

        /// <summary>Server side: reads the current Unity state and writes it into the entity via the cursor.</summary>
        void Serialize(NetworkWriter writer);

        /// <summary>Client side: reads the interpolated entity state from the cursor and applies it to Unity.</summary>
        void Deserialize(NetworkReader reader);
    }

    /// <summary>A forward-only cursor that writes a component's fields into a server <see cref="NetworkEntity"/> at its base index.</summary>
    public struct NetworkWriter
    {
        private readonly NetworkEntity _entity;
        private int _index;

        internal NetworkWriter(NetworkEntity entity, int baseIndex) { _entity = entity; _index = baseIndex; }

        /// <summary>Writes a bool and advances.</summary>
        public void WriteBool(bool v) => _entity.SetBool(_index++, v);
        /// <summary>Writes an integer (Byte/Int/UInt/Long) and advances.</summary>
        public void WriteInt(long v) => _entity.SetInt(_index++, v);
        /// <summary>Writes a float/double and advances.</summary>
        public void WriteFloat(double v) => _entity.SetFloat(_index++, v);
        /// <summary>Writes a 2D vector and advances.</summary>
        public void WriteVec2(Vec2 v) => _entity.SetVec2(_index++, v);
        /// <summary>Writes a 3D vector and advances.</summary>
        public void WriteVec3(Vec3 v) => _entity.SetVec3(_index++, v);
        /// <summary>Writes a quaternion and advances.</summary>
        public void WriteQuat(Quat v) => _entity.SetQuat(_index++, v);
        /// <summary>Writes a string and advances.</summary>
        public void WriteString(string v) => _entity.SetString(_index++, v);
    }

    /// <summary>A forward-only cursor that reads a component's (interpolated) fields from a client <see cref="NetworkEntityView"/> at its base index.</summary>
    public struct NetworkReader
    {
        private readonly NetworkEntityView _view;
        private int _index;

        internal NetworkReader(NetworkEntityView view, int baseIndex) { _view = view; _index = baseIndex; }

        /// <summary>Reads a bool and advances.</summary>
        public bool ReadBool() => _view.GetBool(_index++);
        /// <summary>Reads an integer and advances.</summary>
        public long ReadInt() => _view.GetInt(_index++);
        /// <summary>Reads a float/double and advances.</summary>
        public double ReadFloat() => _view.GetFloat(_index++);
        /// <summary>Reads a 2D vector and advances.</summary>
        public Vec2 ReadVec2() => _view.GetVec2(_index++);
        /// <summary>Reads a 3D vector and advances.</summary>
        public Vec3 ReadVec3() => _view.GetVec3(_index++);
        /// <summary>Reads a quaternion and advances.</summary>
        public Quat ReadQuat() => _view.GetQuat(_index++);
        /// <summary>Reads a string and advances.</summary>
        public string ReadString() => _view.GetString(_index++);
    }
}
