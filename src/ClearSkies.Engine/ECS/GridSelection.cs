using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Tracks which single DynamicGrid root entity currently carries <see cref="SelectedGridComponent"/>,
/// enforcing the "only one at a time" invariant. Constructed once (see Program.cs) and shared by every
/// call site that can change grid selection: DynamicGridFactory (on spawn) and PlayerInputSystem (on a
/// successful block break/place against a grid).
/// </summary>
public sealed class GridSelection
{
    private readonly EntitySet _selected;
    private readonly List<Entity> _scratch = new();

    public GridSelection(World world)
    {
        _selected = world.GetEntities()
            .With<DynamicGridComponent>()
            .With<SelectedGridComponent>()
            .AsSet();
    }

    /// <summary>Clears the flag from whichever grid root currently holds it (if any) and sets it on
    /// <paramref name="gridRoot"/>.</summary>
    public void Select(Entity gridRoot)
    {
        // Snapshot first: removing a component from an entity that is itself part of this same live
        // EntitySet while iterating it directly is not safe.
        _scratch.Clear();
        foreach (ref readonly Entity e in _selected.GetEntities())
            _scratch.Add(e);

        foreach (var e in _scratch)
            if (e != gridRoot) e.Remove<SelectedGridComponent>();

        if (!gridRoot.Has<SelectedGridComponent>())
            gridRoot.Set(new SelectedGridComponent());
    }
}
