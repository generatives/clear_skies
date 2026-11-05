using ClearSkies.Engine.Generation;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Which of a landmass's columns show a stream, from its flow tree (see <see cref="FlowTree"/>), and how each one's
/// line runs: where it bends (its meander), how wide it runs, and where it springs (see <see cref="CoarseLandmass.Shown"/>).
/// </summary>
internal static class Streams
{
    /// <summary>How far (blocks) a stream's line bends off a column's middle at most, and how quickly (per column) that
    /// changes from column to column: its meanders.</summary>
    private const float Meander = 4f, MeanderFrequency = 0.35f;
    // (How much of its meander a stream keeps along a hillside, and how much its width varies.)
    private const float Hillside = 0.3f, Widen = 0.4f;

    /// <summary>Works out <paramref name="coarse"/>'s Shown, Up, Bend, Wide and Head from its flow tree.</summary>
    public static void Build(CoarseLandmass coarse, ulong seed)
    {
        var down = coarse.Down; var way = coarse.Way; var carried = coarse.Carried;
        // A stream shows where a column carries StreamFlow or more, and more than the columns either side of it across
        // its way (a sheet down a hillside shows one line, not one per column).
        var shown = new float[coarse.Nodes];
        var beside = new (int Left, int Right)[coarse.Nodes]; // the top nodes either side of each one's way, or -1
        int TopAt(int columnX, int columnZ) =>
            columnX < 0 || columnZ < 0 || columnX >= coarse.Nx || columnZ >= coarse.Nz ? -1 : coarse.TopNode(columnX + coarse.Nx * columnZ);
        for (int column = 0; column < coarse.Columns; column++)
        {
            int n = coarse.TopNode(column);
            if (n < 0) continue;
            int columnX = column % coarse.Nx, columnZ = column / coarse.Nx, wayX = way[n].X, wayZ = way[n].Z;
            beside[n] = way[n] == (0, 0) ? (-1, -1) : (TopAt(columnX - wayZ, columnZ + wayX), TopAt(columnX + wayZ, columnZ - wayX));
        }
        float least = coarse.Settings.StreamFlow;
        float CarriedBy(int node) => node < 0 ? 0f : carried[node];
        for (int n = 0; n < coarse.Nodes; n++)
        {
            var (left, right) = beside[n];
            if (way[n] != (0, 0) && carried[n] >= least && carried[n] > CarriedBy(left) && carried[n] >= CarriedBy(right)) shown[n] = carried[n];
        }
        // A shown stream runs on down its way to wherever it goes, even past a column beside it that carries as much (a
        // stream stepping across the grid diagonally runs beside itself).
        // And water the model shows (see CoarseLandmass.WetLevel) running out over dry ground runs on as a stream, however
        // little it carries: a pond's outlet doesn't break off.
        bool Shows(int n) => coarse.Depth[n] > coarse.Settings.Damp + CoarseLandmass.Shallow;
        for (int n = 0; n < coarse.Nodes; n++)
            if (Shows(n) && way[n] != (0, 0) && down[n] >= 0 && !Shows(down[n]) && shown[n] <= 0) shown[n] = MathF.Max(carried[n], 1e-3f);
        for (int n = 0; n < coarse.Nodes; n++)
            for (int next = shown[n] > 0 ? down[n] : -1; next >= 0 && shown[next] <= 0 && way[next] != (0, 0); next = down[next])
                shown[next] = MathF.Max(carried[next], 1e-3f);
        // Each shown stream's main one running into it (the one carrying most), or -1: its line curves on from that one's.
        var up = new int[coarse.Nodes];
        Array.Fill(up, -1);
        for (int n = 0; n < coarse.Nodes; n++)
        {
            int next = down[n];
            if (shown[n] > 0 && next >= 0 && shown[next] > 0 && (up[next] < 0 || shown[n] > shown[up[next]])) up[next] = n;
        }
        // Each column's meander: how far its stream's line bends off the column's middle, across its way: all of it in a
        // valley's bottom (the ground either side of its way Size / 2 or more over its own), none along a hillside (the
        // ground lower on one side), where bending across its way would take it up and down the slope.
        var bendNoise = new FastNoiseLite(unchecked((int)seed + 77));
        bendNoise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        bendNoise.SetFrequency(MeanderFrequency);
        var bend = new float[coarse.Nodes];
        var wide = new float[coarse.Nodes]; // (each column's stream's width, times)
        for (int n = 0; n < coarse.Nodes; n++)
        {
            int column = coarse.ColumnOf[n], columnX = column % coarse.Nx, columnZ = column / coarse.Nx;
            var (left, right) = beside[n];
            float banks = left < 0 || right < 0 ? 0f : MathF.Min(coarse.Floor[left], coarse.Floor[right]) - coarse.Floor[n];
            float valley = Math.Clamp(banks / (coarse.Settings.Size / 2f), 0f, 1f);
            bend[n] = Meander * bendNoise.GetNoise(columnX, columnZ) * (Hillside + (1 - Hillside) * valley);
            wide[n] = 1f + Widen * bendNoise.GetNoise(columnX + 500f, columnZ - 500f);
        }
        // A stream's head: a shown stream out of no water, neither a shown stream nor standing water running into it (nor
        // standing in it), where it springs from the ground.
        bool Wet(int n) => shown[n] > 0 || coarse.Depth[n] > coarse.Settings.Damp;
        var head = new bool[coarse.Nodes];
        for (int n = 0; n < coarse.Nodes; n++) head[n] = shown[n] > 0 && coarse.Depth[n] <= coarse.Settings.Damp;
        for (int n = 0; n < coarse.Nodes; n++) if (Wet(n) && down[n] >= 0) head[down[n]] = false;
        coarse.Shown = shown; coarse.Up = up; coarse.Bend = bend; coarse.Wide = wide; coarse.Head = head;
    }
}
