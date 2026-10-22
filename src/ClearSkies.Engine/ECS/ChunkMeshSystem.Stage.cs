using System.Buffers;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

public sealed partial class ChunkMeshSystem
{
    /// <summary>The loader's stage that meshes each chunk on the worker that loaded it (see <see cref="IChunkPreparer"/>),
    /// so a loaded chunk needs no mesh job of its own: its mesh is uploaded here once it's drawn. Its neighbours are
    /// the ones loaded in the same job; a see-through face beside any other chunk is remeshed after (see
    /// <see cref="PreparedMesh.CurrentIn"/>). Only for volumes that mesh their chunks on their own
    /// (<see cref="ChunkVolume.MeshIgnoresNeighbours"/>), as the streamed world does; another's chunks get mesh jobs.</summary>
    public IChunkPreparer Stage => _stage ??= new MeshStage(_meshers);
    private MeshStage? _stage;

    /// <summary>A <see cref="Stage"/> with no renderer (a test's), meshing against <paramref name="atlas"/>.</summary>
    internal static IChunkPreparer StageFor(TextureAtlas? atlas)
        => new MeshStage(new ThreadLocal<GreedyMesher>(() => new GreedyMesher(atlas)));

    private sealed class MeshStage(ThreadLocal<GreedyMesher> meshers) : IChunkPreparer
    {
        public ChunkPreparation? Prepare(ChunkPosition pos, ChunkData data, IReadOnlyDictionary<ChunkPosition, ChunkData> job)
        {
            var around = new ChunkData?[6];
            // Solid stone between solid stone above and below is most likely buried once its sides load (four in ten
            // chunks of an island are all opaque): it's left to be found buried as it's uploaded, and meshed then if not.
            if (data.IsAllOpaque && Opaque(job, pos.Offset(0, 1, 0)) && Opaque(job, pos.Offset(0, -1, 0)))
                return PreparedMesh.Unmeshed;
            int seeThrough = 0;
            for (int f = 0; f < 6; f++)
            {
                around[f] = job.GetValueOrDefault(pos.Offset(Offsets[f].X, Offsets[f].Y, Offsets[f].Z));
                if (ChunkVolume.FaceHasTransparent(data, f)) seeThrough |= 1 << f;
            }
            var (opaque, cutout, transparent) = MeshAndPack(meshers.Value!, data, around, alone: true, pos);
            return new PreparedMesh(opaque, cutout, transparent, FindModelBlocks(data), around, seeThrough);
        }

        private static bool Opaque(IReadOnlyDictionary<ChunkPosition, ChunkData> job, ChunkPosition p)
            => job.TryGetValue(p, out var d) && d.IsAllOpaque;
    }

    // The neighbour on each side, in ChunkVolume.FaceHasSolid's order.
    private static readonly (int X, int Y, int Z)[] Offsets =
        { (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1) };

    /// <summary>A chunk's mesh made as it loaded (see <see cref="Stage"/>), on the chunk until it's uploaded.</summary>
    internal sealed class PreparedMesh(Packed opaque, Packed cutout, Packed transparent, ModelCell[] models,
                                       ChunkData?[] around, int seeThroughFaces) : ChunkPreparation
    {
        public Packed Opaque { get; } = opaque;
        public Packed Cutout { get; } = cutout;
        public Packed Transparent { get; } = transparent;
        public ModelCell[] Models { get; } = models;

        /// <summary>No mesh: made for a chunk likely to be buried (see <see cref="Stage"/>), which is meshed as it's
        /// uploaded only if it isn't.</summary>
        public static PreparedMesh Unmeshed => new(Packed.None, Packed.None, Packed.None, Array.Empty<ModelCell>(),
                                                   new ChunkData?[6], 0) { Meshed = false };
        public bool Meshed { get; private init; } = true;

        /// <summary>Whether this mesh was made with <paramref name="neighbour"/> beside face <paramref name="face"/>, so
        /// its arriving there changes nothing.</summary>
        public bool MadeAgainst(int face, ChunkData neighbour) => ReferenceEquals(around[face], neighbour);

        /// <summary>Whether this mesh is still right for the chunk at <paramref name="pos"/>: each face with see-through
        /// blocks on it (the only ones culled against a neighbour, see <see cref="ChunkVolume.MeshIgnoresNeighbours"/>)
        /// has the neighbour it was made against, or none still.</summary>
        public bool CurrentIn(ChunkVolume volume, ChunkPosition pos)
        {
            for (int f = 0; f < 6; f++)
                if ((seeThroughFaces >> f & 1) != 0
                    && !ReferenceEquals(volume.GetData(pos.Offset(Offsets[f].X, Offsets[f].Y, Offsets[f].Z)), around[f]))
                    return false;
            return true;
        }

        /// <summary>Gives the packed meshes back to the pool; this is done with.</summary>
        public void Release()
        {
            if (Opaque.Data.Length > 0) ArrayPool<byte>.Shared.Return(Opaque.Data);
            if (Cutout.Data.Length > 0) ArrayPool<byte>.Shared.Return(Cutout.Data);
            if (Transparent.Data.Length > 0) ArrayPool<byte>.Shared.Return(Transparent.Data);
        }

        // The chunk waits for its upload, not for a mesh job.
        internal override void ApplyTo(ChunkEntry entry)
        {
            if (!entry.Volume.MeshIgnoresNeighbours) { Release(); return; } // meshed alone, but it culls against them
            entry.Entity.Remove<NeedsRemeshFlag>();
            entry.Entity.Set(this);
        }
    }
}
