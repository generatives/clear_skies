using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Pipeline;

/// <summary>A chunk of one of the pipeline's grids (see <see cref="ChunkPipeline"/>): the key for everything it knows
/// about the chunk.</summary>
public readonly record struct ChunkKey(int Grid, ChunkPosition Pos)
{
    public override string ToString() => $"{Grid}:{Pos}";
}

/// <summary>On a chunk entity in the pipeline: its key there.</summary>
public struct InPipeline
{
    public ChunkKey Key;
}

/// <summary>A grid of chunks the pipeline makes: one of the terrain's levels, each linked to the next finer and
/// coarser so <see cref="Relation.Children"/> and <see cref="Relation.Parent"/> can reach them, or a ship's.</summary>
public sealed class PipelineGrid
{
    internal PipelineGrid(int id, string name, PipelineGrid? finer, ChunkVolume? volume)
    {
        Id = id;
        Name = name;
        Finer = finer;
        Volume = volume;
        ForVolume = volume != null;
        Level = finer == null ? 0 : finer.Level + 1;
        if (finer != null) finer.Coarser = this;
    }

    public int Id { get; }
    public string Name { get; }
    /// <summary>The grid whose 8 chunks make up each of this one's (the next level of detail in).</summary>
    public PipelineGrid? Finer { get; }
    /// <summary>The grid one of whose chunks covers 8 of this one's.</summary>
    public PipelineGrid? Coarser { get; private set; }
    /// <summary>How many grids finer ones there are: a chunk here covers 2^Level of the finest grid's across.</summary>
    public int Level { get; }

    /// <summary>Made for a volume whose chunks are put there by hand (a ship's, see <see cref="ChunkPipeline.GridFor"/>),
    /// not made by the pipeline.</summary>
    public bool ForVolume { get; }

    /// <summary>The volume its chunks go in, once there is one.</summary>
    public ChunkVolume? Volume { get; set; }

    /// <summary>Taken out of the pipeline (see <see cref="ChunkPipeline.RemoveGrid"/>).</summary>
    public bool Removed { get; internal set; }

    // Worked out as it's added (or before the first chunk, for those added before): each stage's slot in its chunks
    // (-1 where it doesn't have the stage) and the inputs it reads here.
    internal int[] SlotOf = Array.Empty<int>();
    internal int SlotCount;
    internal IReadOnlyList<Input>[] Inputs = Array.Empty<IReadOnlyList<Input>>();

    public ChunkKey Key(ChunkPosition pos) => new(Id, pos);
    public override string ToString() => Name;
}

/// <summary>What changed in a stage's output, so a dependant that only cares about part of it can skip a rerun: the
/// faces whose border cells changed, as bits 0-5 in <see cref="Faces"/>'s order, and whether what's all opaque may have
/// changed (what buries a neighbour). New output changes them all. An urgent change is one someone is waiting to see
/// (an edit): what it runs again goes before everything else, and so on through the stages after.</summary>
public readonly record struct Change(int BorderFaces, bool Opacity = false, bool Urgent = false)
{
    public static readonly Change All = new(0b111111, true);
    public bool Touches(int face) => (BorderFaces >> face & 1) != 0;
}

/// <summary>The six faces of a chunk, in ChunkVolume.FaceHasSolid's order: -x, +x, -y, +y, -z, +z. The face opposite
/// face f is f ^ 1.</summary>
public static class Faces
{
    public static readonly (int X, int Y, int Z)[] Offsets =
        { (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1) };

    public const int Below = 2, Above = 3;

    public static ChunkPosition Across(ChunkPosition p, int face)
        => p.Offset(Offsets[face].X, Offsets[face].Y, Offsets[face].Z);

    /// <summary>The face of <paramref name="from"/> that touches <paramref name="to"/>, or -1 if they aren't neighbours.</summary>
    public static int Toward(ChunkPosition from, ChunkPosition to)
    {
        for (int f = 0; f < 6; f++)
            if (Across(from, f) == to) return f;
        return -1;
    }
}
