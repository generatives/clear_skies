using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Pipeline;

/// <summary>A chunk of one of the pipeline's grids (see <see cref="ChunkPipeline"/>): the key for everything it knows
/// about the chunk.</summary>
public readonly record struct ChunkKey(int Grid, ChunkPosition Pos)
{
    public override string ToString() => $"{Grid}:{Pos}";
}

/// <summary>A grid of chunks the pipeline makes: one of the terrain's levels, each linked to the next finer and
/// coarser so <see cref="Relation.Children"/> and <see cref="Relation.Parent"/> can reach them, or a ship's.</summary>
public sealed class PipelineGrid
{
    internal PipelineGrid(int id, string name, PipelineGrid? finer)
    {
        Id = id;
        Name = name;
        Finer = finer;
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

    public ChunkKey Key(ChunkPosition pos) => new(Id, pos);
    public override string ToString() => Name;
}

/// <summary>What changed in a stage's output, so a dependant that only cares about part of it can skip a rerun: the
/// faces whose border cells changed, as bits 0-5 in <see cref="Faces"/>'s order. New output changes them all.</summary>
public readonly record struct Change(int BorderFaces)
{
    public static readonly Change All = new(0b111111);
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
