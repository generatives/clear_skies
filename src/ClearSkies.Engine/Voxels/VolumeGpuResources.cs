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
    public const byte BaseSkyLevel = 3;

    public const uint AmbientSky = BaseSkyLevel; // fill before first real flood

    private readonly GpuContext _ctx;

    public ChunkPosition Min { get; private set; }
    public int DX { get; private set; } // volume width  in chunks
    public int DY { get; private set; } // volume height in chunks
    public int DZ { get; private set; } // volume depth  in chunks

    /// <summary>Bumped every <see cref="Allocate"/> (fresh buffers, all bind groups invalidated). Lets a
    /// multi-frame in-progress GPU light relax (see GpuLightSystem) detect that this volume was reallocated
    /// out from under it and abandon cleanly instead of resuming against brand-new, unrelated buffers.</summary>
    public int Generation { get; private set; }

    public int VW => DX * S; // voxels
    public int VH => DY * S;
    public int VD => DZ * S;
    public int TotalVoxels    => VW * VH * VD;
    public int TotalOpacityWords => DX * DY * DZ * WordsPerChunk; // chunk-major

    public GpuBuffer Opacity { get; private set; } = null!;
    public GpuBuffer LightA  { get; private set; } = null!;
    public GpuBuffer LightB  { get; private set; } = null!;
    public GpuBuffer Dims    { get; private set; } = null!; // [VW, VH, VD, 0]

    /// <summary>Per-voxel directional-sun visibility (0-255, 255 = fully lit), volume-linear like LightA.
    /// Recomputed each frame by <c>GpuSunVisPass</c> from the world-space sun shadow map; sampled by the
    /// fragment shader (group 2, binding 2) instead of doing per-pixel PCF. Prefilled 255 (lit) on allocate.</summary>
    public GpuBuffer SunVis  { get; private set; } = null!;

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

    /// <summary>Sun-visibility compute bind group (SunVis + Opacity + sun shadow map + params). 0 = not created /
    /// stale after resize. The shadow-map view is constant (one shared <c>SunShadowPass</c>), so this only needs
    /// recreating when the volume buffers are reallocated.</summary>
    public nint SunVisBind { get; set; }

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

    /// <summary>Reallocates the volume to exactly cover [min, max] (inclusive chunk coords), synchronously on
    /// the calling thread. All buffers are recreated empty and all bind groups invalidated; the caller must
    /// re-upload every chunk and re-flood. Used only for the very first allocation of a volume (nothing is
    /// rendering from it yet, so there's nothing to double-buffer against) — see <see cref="Prepare"/> /
    /// <see cref="AdoptPrepared"/> for the background path used by re-windowing a live volume.</summary>
    public void Reallocate(ChunkPosition min, ChunkPosition max) => Allocate(min, max);

    private void Allocate(ChunkPosition min, ChunkPosition max) => AdoptPrepared(Prepare(_ctx, min, max));

    /// <summary>
    /// The raw buffers for a volume window [min, max], created and pre-filled but not yet installed into any
    /// live <see cref="VolumeGpuResources"/> instance. Building these (mainly the <c>CreateStorage</c> calls —
    /// tens to ~100ms for a few-hundred-MB window at a few thousand loaded chunks) touches no state belonging
    /// to a live volume or its chunks, so it's safe to run on a background thread (see GpuResidencySystem)
    /// while the current volume keeps rendering unaffected; only <see cref="AdoptPrepared"/> — a cheap
    /// pointer-swap plus disposing the old buffers — needs to happen on the main thread.
    /// </summary>
    public sealed class PreparedBuffers
    {
        public ChunkPosition Min;
        public int DX, DY, DZ;
        public GpuBuffer Opacity = null!, LightA = null!, LightB = null!, SunVis = null!, Dims = null!;

        public bool Contains(ChunkPosition pos)
            => pos.X >= Min.X && pos.X < Min.X + DX &&
               pos.Y >= Min.Y && pos.Y < Min.Y + DY &&
               pos.Z >= Min.Z && pos.Z < Min.Z + DZ;

        private int ChunkSlot(ChunkPosition pos)
        {
            int cx = pos.X - Min.X, cy = pos.Y - Min.Y, cz = pos.Z - Min.Z;
            return cx + DX * (cy + DY * cz);
        }

        /// <summary>Writes one chunk's already-packed opacity words into this window's Opacity buffer (a
        /// background-thread-safe counterpart to <see cref="UpdateChunkOpacity"/> — takes the words directly
        /// instead of a live <see cref="ChunkEntry"/>, so it never touches ECS/ChunkVolume state).</summary>
        public void WriteChunkOpacity(ChunkPosition pos, uint[] words)
        {
            if (!Contains(pos)) return;
            ulong byteOffset = (ulong)ChunkSlot(pos) * WordsPerChunk * sizeof(uint);
            Opacity.Write<uint>(byteOffset, words);
        }
    }

    /// <summary>Builds a fresh, standalone set of buffers for [min, max] — everything <see cref="Allocate"/>
    /// used to do, minus touching <c>this</c>. Safe to call from a background thread.</summary>
    public static PreparedBuffers Prepare(GpuContext ctx, ChunkPosition min, ChunkPosition max)
    {
        int dx = max.X - min.X + 1;
        int dy = max.Y - min.Y + 1;
        int dz = max.Z - min.Z + 1;
        int total   = dx * S * dy * S * dz * S;
        int opWords = dx * dy * dz * WordsPerChunk;

        // LightA/LightB/SunVis are the largest buffers here (1 u32/voxel each) and scale with the cube of
        // the view-distance radii — a radius increase that looks modest in chunks can jump this well past
        // the adapter's actual max buffer size (a hard native limit; exceeding it is an unrecoverable wgpu
        // validation error, not a catchable .NET one, and cascades into "invalid buffer" / "invalid bind
        // group" / "invalid command encoder" errors that don't obviously point back here). Check up front
        // so a too-large view distance fails with a clear, actionable message instead.
        ulong lightBufferBytes = (ulong)total * sizeof(uint);
        ulong maxBufferSize = System.Math.Min(ctx.AdapterLimits.MaxBufferSize, ctx.AdapterLimits.MaxStorageBufferBindingSize);
        if (lightBufferBytes > maxBufferSize)
            throw new InvalidOperationException(
                $"GPU light volume too large: {dx}x{dy}x{dz} chunks needs a {lightBufferBytes:N0}-byte light " +
                $"buffer, but this device's max buffer size is {maxBufferSize:N0} bytes. Reduce ChunkLoadSystem's " +
                $"xzRadius/yRadius (or GpuResidencySystem.WindowMargin) so (2*xzRadius+1+2*margin)^2 * " +
                $"(2*yRadius+1+2*margin) * 32768 * 4 stays under that limit.");

        var p = new PreparedBuffers { Min = min, DX = dx, DY = dy, DZ = dz };
        p.Opacity = GpuBuffer.CreateStorage(ctx, (ulong)(opWords * sizeof(uint)));
        p.LightA  = GpuBuffer.CreateStorage(ctx, (ulong)(total  * sizeof(uint)));
        p.LightB  = GpuBuffer.CreateStorage(ctx, (ulong)(total  * sizeof(uint)));
        p.SunVis  = GpuBuffer.CreateStorage(ctx, (ulong)(total  * sizeof(uint)));
        p.Dims    = GpuBuffer.CreateStorage(ctx, 4 * sizeof(uint));

        // Fresh opacity buffer is all-air (0); caller re-uploads every chunk's slice via WriteChunkOpacity.
        p.Opacity.Write<uint>(0, new uint[opWords]);

        // Dim buffer: [VW, VH, VD, 0]
        Span<uint> d = stackalloc uint[4] { (uint)(dx * S), (uint)(dy * S), (uint)(dz * S), 0u };
        p.Dims.Write<uint>(0, d);

        // Pre-fill LightA (dim ambient) and SunVis (fully lit) so chunks look reasonable before the first
        // flood/sun-vis pass, entirely on the GPU (see GpuBufferFill) — for a large volume, a CPU-side fill
        // array plus the QueueWriteBuffer transfer to upload it costs tens of milliseconds of CPU-to-GPU
        // bandwidth, which running this on a background thread (see GpuResidencySystem) keeps off the frame
        // that triggered the (re)allocation.
        ctx.BufferFill.FillU32(p.LightA, AmbientSky, total);
        ctx.BufferFill.FillU32(p.SunVis, 255u, total);

        return p;
    }

    /// <summary>Installs a background-<see cref="Prepare"/>d buffer set as this volume's current one: disposes
    /// the old buffers (a refcount release — safe even if the GPU has not finished with in-flight commands
    /// that reference them, see <see cref="GpuBuffer.Dispose"/>), invalidates every bind group, and bumps
    /// <see cref="Generation"/> so any in-progress relax against the old buffers (see GpuLightSystem) aborts
    /// cleanly instead of resuming against unrelated fresh ones. Cheap (pointer swaps only) — meant to run on
    /// the main thread on the frame the background prep finishes.</summary>
    public void AdoptPrepared(PreparedBuffers prepared)
    {
        ReleaseBindGroups();
        Opacity?.Dispose(); LightA?.Dispose(); LightB?.Dispose(); Dims?.Dispose(); SunVis?.Dispose();
        Generation++;

        Min = prepared.Min;
        DX  = prepared.DX;
        DY  = prepared.DY;
        DZ  = prepared.DZ;

        Opacity = prepared.Opacity;
        LightA  = prepared.LightA;
        LightB  = prepared.LightB;
        SunVis  = prepared.SunVis;
        Dims    = prepared.Dims;
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

    /// <summary>Chunk-major slot index for a chunk: cx + DX*(cy + DY*cz) from <see cref="Min"/>.</summary>
    public int ChunkSlot(ChunkPosition pos)
    {
        int cx = pos.X - Min.X, cy = pos.Y - Min.Y, cz = pos.Z - Min.Z;
        return cx + DX * (cy + DY * cz);
    }

    /// <summary>
    /// Uploads this chunk's opacity slice as one contiguous 1024-word write into the chunk-major opacity
    /// buffer, and rebuilds its emitter list. Recomputes the packed words from block data only when
    /// <see cref="ChunkEntry.PackedOpacityWords"/> is null (first upload, or invalidated by a real edit — see
    /// <c>ChunkVolume.SetBlock</c>); otherwise this call was triggered by a GPU volume reallocation, where the
    /// bits themselves haven't changed and only need re-transmitting into the fresh buffer, so it skips
    /// straight to the write. That keeps re-uploading every loaded chunk after a reallocation cheap enough to
    /// do in one frame — see <see cref="ChunkEntry.PackedOpacityWords"/> for why that matters.
    /// </summary>
    public void UpdateChunkOpacity(ChunkPosition pos, ChunkEntry entry)
    {
        if (!Contains(pos)) return;

        if (entry.PackedOpacityWords is not { } words)
        {
            words = entry.PackedOpacityWords = new uint[WordsPerChunk];
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
                words[ly + S * lz] = bits; // local word: lx is the in-word bit, (ly + 32*lz) is the word
            }
        }

        ulong byteOffset = (ulong)ChunkSlot(pos) * WordsPerChunk * sizeof(uint);
        Opacity.Write<uint>(byteOffset, words);
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
        if (SunVisBind    != 0) { _ctx.Api.BindGroupRelease((BindGroup*)SunVisBind);    SunVisBind    = 0; }
    }

    public void Dispose()
    {
        ReleaseBindGroups();
        Opacity?.Dispose();
        LightA?.Dispose();
        LightB?.Dispose();
        SunVis?.Dispose();
        Dims?.Dispose();
        Emitters?.Dispose();
    }
}
