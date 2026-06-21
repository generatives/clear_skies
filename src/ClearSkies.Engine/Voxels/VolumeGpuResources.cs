using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// GPU-resident lighting data for an entire <see cref="ChunkVolume"/> (static world or dynamic grid).
/// All loaded chunks are packed into one flat 3D buffer; the flood operates across the whole volume so
/// light crosses chunk boundaries naturally.
///
/// Volume-space voxel index: <c>vx + VW*(vy + VH*vz)</c> where VW/VH are the volume width/height in
/// voxels. A chunk at chunk-offset (cx,cy,cz) from <see cref="Min"/> starts at voxel
/// (cx*32, cy*32, cz*32) in volume space.
///
/// Buffers (all <c>array&lt;u32&gt;</c>):
///   Opacity: 1 bit per voxel, packed 32/u32.
///   LightA/LightB: 1 u32 per voxel; bits 0-7 = sky (0-15), bits 8-15 = block (0-15).
/// Dims: [VW, VH, VD, 0] — read by the flood compute shader.
/// </summary>
internal sealed unsafe class VolumeGpuResources : IDisposable
{
    private const int S = ChunkData.Size; // 32

    /// <summary>u32 words of opacity per chunk: 32³ bits / 32 = 1024. Opacity is stored <b>chunk-major</b>
    /// (one contiguous 1024-word slice per chunk) so a single chunk's opacity uploads as one contiguous
    /// write instead of re-uploading the whole volume bitset.</summary>
    public const int WordsPerChunk = (S * S * S) / 32; // 1024

    /// <summary>Ambient sky level injected from every face of the volume. Carries through open air with no
    /// attenuation along each sweep direction; relaxation then loses 1 per step into occluded pockets. This
    /// is soft fill light only — direct sun is a separate world-space shadow term in the renderer. Baked
    /// into the flood shaders and used as the pre-first-flood ambient fill.</summary>
    public const byte BaseSkyLevel = 10;

    public const uint AmbientSky = BaseSkyLevel; // fill before first real flood

    private readonly GpuContext _ctx;

    public ChunkPosition Min { get; private set; }
    public int DX { get; private set; } // volume width  in chunks
    public int DY { get; private set; } // volume height in chunks
    public int DZ { get; private set; } // volume depth  in chunks

    public int VW => DX * S; // voxels
    public int VH => DY * S;
    public int VD => DZ * S;
    public int TotalVoxels    => VW * VH * VD;
    public int TotalOpacityWords => DX * DY * DZ * WordsPerChunk; // chunk-major

    public GpuBuffer Opacity { get; private set; } = null!;
    public GpuBuffer LightA  { get; private set; } = null!;
    public GpuBuffer LightB  { get; private set; } = null!;
    public GpuBuffer Dims    { get; private set; } = null!; // [VW, VH, VD, 0]

    /// <summary>Per-volume sparse emitter list buffer: pairs of (volume-voxel-index, level) as u32, packed.
    /// Sized to the largest emitter count seen; grown lazily. 0-length until first emitter cycle.</summary>
    public GpuBuffer? Emitters { get; private set; }
    public int EmitterCapacity { get; private set; }

    /// <summary>Ambient sky-sweep bind group (group 0). 0 = not created yet / stale after resize.</summary>
    public nint SkySweepBind { get; set; }

    /// <summary>Per-emitter scatter bind group (group 0). 0 = not created / stale after resize or emitter grow.</summary>
    public nint ScatterBind { get; set; }

    /// <summary>Flood ping-pong bind groups (group 0). 0 = not created yet / stale after resize.</summary>
    public nint FloodBindEven { get; set; }
    public nint FloodBindOdd  { get; set; }

    /// <summary>Fragment-shader bind group over <see cref="LightA"/> (group 2). 0 = not created yet / stale.</summary>
    public nint RenderBindGroup { get; set; }

    /// <summary>Cross-volume injection bind group (LightA + lamp cube map + inject params). 0 = not created /
    /// stale after resize. The cube-map texture view is constant (one shared <c>LightShadowPass</c>), so this
    /// only needs recreating when LightA is reallocated.</summary>
    public nint InjectBind { get; set; }

    private VolumeGpuResources(GpuContext ctx) => _ctx = ctx;

    /// <summary>Allocates a new volume covering [min, max] (chunk coordinates, inclusive).</summary>
    public static VolumeGpuResources Create(GpuContext ctx, ChunkPosition min, ChunkPosition max)
    {
        var v = new VolumeGpuResources(ctx);
        v.Allocate(min, max);
        return v;
    }

    /// <summary>True if the current allocation fully covers the inclusive chunk AABB [min, max].</summary>
    public bool Covers(ChunkPosition min, ChunkPosition max)
        => min.X >= Min.X && max.X < Min.X + DX &&
           min.Y >= Min.Y && max.Y < Min.Y + DY &&
           min.Z >= Min.Z && max.Z < Min.Z + DZ;

    /// <summary>Reallocates the volume to exactly cover [min, max] (inclusive chunk coords). All buffers are
    /// recreated empty and all bind groups invalidated; the caller must re-upload every chunk and re-flood.</summary>
    public void Reallocate(ChunkPosition min, ChunkPosition max) => Allocate(min, max);

    private void Allocate(ChunkPosition min, ChunkPosition max)
    {
        ReleaseBindGroups();
        Opacity?.Dispose(); LightA?.Dispose(); LightB?.Dispose(); Dims?.Dispose();

        Min = min;
        DX  = max.X - min.X + 1;
        DY  = max.Y - min.Y + 1;
        DZ  = max.Z - min.Z + 1;

        int total    = TotalVoxels;
        int opWords  = TotalOpacityWords;

        Opacity = GpuBuffer.CreateStorage(_ctx, (ulong)(opWords * sizeof(uint)));
        LightA  = GpuBuffer.CreateStorage(_ctx, (ulong)(total  * sizeof(uint)));
        LightB  = GpuBuffer.CreateStorage(_ctx, (ulong)(total  * sizeof(uint)));
        Dims    = GpuBuffer.CreateStorage(_ctx, 4 * sizeof(uint));

        // Fresh opacity buffer is all-air (0); GpuResidencySystem re-uploads every chunk's slice (it marks
        // them all NeedsGpuUpload on a realloc).
        Opacity.Write<uint>(0, new uint[opWords]);

        // Dim buffer: [VW, VH, VD, 0]
        Span<uint> d = stackalloc uint[4] { (uint)VW, (uint)VH, (uint)VD, 0u };
        Dims.Write<uint>(0, d);

        // Pre-fill LightA with dim ambient so chunks look reasonable before first flood.
        var fill = new uint[total];
        Array.Fill(fill, AmbientSky);
        LightA.Write<uint>(0, fill);
    }

    // ── Bounds helpers ────────────────────────────────────────────────────────

    public bool Contains(ChunkPosition pos)
        => pos.X >= Min.X && pos.X < Min.X + DX &&
           pos.Y >= Min.Y && pos.Y < Min.Y + DY &&
           pos.Z >= Min.Z && pos.Z < Min.Z + DZ;

    /// <summary>Returns the volume-space voxel origin (bx, by, bz) of the chunk at <paramref name="pos"/>.</summary>
    public (int bx, int by, int bz) ChunkVoxelBase(ChunkPosition pos)
        => ((pos.X - Min.X) * S, (pos.Y - Min.Y) * S, (pos.Z - Min.Z) * S);

    // ── Opacity (chunk-major) + emitters ───────────────────────────────────────

    // Reusable scratch for one chunk's opacity slice (avoids per-upload allocation).
    private readonly uint[] _chunkWords = new uint[WordsPerChunk];

    /// <summary>Chunk-major slot index for a chunk: cx + DX*(cy + DY*cz) from <see cref="Min"/>.</summary>
    public int ChunkSlot(ChunkPosition pos)
    {
        int cx = pos.X - Min.X, cy = pos.Y - Min.Y, cz = pos.Z - Min.Z;
        return cx + DX * (cy + DY * cz);
    }

    /// <summary>
    /// Rebuilds this chunk's opacity slice and emitter list from its block data, then uploads the slice as
    /// one contiguous 1024-word write into the chunk-major opacity buffer. Replaces the old whole-volume
    /// re-upload: only the edited chunk's slice touches the GPU.
    /// </summary>
    public void UpdateChunkOpacity(ChunkPosition pos, ChunkEntry entry)
    {
        if (!Contains(pos)) return;
        var data = entry.Data;
        entry.Emitters.Clear();

        for (int lz = 0; lz < S; lz++)
        for (int ly = 0; ly < S; ly++)
        {
            uint bits = 0u;
            for (int lx = 0; lx < S; lx++)
            {
                var def = BlockRegistry.Get(data.Get(lx, ly, lz));
                if (def.Opacity >= 15) bits |= 1u << lx;
                if (def.LightEmission > 0)
                    entry.Emitters.Add(new EmitterVoxel((byte)lx, (byte)ly, (byte)lz, def.LightEmission));
            }
            _chunkWords[ly + S * lz] = bits; // local word: lx is the in-word bit, (ly + 32*lz) is the word
        }

        ulong byteOffset = (ulong)ChunkSlot(pos) * WordsPerChunk * sizeof(uint);
        Opacity.Write<uint>(byteOffset, _chunkWords);
    }

    /// <summary>Ensures the emitter buffer holds at least <paramref name="count"/> entries (2 u32 each),
    /// growing (reallocating) if needed and invalidating the stale scatter bind group.</summary>
    public void EnsureEmitterCapacity(int count)
    {
        if (Emitters != null && count <= EmitterCapacity) return;
        int cap = EmitterCapacity == 0 ? 64 : EmitterCapacity;
        while (cap < count) cap *= 2;
        Emitters?.Dispose();
        Emitters = GpuBuffer.CreateStorage(_ctx, (ulong)(cap * 2 * sizeof(uint)));
        EmitterCapacity = cap;
        if (ScatterBind != 0) { _ctx.Api.BindGroupRelease((BindGroup*)ScatterBind); ScatterBind = 0; }
    }

    // ── Bind group lifecycle ──────────────────────────────────────────────────

    public void ReleaseBindGroups()
    {
        if (SkySweepBind   != 0) { _ctx.Api.BindGroupRelease((BindGroup*)SkySweepBind);   SkySweepBind   = 0; }
        if (ScatterBind   != 0) { _ctx.Api.BindGroupRelease((BindGroup*)ScatterBind);   ScatterBind   = 0; }
        if (FloodBindEven != 0) { _ctx.Api.BindGroupRelease((BindGroup*)FloodBindEven); FloodBindEven = 0; }
        if (FloodBindOdd  != 0) { _ctx.Api.BindGroupRelease((BindGroup*)FloodBindOdd);  FloodBindOdd  = 0; }
        if (RenderBindGroup != 0) { _ctx.Api.BindGroupRelease((BindGroup*)RenderBindGroup); RenderBindGroup = 0; }
        if (InjectBind    != 0) { _ctx.Api.BindGroupRelease((BindGroup*)InjectBind);    InjectBind    = 0; }
    }

    public void Dispose()
    {
        ReleaseBindGroups();
        Opacity?.Dispose();
        LightA?.Dispose();
        LightB?.Dispose();
        Dims?.Dispose();
        Emitters?.Dispose();
    }
}
