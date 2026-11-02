using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Keeps the renderer's climate tint volume filled from an <see cref="IClimateTintMap"/> around the camera. The shader
/// samples it per fragment, blended between texels, so a tint changes smoothly rather than block by block.
///
/// The volume is <see cref="SizeXZ"/> by <see cref="SizeXZ"/> by <see cref="SizeY"/> RGBA8 texels, each
/// <see cref="CellXZ"/> blocks across and <see cref="CellY"/> tall, from <see cref="BottomY"/> up. Past its edges, where
/// distant terrain is drawn coarse, a far volume around the same middle takes over: as many texels, each
/// <see cref="FarCellXZ"/> blocks across; past that, the nearest edge texel's tint applies. Both are rebuilt on a worker
/// whenever the camera has gone <see cref="Recentre"/> blocks from the middle of the last ones, which leaves it well
/// inside the near box.
/// </summary>
public sealed class ClimateTintSystem : IRenderSystem
{
    public const int SizeXZ = 256, CellXZ = 8, FarCellXZ = 64, SizeY = 16, CellY = 128, BottomY = -256;

    /// <summary>How far the far volume's low corner is from the near one's (blocks, in x and in z): they share a middle.</summary>
    public const float FarOffset = (CellXZ - FarCellXZ) * SizeXZ / 2f;

    /// <summary>The bytes of the volumes: x fastest, then z, then y; the near volume's layers, then the far one's.</summary>
    public const int Bytes = SizeXZ * SizeXZ * SizeY * 2 * 4;

    private const float Recentre = 256f;

    private readonly Renderer _renderer;
    private readonly IClimateTintMap _map;
    private float _centreX = float.NaN, _centreZ = float.NaN;
    private Task<(float X, float Z, byte[] Texels)>? _building;

    public ClimateTintSystem(Renderer renderer, IClimateTintMap map)
    {
        _renderer = renderer;
        _map = map;
    }

    public void Render(SystemStage stage, in RenderContext frame)
    {
        if (_building is { } b)
        {
            if (!b.IsCompleted) return;
            _building = null;
            var (x, z, texels) = b.Result;
            _renderer.SetClimateTint(x, z, texels);
        }
        var focus = frame.CameraPosition;
        if (MathF.Abs(focus.X - _centreX) < Recentre && MathF.Abs(focus.Z - _centreZ) < Recentre) return;
        const float Half = SizeXZ * CellXZ / 2f;
        float originX = MathF.Floor((focus.X - Half) / CellXZ) * CellXZ;
        float originZ = MathF.Floor((focus.Z - Half) / CellXZ) * CellXZ;
        _centreX = focus.X; _centreZ = focus.Z;
        var map = _map;
        _building = Task.Run(() => (originX, originZ, Build(map, originX, originZ)));
    }

    /// <summary>The volumes whose near one's low corner is at (originX, originZ): each texel the tint at its middle.</summary>
    public static byte[] Build(IClimateTintMap map, float originX, float originZ)
    {
        var texels = new byte[Bytes];
        Fill(texels, map, originX, originZ, CellXZ, 0);
        Fill(texels, map, originX + FarOffset, originZ + FarOffset, FarCellXZ, SizeY);
        return texels;
    }

    private static void Fill(byte[] texels, IClimateTintMap map, float originX, float originZ, int cell, int firstLayer)
    {
        Span<float> heights = stackalloc float[SizeY];
        Span<Vector4> tints = stackalloc Vector4[SizeY];
        for (int j = 0; j < SizeY; j++) heights[j] = BottomY + (j + 0.5f) * CellY;
        for (int k = 0; k < SizeXZ; k++)
        for (int i = 0; i < SizeXZ; i++)
        {
            map.Column(originX + (i + 0.5f) * cell, originZ + (k + 0.5f) * cell, heights, tints);
            for (int j = 0; j < SizeY; j++)
            {
                int at = 4 * (i + SizeXZ * (k + SizeXZ * (firstLayer + j)));
                texels[at] = Byte(tints[j].X);
                texels[at + 1] = Byte(tints[j].Y);
                texels[at + 2] = Byte(tints[j].Z);
                texels[at + 3] = Byte(tints[j].W);
            }
        }
    }

    private static byte Byte(float v) => (byte)(System.Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
}
