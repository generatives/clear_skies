using ClearSkies.Engine.ECS;
using DefaultEcs;

namespace ClearSkies.Engine.Entities;

/// <summary>
/// Block entity state that the machine flying a ship streams to everyone else, for animation and effects (a Fan's
/// thrust, for its flames). Each field is one byte of a component, registered once under its own ID with
/// <see cref="Register{T}"/>; nothing else is needed for it to sync. Body sync carries a ship's fields with its snapshots,
/// each when it changes and all of them about once a second (for lost packets and players who just arrived), and sets
/// them on the other machines as they arrive. Only ever the latest value, over an unreliable channel: not for gameplay
/// state, and discrete changes (a door opened) go through commands.
/// </summary>
public static class SyncedState
{
    public delegate void Setter<T>(ref T component, byte value);

    /// <summary>A registered field: reads it off a block entity (null if it hasn't the component) and sets it.</summary>
    public sealed record Field(byte Id, Func<Entity, byte?> Get, Action<Entity, byte> Set);

    private static readonly Field?[] ById = new Field?[256];
    private static readonly List<Field> All = new();

    static SyncedState()
    {
        Register<Fan>(1, fan => FromFraction(fan.Thrust), (ref Fan fan, byte v) => fan.Thrust = ToFraction(v));
    }

    public static IReadOnlyList<Field> Fields => All;

    /// <summary>Syncs a byte of component <typeparamref name="T"/>, under <paramref name="id"/> (unique, and the same
    /// on every machine: it's what goes over the wire).</summary>
    public static void Register<T>(byte id, Func<T, byte> get, Setter<T> set)
    {
        if (ById[id] is not null) throw new ArgumentException($"Synced field {id} is already registered.", nameof(id));
        var field = new Field(id, e => e.Has<T>() ? get(e.Get<T>()) : null, (e, v) => { if (e.Has<T>()) set(ref e.Get<T>(), v); });
        ById[id] = field;
        All.Add(field);
    }

    public static Field? Find(byte id) => ById[id];

    /// <summary>A 0-1 fraction as a byte, and back.</summary>
    public static byte FromFraction(float f) => (byte)MathF.Round(System.Math.Clamp(f, 0f, 1f) * byte.MaxValue);
    public static float ToFraction(byte v) => v / (float)byte.MaxValue;
}
