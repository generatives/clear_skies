using ClearSkies.Engine.Serialization;
using DefaultEcs;

namespace ClearSkies.Engine.Entities;

/// <summary>
/// An entity's descendants that replicate with it, each under a key that's the same on every machine (a chunk volume's
/// block entities, by cell). Whatever makes the descendants gives them their keys; key 0 is the root itself.
/// </summary>
public interface IReplicatedChildren
{
    /// <summary>Every descendant here, with its key (never 0).</summary>
    IEnumerable<(ulong Key, Entity Entity)> ReplicatedChildren { get; }

    /// <summary>The descendant with <paramref name="key"/>, if it's here.</summary>
    bool TryGetReplicatedChild(ulong key, out Entity entity);
}

/// <summary>
/// The components whose state the machine with authority over an entity streams to everyone else who has it: the
/// entity itself (one with an <see cref="EntityId"/> and a <see cref="NetOwner"/>) and its descendants (see
/// <see cref="Children{T}"/>). Each component type is registered once, with how to write and read it, and nothing else
/// is needed for it to replicate (the Net layer's ReplicationSystem sends it when its bytes change, and all of it now
/// and then).
/// <para>Component IDs are given in registration order, so every machine must register the same components in the same
/// order: <see cref="Checksum"/> lets a joining machine check. Only ever the latest state, over an unreliable channel:
/// a lost change waits for the next full refresh, as does a machine that has just been sent the entity. Fine for
/// animation and effects; discrete changes (a door opened) go through commands.</para>
/// </summary>
public sealed class ReplicatedComponents
{
    public delegate void Writer<T>(in T component, NetWriter w);
    public delegate void Reader<T>(ref T component, ref NetReader r);

    /// <summary>A registered component type, seen without its type.</summary>
    public abstract class Component
    {
        protected Component(ushort id, string name) => (Id, Name) = (id, name);

        public ushort Id { get; }
        public string Name { get; }

        /// <summary>Writes <paramref name="entity"/>'s component, if it has one; returns whether it did.</summary>
        public abstract bool TryWrite(Entity entity, NetWriter w);

        /// <summary>Reads the component into <paramref name="entity"/>'s, if it has one; the bytes are read either way.</summary>
        public abstract void Read(Entity entity, ref NetReader r);
    }

    private sealed class Component<T>(ushort id, Writer<T> write, Reader<T> read) : Component(id, typeof(T).FullName!)
    {
        public override bool TryWrite(Entity entity, NetWriter w)
        {
            if (!entity.Has<T>()) return false;
            write(entity.Get<T>(), w);
            return true;
        }

        public override void Read(Entity entity, ref NetReader r)
        {
            if (entity.IsAlive && entity.Has<T>()) read(ref entity.Get<T>(), ref r);
            else { T scratch = default!; read(ref scratch, ref r); }
        }
    }

    private readonly List<Component> _components = new();
    private readonly List<Func<Entity, IReplicatedChildren?>> _children = new();

    public IReadOnlyList<Component> All => _components;

    /// <summary>Replicates component <typeparamref name="T"/>, written by <paramref name="write"/> and read into the
    /// receiver's own copy by <paramref name="read"/> (which may set only some of it).</summary>
    public void Register<T>(Writer<T> write, Reader<T> read)
    {
        if (_components.Count > ushort.MaxValue) throw new InvalidOperationException("Too many replicated components.");
        if (_components.Any(c => c.Name == typeof(T).FullName)) throw new ArgumentException($"{typeof(T).Name} is already replicated.");
        _components.Add(new Component<T>((ushort)_components.Count, write, read));
    }

    /// <summary>An entity with component <typeparamref name="T"/> replicates its descendants too: those
    /// <paramref name="children"/> gives.</summary>
    public void Children<T>(Func<T, IReplicatedChildren> children) =>
        _children.Add(e => e.Has<T>() ? children(e.Get<T>()) : null);

    public Component? Find(ushort id) => id < _components.Count ? _components[id] : null;

    /// <summary>The descendants of <paramref name="root"/> that replicate with it.</summary>
    public IEnumerable<IReplicatedChildren> ChildrenOf(Entity root)
    {
        foreach (var children in _children)
            if (children(root) is { } c) yield return c;
    }

    /// <summary>The entity under <paramref name="root"/> with <paramref name="key"/> (0: the root), if it's here.</summary>
    public bool TryFind(Entity root, ulong key, out Entity entity)
    {
        entity = root;
        if (key == 0) return true;
        foreach (var children in ChildrenOf(root))
            if (children.TryGetReplicatedChild(key, out entity)) return true;
        return false;
    }

    /// <summary>A hash of the registered components, in order: the same on every machine that registers the same.</summary>
    public ulong Checksum
    {
        get
        {
            var w = new NetWriter();
            foreach (var c in _components) w.WriteString(c.Name);
            return DescriptionHash.Of(w.Written);
        }
    }
}
