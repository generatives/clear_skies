#!/usr/bin/env bash
# Rebuilds the prebuilt Clay libraries in bin/: linux-x64 with gcc, win-x64 with the MinGW cross compiler
# (Ubuntu: apt install gcc-mingw-w64-x86-64) and win-arm64 with clang and lld (Ubuntu: apt install clang lld llvm).
# The win-arm64 DLL has no C runtime of its own: it imports the few functions it needs from msvcrt.dll, which every
# Windows install has, through an import library made on the fly. It is compiled for the MinGW (GNU) ABI like the
# win-x64 build, not MSVC: clang's MSVC mode ignores clay.h's __attribute__((__packed__)) on enums, making them 4
# bytes instead of 1 and moving the struct fields the C# layout check verifies. macOS isn't cross-compiled here; build it there with CMake (see
# README.md) as one universal libclay.dylib in bin/osx.
set -euo pipefail
cd "$(dirname "$0")"

mkdir -p bin/linux-x64 bin/win-x64 bin/win-arm64
gcc -std=c99 -O2 -shared -fPIC -fvisibility=hidden -s -o bin/linux-x64/libclay.so clay_shim.c
x86_64-w64-mingw32-gcc -std=c99 -O2 -shared -static-libgcc -s -o bin/win-x64/clay.dll clay_shim.c

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
printf 'LIBRARY msvcrt.dll\nEXPORTS\nmalloc\nfree\nmemcpy\nmemset\nmemmove\nmemcmp\n' > "$tmp/msvcrt.def"
llvm-dlltool -m arm64 -d "$tmp/msvcrt.def" -l "$tmp/msvcrt.lib"
clang --target=aarch64-w64-windows-gnu -ffreestanding -std=c99 -O2 -c clay_shim.c -o "$tmp/clay_shim.obj"
lld-link /dll /noentry /noimplib /nodefaultlib /machine:arm64 /out:bin/win-arm64/clay.dll "$tmp/clay_shim.obj" "$tmp/msvcrt.lib"
echo "Built bin/linux-x64/libclay.so, bin/win-x64/clay.dll and bin/win-arm64/clay.dll"
