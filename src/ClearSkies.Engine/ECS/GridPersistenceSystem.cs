using System;
using System.Collections.Generic;
using System.IO;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Debug panel for saving the currently Selected Grid's blocks to disk and loading a previously saved
/// grid back in, spawned in front of the player (same placement as the G-key spawn). All work happens
/// inside DrawDebugUi in response to button clicks — there is no continuous per-frame Update logic.
/// </summary>
public sealed class GridPersistenceSystem : ISystem
{
    private readonly World _world;
    private readonly ChunkMeshSystem _meshSystem;
    private readonly PhysicsWorld _physics;
    private readonly GridSelection _selection;
    private readonly EntitySet _cameras;
    private readonly EntitySet _selectedGrid;
    private readonly string _savesDir;

    private string _saveName = "";
    private readonly List<string> _saveFiles = new();
    private string? _chosenFile;
    private string _status = "";

    public GridPersistenceSystem(World world, ChunkMeshSystem meshSystem, PhysicsWorld physics, GridSelection selection)
    {
        _world      = world;
        _meshSystem = meshSystem;
        _physics    = physics;
        _selection  = selection;
        _cameras      = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _selectedGrid = world.GetEntities().With<ChunkGrid>().With<SelectedGridComponent>().AsSet();

        _savesDir = Path.Combine(AppContext.BaseDirectory, "Saves", "Grids");
        Directory.CreateDirectory(_savesDir);
        RefreshSaveList();
    }

    public void Update(float dt) { } // all work is UI-button-driven; see DrawDebugUi.

    // ── debug UI ─────────────────────────────────────────────────────────────
    // Drawn as a section inside AirshipDebugPanel's combined "Airship" window, not its own panel.
    public void DrawDebugUi()
    {
        bool hasSelection = TryGetSelectedGrid(out _);
        ImGui.Text(hasSelection ? "Selected grid: yes" : "Selected grid: none (spawn or edit a grid first)");

        ImGui.InputText("Save name", ref _saveName, 64);

        ImGui.BeginDisabled(!hasSelection || string.IsNullOrWhiteSpace(_saveName));
        if (ImGui.Button("Save")) SaveSelected();
        ImGui.EndDisabled();

        ImGui.BeginDisabled(!hasSelection);
        if (ImGui.Button("Delete Selected Grid")) DeleteSelected();
        ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.Text("Saved grids:");
        foreach (var name in _saveFiles)
        {
            if (ImGui.Selectable(name, name == _chosenFile))
                _chosenFile = name;
        }

        ImGui.BeginDisabled(_chosenFile is null);
        if (ImGui.Button("Load")) LoadChosen();
        ImGui.EndDisabled();

        if (_status.Length > 0)
        {
            ImGui.Separator();
            ImGui.Text(_status);
        }
    }

    // ── actions ──────────────────────────────────────────────────────────────

    private bool TryGetSelectedGrid(out ChunkVolume grid)
    {
        foreach (ref readonly Entity e in _selectedGrid.GetEntities())
        {
            grid = e.Get<ChunkGrid>().Volume;
            return true;
        }
        grid = null!;
        return false;
    }

    private void RefreshSaveList()
    {
        _saveFiles.Clear();
        foreach (var path in Directory.EnumerateFiles(_savesDir, "*.grid"))
            _saveFiles.Add(Path.GetFileNameWithoutExtension(path));
        _saveFiles.Sort(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveSelected()
    {
        if (!TryGetSelectedGrid(out var grid)) { _status = "No grid selected."; return; }

        string safeName = Path.GetFileName(_saveName.Trim()); // defensive: strip any path separators
        if (safeName.Length == 0) { _status = "Enter a name first."; return; }

        string path = Path.Combine(_savesDir, safeName + ".grid");
        try
        {
            GridSerializer.Save(grid, path);
            _status = $"Saved '{safeName}'.";
            RefreshSaveList();
        }
        catch (IOException ex) { _status = $"Save failed: {ex.Message}"; }
    }

    private void DeleteSelected()
    {
        if (!TryGetSelectedGrid(out var grid)) { _status = "No grid selected."; return; }
        grid.Root.Dispose();
        _status = "Deleted selected grid.";
    }

    private void LoadChosen()
    {
        if (_chosenFile is null) return;
        string path = Path.Combine(_savesDir, _chosenFile + ".grid");

        try
        {
            var voxels = GridSerializer.Load(path);
            if (voxels.Count == 0) { _status = $"'{_chosenFile}' has no blocks; not spawned."; return; }

            if (!CameraUtil.TryGetActive(_cameras, out var camTransform))
            {
                _status = "No active camera to spawn in front of.";
                return;
            }

            var spawn = CameraUtil.SpawnPointInFrontOf(camTransform);
            DynamicGridFactory.SpawnFromVoxels(
                _world, _selection,
                new PhysVec(spawn.X, spawn.Y, spawn.Z), voxels);

            _status = $"Loaded '{_chosenFile}' ({voxels.Count} blocks).";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            _status = $"Load failed: {ex.Message}";
        }
    }
}
