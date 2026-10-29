using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Pipeline;

/// <summary>
/// Which chunks a stage's input is read from, around the chunk being made: <see cref="Self"/>, a neighbour, the
/// coarser chunk over it or the finer ones under it. Combined with | (either) and &amp; (both); a relation that tests
/// rather than picks (<see cref="ChangedBorder"/>, or one made by <see cref="Where"/>) only narrows another.
/// </summary>
/// <remarks>The pipeline asks it both ways: which chunks the made one reads (to wait for), and which
/// chunks read a source that just changed (to run again), the second filtered by what changed. Each answer is written
/// into a span the caller gives (at most <see cref="MaxKeys"/>), as it's asked for every result passed on.</remarks>
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

    /// <summary>The most chunks a relation picks.</summary>
    internal const int MaxKeys = 16;

    /// <summary>The chunk across <paramref name="face"/> (see <see cref="Faces"/>), in the same grid.</summary>
    public static Relation Neighbour(int face) => new NeighbourRelation(face);

    public static Relation operator |(Relation a, Relation b) => new Either(a, b);
    public static Relation operator &(Relation a, Relation b) => new Both(a, b);

    /// <summary>The chunks this picks for which <paramref name="test"/> holds: given the made chunk, the source and
    /// what changed in the source (everything, when the made chunk is first waiting for it).</summary>
    public Relation Where(Func<ChunkKey, ChunkKey, Change, bool> test) => this & new Test(test);

    /// <summary>Writes the chunks the made one reads into <paramref name="into"/>; how many, or -1 for a test, which
    /// picks nothing on its own.</summary>
    internal abstract int Sources(ChunkPipeline pipeline, ChunkKey made, Span<ChunkKey> into);

    /// <summary>Writes every chunk that might read <paramref name="source"/> through this; how many, or -1 for a test.</summary>
    internal abstract int Readers(ChunkPipeline pipeline, ChunkKey source, Span<ChunkKey> into);

    /// <summary>Whether the made chunk reads <paramref name="source"/> through this, given what changed in it.</summary>
    internal abstract bool Includes(ChunkPipeline pipeline, ChunkKey made, ChunkKey source, Change change);

    /// <summary>The sources the made chunk reads now, a source's whole output counting as new.</summary>
    internal Span<ChunkKey> Picked(ChunkPipeline pipeline, ChunkKey made, Span<ChunkKey> into)
    {
        int n = Sources(pipeline, made, into);
        return n >= 0 ? into[..n] : throw new InvalidOperationException("A test picks no chunks on its own: use it with & on one that does.");
    }

    /// <summary>The chunks that read <paramref name="source"/> through this and care about <paramref name="change"/>.</summary>
    internal Span<ChunkKey> Affected(ChunkPipeline pipeline, ChunkKey source, Change change, Span<ChunkKey> into)
    {
        int n = Readers(pipeline, source, into);
        if (n < 0) throw new InvalidOperationException("A test picks no chunks on its own.");
        int kept = 0;
        for (int i = 0; i < n; i++)
            if (Includes(pipeline, into[i], source, change)) into[kept++] = into[i];
        return into[..kept];
    }

    private sealed class SelfRelation : Relation
    {
        internal override int Sources(ChunkPipeline p, ChunkKey made, Span<ChunkKey> into) { into[0] = made; return 1; }
        internal override int Readers(ChunkPipeline p, ChunkKey source, Span<ChunkKey> into) { into[0] = source; return 1; }
        internal override bool Includes(ChunkPipeline p, ChunkKey made, ChunkKey source, Change change) => made == source;
    }

    private sealed class NeighbourRelation(int face) : Relation
    {
        internal override int Sources(ChunkPipeline p, ChunkKey made, Span<ChunkKey> into)
        {
            into[0] = made with { Pos = Faces.Across(made.Pos, face) };
            return 1;
        }
        internal override int Readers(ChunkPipeline p, ChunkKey source, Span<ChunkKey> into)
        {
            into[0] = source with { Pos = Faces.Across(source.Pos, face ^ 1) };
            return 1;
        }
        internal override bool Includes(ChunkPipeline p, ChunkKey made, ChunkKey source, Change change)
            => made.Grid == source.Grid && Faces.Across(made.Pos, face) == source.Pos;
    }

    private sealed class ChildrenRelation : Relation
    {
        internal override int Sources(ChunkPipeline p, ChunkKey made, Span<ChunkKey> into)
        {
            if (p.Grid(made.Grid).Finer is not { } finer) return 0;
            for (int j = 0; j < 8; j++) into[j] = finer.Key(Child(made.Pos, j));
            return 8;
        }
        internal override int Readers(ChunkPipeline p, ChunkKey source, Span<ChunkKey> into)
        {
            if (p.Grid(source.Grid).Coarser is not { } coarser) return 0;
            into[0] = coarser.Key(Over(source.Pos));
            return 1;
        }
        internal override bool Includes(ChunkPipeline p, ChunkKey made, ChunkKey source, Change change)
            => p.Grid(made.Grid).Finer?.Id == source.Grid && Over(source.Pos) == made.Pos;
    }

    private sealed class ParentRelation : Relation
    {
        internal override int Sources(ChunkPipeline p, ChunkKey made, Span<ChunkKey> into) => Children.Readers(p, made, into);
        internal override int Readers(ChunkPipeline p, ChunkKey source, Span<ChunkKey> into) => Children.Sources(p, source, into);
        internal override bool Includes(ChunkPipeline p, ChunkKey made, ChunkKey source, Change change)
            => Children.Includes(p, source, made, change);
    }

    private sealed class Either(Relation a, Relation b) : Relation
    {
        internal override int Sources(ChunkPipeline p, ChunkKey made, Span<ChunkKey> into)
        {
            int n = a.Sources(p, made, into);
            return n < 0 ? -1 : Union(n, b.Sources(p, made, into[n..]), into);
        }
        internal override int Readers(ChunkPipeline p, ChunkKey source, Span<ChunkKey> into)
        {
            int n = a.Readers(p, source, into);
            return n < 0 ? -1 : Union(n, b.Readers(p, source, into[n..]), into);
        }
        internal override bool Includes(ChunkPipeline p, ChunkKey made, ChunkKey source, Change change)
            => a.Includes(p, made, source, change) || b.Includes(p, made, source, change);

        /// <summary>A's <paramref name="n"/> chunks in <paramref name="into"/>, then b's <paramref name="m"/> (written
        /// after them) that aren't among them; -1 if b is a test.</summary>
        private static int Union(int n, int m, Span<ChunkKey> into)
        {
            if (m < 0) return -1;
            int count = n;
            for (int i = n; i < n + m; i++)
                if (into[..n].IndexOf(into[i]) < 0) into[count++] = into[i];
            return count;
        }
    }

    private sealed class Both(Relation a, Relation b) : Relation
    {
        internal override int Sources(ChunkPipeline p, ChunkKey made, Span<ChunkKey> into)
        {
            int n = a.Sources(p, made, into);
            if (n < 0) n = b.Sources(p, made, into);
            if (n < 0) return -1;
            int kept = 0;
            for (int i = 0; i < n; i++)
                if (Includes(p, made, into[i], Change.All)) into[kept++] = into[i];
            return kept;
        }
        internal override int Readers(ChunkPipeline p, ChunkKey source, Span<ChunkKey> into)
        {
            int n = a.Readers(p, source, into); // filtered by Includes, with the change
            return n >= 0 ? n : b.Readers(p, source, into);
        }
        internal override bool Includes(ChunkPipeline p, ChunkKey made, ChunkKey source, Change change)
            => a.Includes(p, made, source, change) && b.Includes(p, made, source, change);
    }

    private sealed class Test(Func<ChunkKey, ChunkKey, Change, bool> test) : Relation
    {
        internal override int Sources(ChunkPipeline p, ChunkKey made, Span<ChunkKey> into) => -1;
        internal override int Readers(ChunkPipeline p, ChunkKey source, Span<ChunkKey> into) => -1;
        internal override bool Includes(ChunkPipeline p, ChunkKey made, ChunkKey source, Change change)
            => test(made, source, change);
    }

    internal static ChunkPosition Child(ChunkPosition p, int j)
        => new(p.X * 2 + (j & 1), p.Y * 2 + ((j >> 1) & 1), p.Z * 2 + (j >> 2));

    internal static ChunkPosition Over(ChunkPosition p) => new(p.X >> 1, p.Y >> 1, p.Z >> 1);
}
