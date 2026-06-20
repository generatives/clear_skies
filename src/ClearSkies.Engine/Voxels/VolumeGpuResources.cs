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

    public const uint AmbientSky = LightEngine.BaseSkyLevel; // fill before first real flood

    private readonly GpuContext _ctx;

    public ChunkPosition Min { get; private set; }
    public int DX { get; private set; } // volume width  in chunks
    public int DY { get; private set; } // volume height in chunks
    public int DZ { get; private set; } // volume depth  in chunks

    public int VW => DX * S; // voxels
    public int VH => DY * S;
    public int VD => DZ * S;
    public int TotalVoxels    => VW * VH * VD;
    public int TotalOpacityWords => (TotalVoxels + 31) / 32;

    public GpuBuffer Opacity { get; private set; } = null!;
    public GpuBuffer LightA  { get; private set; } = null!;
    public GpuBuffer LightB  { get; private set; } = null!;
    public GpuBuffer Dims    { get; private set; } = null!; // [VW, VH, VD, 0]

    /// <summary>Flood ping-pong bind groups (group 0). 0 = not created yet / stale after resize.</summary>
    public nint FloodBindEven { get; set; }
    public nint FloodBindOdd  { get; set; }

    /// <summary>Fragment-shader bind group over <see cref="LightA"/> (group 2). 0 = not created yet / stale.</summary>
    public nint RenderBindGroup { get; set; }

    // CPU shadow of the opacity bitset — updated on every SetBlock, uploaded before flood.
    private uint[] _opacityShadow = Array.Empty<uint>();
    public bool OpacityDirty { get; set; }

    private VolumeGpuResources(GpuContext ctx) => _ctx = ctx;

    /// <summary>Allocates a new volume covering [min, max] (chunk coordinates, inclusive).</summary>
    public static VolumeGpuResources Create(GpuContext ctx, ChunkPosition min, ChunkPosition max)
    {
        var v = new VolumeGpuResources(ctx);
        v.Allocate(min, max);
        return v;
    }

    /// <summary>
    /// Expands the volume to cover <paramref name="newMin"/>/<paramref name="newMax"/> if they fall
    /// outside the current bounds. Returns true if a reallocation occurred (caller should re-upload all
    /// chunk data and mark all chunks for remesh).
    /// </summary>
    public bool EnsureContains(ChunkPosition newMin, ChunkPosition newMax)
    {
        if (newMin.X >= Min.X && newMin.Y >= Min.Y && newMin.Z >= Min.Z &&
            newMax.X < Min.X + DX && newMax.Y < Min.Y + DY && newMax.Z < Min.Z + DZ)
            return false; // already fits

        Allocate(
            new ChunkPosition(System.Math.Min(newMin.X, Min.X), System.Math.Min(newMin.Y, Min.Y), System.Math.Min(newMin.Z, Min.Z)),
            new ChunkPosition(System.Math.Max(newMax.X, Min.X + DX - 1), System.Math.Max(newMax.Y, Min.Y + DY - 1), System.Math.Max(newMax.Z, Min.Z + DZ - 1)));
        return true;
    }

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

        _opacityShadow = new uint[opWords];
        Opacity = GpuBuffer.CreateStorage(_ctx, (ulong)(opWords * sizeof(uint)));
        LightA  = GpuBuffer.CreateStorage(_ctx, (ulong)(total  * sizeof(uint)));
        LightB  = GpuBuffer.CreateStorage(_ctx, (ulong)(total  * sizeof(uint)));
        Dims    = GpuBuffer.CreateStorage(_ctx, 4 * sizeof(uint));

        // Dim buffer: [VW, VH, VD, 0]
        Span<uint> d = stackalloc uint[4] { (uint)VW, (uint)VH, (uint)VD, 0u };
        Dims.Write<uint>(0, d);

        // Pre-fill LightA with dim ambient so chunks look reasonable before first flood.
        var fill = new uint[total];
        Array.Fill(fill, AmbientSky);
        LightA.Write<uint>(0, fill);

        OpacityDirty = true;
    }

    // ── Bounds helpers ────────────────────────────────────────────────────────

    public bool Contains(ChunkPosition pos)
        => pos.X >= Min.X && pos.X < Min.X + DX &&
           pos.Y >= Min.Y && pos.Y < Min.Y + DY &&
           pos.Z >= Min.Z && pos.Z < Min.Z + DZ;

    /// <summary>Returns the volume-space voxel origin (bx, by, bz) of the chunk at <paramref name="pos"/>.</summary>
    public (int bx, int by, int bz) ChunkVoxelBase(ChunkPosition pos)
        => ((pos.X - Min.X) * S, (pos.Y - Min.Y) * S, (pos.Z - Min.Z) * S);

    // ── Opacity ───────────────────────────────────────────────────────────────

    /// <summary>Updates this chunk's bits in the CPU opacity shadow. Call <see cref="UploadOpacityIfDirty"/> to sync GPU.</summary>
    public void UpdateChunkOpacity(ChunkPosition pos, ChunkData data)
    {
        if (!Contains(pos)) return;
        var (bx, by, bz) = ChunkVoxelBase(pos);
        for (int lz = 0; lz < S; lz++)
        for (int ly = 0; ly < S; ly++)
        for (int lx = 0; lx < S; lx++)
        {
            int vi = (bx + lx) + VW * ((by + ly) + VH * (bz + lz));
            if (BlockRegistry.Get(data.Get(lx, ly, lz)).Opacity >= 15)
                _opacityShadow[vi >> 5] |=  1u << (vi & 31);
            else
                _opacityShadow[vi >> 5] &= ~(1u << (vi & 31));
        }
        OpacityDirty = true;
    }

    /// <summary>Uploads the CPU opacity shadow to GPU if dirty.</summary>
    public void UploadOpacityIfDirty()
    {
        if (!OpacityDirty) return;
        Opacity.Write<uint>(0, _opacityShadow);
        OpacityDirty = false;
    }

    // ── Light seed ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the per-cycle light seed into <paramref name="buf"/> (caller allocates, length = TotalVoxels).
    /// Each voxel: bits 0-7 = CPU sky from <see cref="ChunkEntry.Light"/>, bits 8-15 = block emission.
    /// Unloaded slots stay 0.
    /// </summary>
    public void BuildLightSeed(IEnumerable<KeyValuePair<ChunkPosition, ChunkEntry>> chunks, uint[] buf)
    {
        Array.Clear(buf, 0, buf.Length);
        foreach (var (pos, entry) in chunks)
        {
            if (!Contains(pos)) continue;
            if (entry.NeedsRelight) continue; // CPU light not yet initialised → leave 0 (floods as dark)
            var (bx, by, bz) = ChunkVoxelBase(pos);
            for (int lz = 0; lz < S; lz++)
            for (int ly = 0; ly < S; ly++)
            for (int lx = 0; lx < S; lx++)
            {
                int vi       = (bx + lx) + VW * ((by + ly) + VH * (bz + lz));
                byte sky     = entry.Light.GetSky(lx, ly, lz);
                byte emis    = BlockRegistry.Get(entry.Data.Get(lx, ly, lz)).LightEmission;
                buf[vi]      = (uint)sky | ((uint)emis << 8);
            }
        }
    }

    // ── Bind group lifecycle ──────────────────────────────────────────────────

    public void ReleaseBindGroups()
    {
        if (FloodBindEven != 0) { _ctx.Api.BindGroupRelease((BindGroup*)FloodBindEven); FloodBindEven = 0; }
        if (FloodBindOdd  != 0) { _ctx.Api.BindGroupRelease((BindGroup*)FloodBindOdd);  FloodBindOdd  = 0; }
        if (RenderBindGroup != 0) { _ctx.Api.BindGroupRelease((BindGroup*)RenderBindGroup); RenderBindGroup = 0; }
    }

    public void Dispose()
    {
        ReleaseBindGroups();
        Opacity?.Dispose();
        LightA?.Dispose();
        LightB?.Dispose();
        Dims?.Dispose();
    }
}
