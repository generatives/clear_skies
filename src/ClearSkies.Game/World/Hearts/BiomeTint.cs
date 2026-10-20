using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Keeps the renderer's <see cref="ClimateTint"/> filled with the biomes' tints (<see cref="Biomes.Tint"/>) around the
/// camera: rebuilt on a worker whenever the camera has gone <see cref="Recentre"/> blocks from the middle of the last one,
/// which leaves it well inside the box (a box is <see cref="ClimateTint.SizeXZ"/> times <see cref="ClimateTint.CellXZ"/>
/// blocks across).
/// </summary>
public sealed class BiomeTintSystem : ISystem
{
    private const float Recentre = 256f;

    private readonly ContinentTerrain _terrain;
    private float _centreX = float.NaN, _centreZ = float.NaN;
    private Task? _building;

    public BiomeTintSystem(ulong seed) => _terrain = ContinentTerrain.For(seed);

    public void Update(float dt)
    {
        if (_building is { IsCompleted: false }) return;
        var focus = ClimateTint.Focus;
        if (MathF.Abs(focus.X - _centreX) < Recentre && MathF.Abs(focus.Z - _centreZ) < Recentre) return;
        const float Half = ClimateTint.SizeXZ * ClimateTint.CellXZ / 2f;
        float originX = MathF.Floor((focus.X - Half) / ClimateTint.CellXZ) * ClimateTint.CellXZ;
        float originZ = MathF.Floor((focus.Z - Half) / ClimateTint.CellXZ) * ClimateTint.CellXZ;
        _centreX = focus.X; _centreZ = focus.Z;
        _building = Task.Run(() => ClimateTint.Submit(originX, originZ, Build(_terrain, originX, originZ)));
    }

    /// <summary>The tint volume whose low corner is at (originX, originZ): each texel the tint at its middle.</summary>
    public static byte[] Build(ContinentTerrain terrain, float originX, float originZ)
    {
        const int N = ClimateTint.SizeXZ;
        var texels = new byte[ClimateTint.Bytes];
        for (int k = 0; k < N; k++)
        for (int i = 0; i < N; i++)
        {
            float x = originX + (i + 0.5f) * ClimateTint.CellXZ, z = originZ + (k + 0.5f) * ClimateTint.CellXZ;
            float regional = terrain.Heat(x, z), wet = terrain.Wet(x, z), pick = terrain.Stands(x, z);
            for (int j = 0; j < ClimateTint.SizeY; j++)
            {
                float y = ClimateTint.BottomY + (j + 0.5f) * ClimateTint.CellY;
                var (r, g, b, a) = Biomes.Tint(Biomes.Heat(y, regional), wet, pick);
                int at = 4 * (i + N * (k + N * j));
                texels[at] = (byte)(r * 255f + 0.5f);
                texels[at + 1] = (byte)(g * 255f + 0.5f);
                texels[at + 2] = (byte)(b * 255f + 0.5f);
                texels[at + 3] = (byte)(a * 255f + 0.5f);
            }
        }
        return texels;
    }
}
