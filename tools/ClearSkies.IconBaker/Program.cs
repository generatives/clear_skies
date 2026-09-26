using System.Globalization;
using ClearSkies.Engine.Rendering.Gltf;
using ClearSkies.Engine.Voxels;
using ClearSkies.IconBaker;
using StbImageWriteSharp;

// Bakes inventory icons for model blocks (BlockDef.Model): each block's glTF (from the repo's blockbench folder, the
// same files the game links in as Resources/Models) is drawn at a fixed 3/4 angle by IconRasterizer and saved as the
// PNG its BlockDef.IconTexture names, in src/ClearSkies.Game/Resources/Icons. Commit the results; rerun after changing
// a model or adding a model block.
//
//   dotnet run --project tools/ClearSkies.IconBaker [-- options]
//
// Options:
//   --block <name>     only this block (by BlockDef.Name, case-insensitive); repeatable
//   --size <px>        icon size (default 32)
//   --supersample <n>  anti-aliasing: samples per pixel along each axis (default 4)
//   --yaw <degrees>    turn about the vertical axis (default 225: the model's front and right side)
//   --pitch <degrees>  tilt down onto the top (default 30)
//   --no-outline       skip the dark one-pixel outline
//   --models <dir>     where BlockDef.Model paths are relative to (default <repo>/blockbench)
//   --out <dir>        where icons are written (default <repo>/src/ClearSkies.Game/Resources/Icons)

string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
float FloatArg(string name, float fallback) =>
    Arg(name) is { } v ? float.Parse(v, CultureInfo.InvariantCulture) : fallback;

string? repo = FindRepoRoot();
string RepoPath(params string[] parts) => Path.Combine(parts.Prepend(repo ?? throw new DirectoryNotFoundException(
    "Couldn't find the repository (ClearSkies.sln) above the working directory; pass --models and --out.")).ToArray());
string modelsDir = Arg("--models") ?? RepoPath("blockbench");
string outDir = Arg("--out") ?? RepoPath("src", "ClearSkies.Game", "Resources", "Icons");
var only = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--block") only.Add(args[i + 1]);

var rasterizer = new IconRasterizer
{
    Size = (int)FloatArg("--size", 32),
    Supersample = (int)FloatArg("--supersample", 4),
    YawDegrees = FloatArg("--yaw", 225f),
    PitchDegrees = FloatArg("--pitch", 30f),
    Outline = !args.Contains("--no-outline"),
};

Directory.CreateDirectory(outDir);
int baked = 0, failed = 0;
foreach (var id in Enum.GetValues<BlockId>().Distinct())
{
    ref readonly var block = ref BlockRegistry.Get(id);
    if (block.Model is null || (only.Count > 0 && !only.Contains(block.Name))) continue;

    string iconName = block.IconTexture ?? Path.GetFileNameWithoutExtension(block.Model) + ".png";
    if (block.IconTexture is null)
        Console.WriteLine($"  note: {block.Name} has no IconTexture; writing {iconName} — set IconTexture = \"{iconName}\" in BlockRegistry to use it.");
    try
    {
        var model = GltfLoader.Load(Path.Combine(modelsDir, block.Model));
        byte[] rgba = rasterizer.Render(model);
        string path = Path.Combine(outDir, iconName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path))
            new ImageWriter().WritePng(rgba, rasterizer.Size, rasterizer.Size, ColorComponents.RedGreenBlueAlpha, file);
        Console.WriteLine($"{block.Name,-16} {block.Model} -> {(repo is null ? path : Path.GetRelativePath(repo, path))}");
        baked++;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"{block.Name,-16} FAILED: {e.Message}");
        failed++;
    }
}
Console.WriteLine($"Baked {baked} icon(s) at {rasterizer.Size}x{rasterizer.Size}" + (failed > 0 ? $", {failed} failed." : "."));
return failed > 0 ? 1 : 0;

// The folder holding ClearSkies.sln, searched upwards from the working directory and then from the tool's own folder.
static string? FindRepoRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ClearSkies.sln")))
                return dir.FullName;
    return null;
}
