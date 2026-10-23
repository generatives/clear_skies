using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Pipeline;

/// <summary>
/// Which chunks a stage's input is read from, around the chunk being made: <see cref="Self"/>, a neighbour, the
/// coarser chunk over it or the finer ones under it. Combined with | (either) and &amp; (both); a relation that tests
/// rather than picks (<see cref="ChangedBorder"/>, or one made by <see cref="Where"/>) only narrows another.
/// </summary>
/// <remarks>The pipeline asks it both ways: which chunks the made one reads (to wait for and keep loaded), and which
/// chunks read a source that just changed (to run again), the second filtered by what changed.</remarks>
public abstract class Relation
{
    public static readonly Relation Self = new SelfRelation();
    public static readonly Relation Above = Neighbour(Faces.Above);
    public static readonly Relation Below = Neighbour(Faces.Below);
    /// <summary>The 8 chunks of the finer grid that this one covers.</summary>
    public static readonly Relation Children = new ChildrenRelation();
    /// <summary>The chunk of the coarser grid that covers this one.</summary>
    public static readonly Relation Parent = new ParentRelation();
    public static readonly Relation Neighbours =
        Neighbour(0) | Neighbour(1) | Neighbour(2) | Neighbour(3) | Neighbour(4) | Neighbour(5);
    /// <summary>Neighbours whose cells on the face shared with this chunk changed.</summary>
    public static readonly Relation ChangedBorder = Neighbours.Where((made, source, change)
        => change.Touches(Faces.Toward(source.Pos, made.Pos)));

    /// <summary>The chunk across <paramref name="face"/> (see <see cref="Faces"/>), in the same grid.</summary>
    public static Relation Neighbour(int face) => new NeighbourRelation(face);

    public static Relation operator |(Relation a, Relation b) => new Either(a, b);
    public static Relation operator &(Relation a, Relation b) => new Both(a, b);

    /// <summary>The chunks this picks for which <paramref name="test"/> holds: given the made chunk, the source and
    /// what changed in the source (everything, when the made chunk is first waiting for it).</summary>
    public Relation Where(Func<ChunkKey, ChunkKey, Change, bool> test) => this & new Test(test);

    /// <summary>The chunks the made one reads; null for a test, which picks nothing on its own.</summary>
    internal abstract IEnumerable<ChunkKey>? Sources(ChunkPipeline pipeline, ChunkKey made);

    /// <summary>Every chunk that might read <paramref name="source"/> through this; null for a test.</summary>
    internal abstract IEnumerable<ChunkKey>? Readers(ChunkPipeline pipeline, ChunkKey source);

    /// <summary>Whether the made chunk reads <paramref name="source"/> through this, given what changed in it.</summary>
    internal abstract bool Includes(ChunkPipeline pipeline, ChunkKey made, ChunkKey source, Change change);

    /// <summary>The sources the made chunk reads now, a source's whole output counting as new.</summary>
    internal IEnumerable<ChunkKey> Picked(ChunkPipeline pipeline, ChunkKey made)
        => Sources(pipeline, made) ?? throw new InvalidOperationException("A test picks no chunks on its own: use it with & on one that does.");

    /// <summary>The chunks that read <paramref name="source"/> through this and care about <paramref name="change"/>.</summary>
    internal IEnumerable<ChunkKey> Affected(ChunkPipeline pipeline, ChunkKey source, Change change)
    {
        var readers = Readers(pipeline, source) ?? throw new InvalidOperationException("A test picks no chunks on its own.");
        foreach (var made in readers)
            if (Includes(pipeline, made, source, change)) yield return made;
    }

    private sealed class SelfRelation : Relation
    {
        internal override IEnumerable<ChunkKey> Sources(ChunkPipeline p, ChunkKey made) => new[] { made };
        internal override IEnumerable<ChunkKey> Readers(ChunkPipeline p, ChunkKey source) => new[] { source };
        internal override bool Includes(ChunkPipeline p, ChunkKey made, ChunkKey source, Change change) => made == source;
    }

    private sealed class NeighbourRelation(int face) : Relation
    {
        internal override IEnumerable<ChunkKey> Sources(ChunkPipeline p, ChunkKey made)
            => new[] { made with { Pos = Faces.Across(made.Pos, face) } };
        internal override IEnumerable<ChunkKey> Readers(ChunkPipeline p, ChunkKey source)
            => new[] { source with { Pos = Faces.Across(source.Pos, face ^ 1) } };
        internal override bool Includes(ChunkPipeline p, ChunkKey made, ChunkKey source, Change change)
            => made.Grid == source.Grid && Faces.Across(made.Pos, face) == source.Pos;
    }

    private sealed class ChildrenRelation : Relation
    {
        internal override IEnumerable<ChunkKey> Sources(ChunkPipeline p, ChunkKey made)
        {
            if (p.Grid(made.Grid).Finer is not { } finer) yield break;
            for (int j = 0; j < 8; j++) yield return finer.Key(Child(made.Pos, j));
        }
        internal override IEnumerable<ChunkKey> Readers(ChunkPipeline p, ChunkKey source)
            => p.Grid(source.Grid).Coarser is { } coarser ? new[] { coarser.Key(Over(source.Pos)) } : Array.Empty<ChunkKey>();
        internal override bool Includes(ChunkPipeline p, ChunkKey made, ChunkKey source, Change change)
            => p.Grid(made.Grid).Finer?.Id == source.Grid && Over(source.Pos) == made.Pos;
    }

    private sealed class ParentRelation : Relation
    {
        internal override IEnumerable<ChunkKey> Sources(ChunkPipeline p, ChunkKey made)
            => Children.Readers(p, made)!;
        internal override IEnumerable<ChunkKey> Readers(ChunkPipeline p, ChunkKey source)
            => Children.Sources(p, source)!;
        internal override bool Includes(ChunkPipeline p, ChunkKey made, ChunkKey source, Change change)
            => Children.Includes(p, source, made, change);
    }

    private sealed class Either(Relation a, Relation b) : Relation
    {
        internal override IEnumerable<ChunkKey>? Sources(ChunkPipeline p, ChunkKey made)
            => a.Sources(p, made) is { } x && b.Sources(p, made) is { } y ? x.Union(y) : null;
        internal override IEnumerable<ChunkKey>? Readers(ChunkPipeline p, ChunkKey source)
            => a.Readers(p, source) is { } x && b.Readers(p, source) is { } y ? x.Union(y) : null;
        internal override bool Includes(ChunkPipeline p, ChunkKey made, ChunkKey source, Change change)
            => a.Includes(p, made, source, change) || b.Includes(p, made, source, change);
    }

    private sealed class Both(Relation a, Relation b) : Relation
    {
        internal override IEnumerable<ChunkKey>? Sources(ChunkPipeline p, ChunkKey made)
            => Narrow(a.Sources(p, made), b.Sources(p, made), s => Includes(p, made, s, Change.All));
        internal override IEnumerable<ChunkKey>? Readers(ChunkPipeline p, ChunkKey source)
            => a.Readers(p, source) ?? b.Readers(p, source); // filtered by Includes, with the change
        internal override bool Includes(ChunkPipeline p, ChunkKey made, ChunkKey source, Change change)
            => a.Includes(p, made, source, change) && b.Includes(p, made, source, change);

        private static IEnumerable<ChunkKey>? Narrow(IEnumerable<ChunkKey>? x, IEnumerable<ChunkKey>? y, Func<ChunkKey, bool> keep)
            => (x ?? y)?.Where(keep);
    }

    private sealed class Test(Func<ChunkKey, ChunkKey, Change, bool> test) : Relation
    {
        internal override IEnumerable<ChunkKey>? Sources(ChunkPipeline p, ChunkKey made) => null;
        internal override IEnumerable<ChunkKey>? Readers(ChunkPipeline p, ChunkKey source) => null;
        internal override bool Includes(ChunkPipeline p, ChunkKey made, ChunkKey source, Change change)
            => test(made, source, change);
    }

    internal static ChunkPosition Child(ChunkPosition p, int j)
        => new(p.X * 2 + (j & 1), p.Y * 2 + ((j >> 1) & 1), p.Z * 2 + (j >> 2));

    internal static ChunkPosition Over(ChunkPosition p) => new(p.X >> 1, p.Y >> 1, p.Z >> 1);
}
