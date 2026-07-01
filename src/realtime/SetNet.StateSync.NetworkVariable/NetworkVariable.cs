using System;
using System.Collections.Generic;

namespace SetNet.StateSync.NetworkVariable
{
    /// <summary>
    /// A typed, change-tracked view over one replicated field. Instead of remembering field indices and polling
    /// <c>view.GetFloat(2)</c>, bind a <see cref="NetworkVariable{T}"/> to it and read <see cref="Value"/> or subscribe to
    /// <see cref="Changed"/>. Call <see cref="Poll"/> once per frame (after <c>ClientReplication.Update()</c>) and it raises
    /// <see cref="Changed"/> whenever the interpolated value differs from last frame. Supports <c>float</c>, <c>double</c>,
    /// <c>int</c>, <c>long</c>, <c>bool</c>, <c>string</c>, <see cref="Vec2"/>, <see cref="Vec3"/> and <see cref="Quat"/>.
    /// </summary>
    /// <typeparam name="T">The field's CLR type.</typeparam>
    public sealed class NetworkVariable<T>
    {
        private readonly NetworkEntityView _view;
        private readonly int _index;
        private readonly Func<NetworkEntityView, int, T> _read;
        private T _last = default!;
        private bool _has;

        /// <summary>Raised (during <see cref="Poll"/>) when the value changes.</summary>
        public event Action<T>? Changed;

        /// <summary>Binds to field <paramref name="index"/> of a client-side entity view.</summary>
        public NetworkVariable(NetworkEntityView view, int index)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _index = index;
            _read = Reader();
        }

        /// <summary>The current (interpolated) value.</summary>
        public T Value => _read(_view, _index);

        /// <summary>Re-reads the value and raises <see cref="Changed"/> if it differs from the last poll. Call once per frame.</summary>
        public void Poll()
        {
            var v = Value;
            if (_has && EqualityComparer<T>.Default.Equals(v, _last)) return;
            _has = true;
            _last = v;
            Changed?.Invoke(v);
        }

        private static Func<NetworkEntityView, int, T> Reader()
        {
            var t = typeof(T);
            if (t == typeof(float)) return (v, i) => (T)(object)(float)v.GetFloat(i);
            if (t == typeof(double)) return (v, i) => (T)(object)v.GetFloat(i);
            if (t == typeof(int)) return (v, i) => (T)(object)(int)v.GetInt(i);
            if (t == typeof(long)) return (v, i) => (T)(object)v.GetInt(i);
            if (t == typeof(bool)) return (v, i) => (T)(object)v.GetBool(i);
            if (t == typeof(string)) return (v, i) => (T)(object)v.GetString(i);
            if (t == typeof(Vec2)) return (v, i) => (T)(object)v.GetVec2(i);
            if (t == typeof(Vec3)) return (v, i) => (T)(object)v.GetVec3(i);
            if (t == typeof(Quat)) return (v, i) => (T)(object)v.GetQuat(i);
            throw new NotSupportedException($"NetworkVariable<{t.Name}> is not supported. Use float/double/int/long/bool/string/Vec2/Vec3/Quat.");
        }
    }

    /// <summary>Fluent helper to bind a <see cref="NetworkVariable{T}"/> to a field on a view.</summary>
    public static class NetworkVariableExtensions
    {
        /// <summary>Binds a typed, change-tracked variable to field <paramref name="index"/> of this view.</summary>
        public static NetworkVariable<T> Watch<T>(this NetworkEntityView view, int index) => new NetworkVariable<T>(view, index);
    }
}
