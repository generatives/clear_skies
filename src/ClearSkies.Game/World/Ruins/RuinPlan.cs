namespace ClearSkies.Game.Ruins;

/// <summary>A rectangle of a site's cells: I0..I1-1 across, J0..J1-1 down.</summary>
public readonly record struct CellRect(int I0, int J0, int I1, int J1)
{
    public int Width => I1 - I0;
    public int Depth => J1 - J0;
    public int Area => Width * Depth;
    public bool Contains(int i, int j) => i >= I0 && i < I1 && j >= J0 && j < J1;
    public CellRect Clip(int nx, int nz) => new(Math.Max(0, I0), Math.Max(0, J0), Math.Min(nx, I1), Math.Min(nz, J1));
    public bool IsEmpty => I1 <= I0 || J1 <= J0;
}

public enum PartKind : byte
{
    /// <summary>A main hall: an avenue, spoke or the spine.</summary>
    Avenue,
    /// <summary>A side hall: a street or branch.</summary>
    Hall,
    /// <summary>A big open hall where ways meet.</summary>
    Hub,
    /// <summary>A hall across a gap in the ground: brick deck and roof, open sides.</summary>
    Bridge,
    Room,
    /// <summary>A shaft through floors (see <see cref="Part.Floor"/> to <see cref="Part.LastFloor"/>).</summary>
    Core,
}

/// <summary>Placeholder district use, so district shapes show on maps.</summary>
public enum DistrictUse : byte { Residential, Admin, Military }

/// <summary>One piece of a ruin on one floor (a core spans several). A room's door opens from its cell (DoorI, DoorJ)
/// into the hall cell beside it on side (DoorDi, DoorDj).</summary>
public struct Part
{
    public CellRect Rect;
    public PartKind Kind;
    public byte Floor, LastFloor;
    public short District;
    public short DoorI, DoorJ;
    public sbyte DoorDi, DoorDj;
    /// <summary>For the entrance core: it rises to the surface.</summary>
    public bool Entrance;
}

/// <summary>A laid-out ruin: its parts, which district each cell is in and each district's use.</summary>
public sealed class RuinPlan
{
    public readonly RuinSite Site;
    public readonly string Model;
    public readonly List<Part> Parts = [];
    public int[] DistrictOf = [];
    public DistrictUse[] Uses = [];
    public TimeSpan SkeletonTime, RoomsTime;

    public RuinPlan(RuinSite site, string model) { Site = site; Model = model; }
}
