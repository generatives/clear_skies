# Clay (native)

[Clay](https://github.com/nicbarker/clay) is the immediate-mode layout library behind the game UI
(`src/ClearSkies.Engine/Ui`). It is a single C header, built here into a small shared library that the C# bindings
load as `clay` (`clay.dll`, `libclay.so` or `libclay.dylib`).

- `clay.h` is vendored unmodified from upstream `main` at commit
  [`e6cc369`](https://github.com/nicbarker/clay/commit/e6cc36941ab2af5d81107617039d6f527a1c660b) (2026-05-20;
  the header still says `VERSION: 0.14`, but it is ~100 commits of fixes and the transition API past the v0.14 tag).
  License: `CLAY_LICENSE.md` (zlib).
- `clay_shim.c` compiles Clay and exports the `ClayShim_*` functions the bindings call. They take pointers and
  primitives only, never structs by value, and Clay's callbacks go through C trampolines that do the same, so the
  bindings don't depend on each platform's struct-passing convention. `ClayShim_LayoutInfo` reports the size and
  field offsets of every shared struct; the C# side compares them against its own mirrors at startup
  (`ClayLayoutCheck`), so a mismatch fails loudly instead of corrupting memory.
- `bin/<rid>/` holds the prebuilt libraries. `ClearSkies.Engine.csproj` copies whichever exist to the output folder,
  except that `win-x64` and `win-arm64` share the name `clay.dll`: `win-arm64` is used when building for it
  (`-r win-arm64`, or no runtime identifier on an ARM64 machine), `win-x64` otherwise.

## Rebuilding

After changing `clay_shim.c` or updating `clay.h`:

```sh
native/clay/build.sh   # Linux: builds bin/linux-x64/libclay.so, bin/win-x64/clay.dll (MinGW), bin/win-arm64/clay.dll (clang)
```

Or with CMake on any platform (Windows with Visual Studio's C compiler, macOS with Xcode's):

```sh
cmake -S native/clay -B native/clay/build -DCMAKE_BUILD_TYPE=Release
cmake --build native/clay/build --config Release
```

and copy the library into `bin/<rid>/` (`win-x64`, `linux-x64`, ...). Every library in `bin` is copied to the same
output folder, so there can be only one per file name (the two Windows DLLs aside, see above): on macOS, build one universal `libclay.dylib`
(`-DCMAKE_OSX_ARCHITECTURES="arm64;x86_64"`) into `bin/osx`.

## Updating Clay

1. Replace `clay.h` with the new upstream version and update the commit above.
2. Diff the struct declarations near the top of `clay.h` against the mirrors in
   `src/ClearSkies.Engine/Ui/Clay/ClayTypes.cs` and update them.
3. Rebuild the libraries and run the game: the layout check names any struct whose size or field offsets no longer
   match. If a shim signature changed, bump `CLAY_SHIM_VERSION` here and `ClayNative.ShimVersion` in C#.
