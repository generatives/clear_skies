using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;

namespace ClearSkies.Net.Protocol;

/// <summary>One replicated component's state (see <see cref="ReplicatedComponents"/>): which entity under the root
/// (<see cref="Key"/>, 0 for the root itself), which component, and its bytes as its writer wrote them.</summary>
public readonly record struct ComponentState(ulong Key, ushort Component, byte[] Data)
{
    public int EncodedSize => 8 + VarSize(Component) + VarSize((uint)Data.Length) + Data.Length;

    internal static int VarSize(uint v)
    {
        int n = 1;
        while (v >= 0x80) { v >>= 7; n++; }
        return n;
    }
}

/// <summary>An entity's replicated components that changed (or all of them, now and then), as the machine with
/// authority over it had them at a tick.</summary>
public sealed class EntityState
{
    public EntityId Root;
    public readonly List<ComponentState> Components = new();

    public int EncodedSize
    {
        get
        {
            int size = 4 + ComponentState.VarSize((uint)Components.Count);
            foreach (var c in Components) size += c.EncodedSize;
            return size;
        }
    }

    public void Write(NetWriter w)
    {
        Root.Write(w);
        w.WriteVarUInt((uint)Components.Count);
        foreach (var c in Components)
        {
            w.WriteUInt64(c.Key);
            w.WriteVarUInt(c.Component);
            w.WriteBytes(c.Data);
        }
    }

    public static EntityState Read(ref NetReader r)
    {
        var s = new EntityState { Root = EntityId.Read(ref r) };
        uint count = r.ReadVarUInt();
        if (count > r.Remaining / 10) throw new InvalidDataException($"Entity state with {count} components.");
        for (uint i = 0; i < count; i++)
        {
            ulong key = r.ReadUInt64();
            uint component = r.ReadVarUInt();
            if (component > ushort.MaxValue) throw new InvalidDataException($"Replicated component {component}.");
            s.Components.Add(new ComponentState(key, (ushort)component, r.ReadBytes().ToArray()));
        }
        return s;
    }

    /// <summary>A frame stops taking entities before it passes this many bytes, under an unreliable packet's limit.</summary>
    public const int MaxFrameBytes = 1000;

    public delegate void PacketSender(ReadOnlySpan<byte> packet);

    /// <summary>Writes <paramref name="states"/> as frames of up to <see cref="MaxFrameBytes"/> (or one entity, whatever
    /// its size), each sent as it's written.</summary>
    public static void WriteFrames(NetWriter writer, uint tick, IReadOnlyList<EntityState> states, PacketSender send)
    {
        for (int start = 0, count; start < states.Count; start += count)
        {
            count = 1;
            for (int bytes = states[start].EncodedSize;
                 start + count < states.Count && count < ushort.MaxValue
                 && bytes + states[start + count].EncodedSize <= MaxFrameBytes; count++)
                bytes += states[start + count].EncodedSize;
            writer.Clear();
            writer.WriteByte((byte)MessageKind.EntityStateFrame);
            writer.WriteUInt32(tick);
            writer.WriteUInt16((ushort)count);
            for (int i = 0; i < count; i++) states[start + i].Write(writer);
            send(writer.Written);
        }
    }

    /// <summary>Reads a frame written by <see cref="WriteFrames"/> (after its kind) into <paramref name="states"/>;
    /// returns its tick.</summary>
    public static uint ReadFrame(ref NetReader r, List<EntityState> states)
    {
        uint tick = r.ReadUInt32();
        int count = r.ReadUInt16();
        states.Clear();
        for (int i = 0; i < count; i++) states.Add(Read(ref r));
        return tick;
    }
}
