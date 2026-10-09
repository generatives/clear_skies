using DefaultEcs;

namespace ClearSkies.Engine.Entities;

/// <summary>The synced fields' IDs, all in one table: they go over the wire, so each must be unique and the same on
/// every machine (the protocol version catches format changes, not a reused ID).</summary>
public static class SyncedFieldIds
{
    public const byte FanThrust = 1;
}

/// <summary>
/// Block entity state that the machine flying a ship streams to everyone else, for animation and effects (a Fan's
/// thrust, for its flames). Each field is one byte of a component, registered once where the game's systems are set
/// up (<see cref="Register{T}"/>, with an ID from <see cref="SyncedFieldIds"/>); nothing else is needed for it to sync.
/// The Net layer's SyncedStateSystem sends each field when it changes, and every field of a ship about once a second,
/// on the ship's snapshots; other machines apply them as their copy of the ship is played back past those ticks.
/// <para>Only ever the latest value, over an unreliable channel: a lost change waits for the next refresh, and so
/// does a player who has just joined or seen the ship come into view (up to about a second of wrong values). Fine for
/// animation; not for gameplay state, and discrete changes (a door opened) go through commands.</para>
/// </summary>
public sealed class SyncedFields
{
    public delegate void Setter<T>(ref T component, byte value);

    /// <summary>A registered field: reads it off a block entity, if it has the component, and sets it.</summary>
    public abstract class Field
    {
        protected Field(byte id) => Id = id;

        public byte Id { get; }

        public abstract bool TryGet(Entity block, out byte value);

        public abstract void Set(Entity block, byte value);
    }

    private sealed class Field<T> : Field
    {
        private readonly Func<T, byte> _get;
        private readonly Setter<T> _set;

        public Field(byte id, Func<T, byte> get, Setter<T> set) : base(id) => (_get, _set) = (get, set);

        public override bool TryGet(Entity block, out byte value)
        {
            value = block.Has<T>() ? _get(block.Get<T>()) : (byte)0;
            return block.Has<T>();
        }

        public override void Set(Entity block, byte value)
        {
            if (block.Has<T>()) _set(ref block.Get<T>(), value);
        }
    }

    private readonly Field?[] _byId = new Field?[256];
    private readonly List<Field> _all = new();

    public IReadOnlyList<Field> All => _all;

    /// <summary>Syncs a byte of component <typeparamref name="T"/> under <paramref name="id"/>.</summary>
    public void Register<T>(byte id, Func<T, byte> get, Setter<T> set)
    {
        if (_byId[id] is not null) throw new ArgumentException($"Synced field {id} is already registered.", nameof(id));
        var field = new Field<T>(id, get, set);
        _byId[id] = field;
        _all.Add(field);
    }

    public Field? Find(byte id) => _byId[id];

    /// <summary>A 0-1 fraction as a byte, and back.</summary>
    public static byte FromFraction(float f) => (byte)MathF.Round(System.Math.Clamp(f, 0f, 1f) * byte.MaxValue);
    public static float ToFraction(byte v) => v / (float)byte.MaxValue;
}
