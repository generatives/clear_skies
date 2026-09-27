#!/usr/bin/env bash
# Rebuilds the prebuilt Clay libraries in bin/: linux-x64 with gcc and win-x64 with the MinGW cross compiler
# (Ubuntu: apt install gcc-mingw-w64-x86-64). macOS isn't cross-compiled here; build it there with CMake (see
# README.md) as one universal libclay.dylib in bin/osx.
set -euo pipefail
cd "$(dirname "$0")"

mkdir -p bin/linux-x64 bin/win-x64
gcc -std=c99 -O2 -shared -fPIC -fvisibility=hidden -s -o bin/linux-x64/libclay.so clay_shim.c
x86_64-w64-mingw32-gcc -std=c99 -O2 -shared -static-libgcc -s -o bin/win-x64/clay.dll clay_shim.c
echo "Built bin/linux-x64/libclay.so and bin/win-x64/clay.dll"
