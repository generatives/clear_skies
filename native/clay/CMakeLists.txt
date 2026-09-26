# Builds Clay (clay.h + clay_shim.c) as the shared library the engine's C# bindings load ("clay": clay.dll,
# libclay.so or libclay.dylib). Prebuilt copies for each platform live in bin/<rid>/ and are what the engine's
# csproj copies to the output folder; rebuild them with build.sh (Linux, and Windows via MinGW) or, on Windows or
# macOS, with:
#
#   cmake -S native/clay -B native/clay/build -DCMAKE_BUILD_TYPE=Release
#   cmake --build native/clay/build --config Release
#
# then copy the library from native/clay/build (or build/Release) into bin/<rid>/.
cmake_minimum_required(VERSION 3.16)
project(clay C)

add_library(clay SHARED clay_shim.c)
set_target_properties(clay PROPERTIES
    C_STANDARD 99
    C_VISIBILITY_PRESET hidden)  # only the ClayShim_* functions (marked for export) are visible
if(WIN32)
    set_target_properties(clay PROPERTIES PREFIX "")  # clay.dll, also under MinGW (which would say libclay.dll)
endif()
if(MSVC)
    target_compile_options(clay PRIVATE /O2)
else()
    target_compile_options(clay PRIVATE -O2)
endif()
