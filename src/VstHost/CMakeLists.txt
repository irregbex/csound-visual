#-------------------------------------------------------------------------------
# CsoundVisualVstHost.dll - VST3 hosting bridge for Csound Visual (Windows x64)
#
#   cmake -S src/VstHost -B src/VstHost/build -G Ninja -DCMAKE_BUILD_TYPE=Release
#   cmake --build src/VstHost/build --target CsoundVisualVstHost
#
# or simply: powershell -ExecutionPolicy Bypass -File tools\build_vsthost.ps1
#
# Reuses the VST3 SDK already downloaded for the plug-in (src/CsoundVST/external/vst3sdk).
# Static CRT, so the DLL only depends on Windows system libraries.
#
# Outputs (in build/bin/):
#   CsoundVisualVstHost.dll   - the bridge; build_vsthost.ps1 copies it to
#                               src/CsoundVisual/native/, and the app's build copies it next
#                               to CsoundVisual.exe. The app works without it (no VST3 host).
#   vsthost_native_test.exe   - console test of the DLL without .NET (see test/).
#-------------------------------------------------------------------------------
# 3.25 is the minimum the VST3 SDK's own CMakeLists.txt requires.
cmake_minimum_required(VERSION 3.25.0)

# VST3 SDK options, forced into the cache BEFORE add_subdirectory(vst3sdk) reads them.
# We only need the hosting helpers: no example plug-ins/hosts, no VSTGUI, no symlink of
# built plug-ins into the system VST3 folder, no validator run. SMTG_USE_STATIC_CRT makes the
# SDK's own targets use /MT too, matching this DLL (mixing CRTs would not link).
set(SMTG_ENABLE_VST3_PLUGIN_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VST3_HOSTING_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VSTGUI_SUPPORT OFF CACHE BOOL "" FORCE)
set(SMTG_CREATE_PLUGIN_LINK OFF CACHE BOOL "" FORCE)
set(SMTG_RUN_VST_VALIDATOR OFF CACHE BOOL "" FORCE)
set(SMTG_USE_STATIC_CRT ON CACHE BOOL "" FORCE)

# SDK location: the copy cloned by tools/build_vst.ps1 for our own Csound VST3 plug-in
# (src/CsoundVST). Sharing it avoids a second multi-hundred-MB checkout. The SDK is MIT
# licensed (since 3.8), compatible with this project's GPL-3.0.
set(vst3sdk_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../CsoundVST/external/vst3sdk")
if(NOT EXISTS "${vst3sdk_SOURCE_DIR}/CMakeLists.txt")
    message(FATAL_ERROR "VST3 SDK not found at ${vst3sdk_SOURCE_DIR} (run tools/build_vst.ps1 once, it clones it).")
endif()

project(CsoundVisualVstHost VERSION 1.0.0 LANGUAGES C CXX)

# C++20 for this tree (vsthost.cpp itself needs at least C++17: if-with-initializer,
# std::clamp, std::string::data() returning a writable pointer).
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT for every target in this tree: /MT in Release, /MTd in Debug. The DLL then needs
# no Visual C++ redistributable on the user's machine.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Pull in the SDK's targets (base, pluginterfaces, sdk_common, sdk_hosting...), built into
# build/vst3sdk. smtg_enable_vst3_sdk() applies the SDK's global compiler settings.
add_subdirectory(${vst3sdk_SOURCE_DIR} ${PROJECT_BINARY_DIR}/vst3sdk)
smtg_enable_vst3_sdk()

# The bridge DLL. Besides our source it compiles two SDK files directly:
#   module_win32.cpp  - VST3::Hosting::Module for Windows (LoadLibrary, bundle layout,
#                       InitDll/ExitDll); not part of sdk_hosting's portable sources.
#   memorystream.cpp  - MemoryStream, the in-memory IBStream used for state blobs.
add_library(CsoundVisualVstHost SHARED
    source/vsthost.cpp
    source/vsthost.h
    ${vst3sdk_SOURCE_DIR}/public.sdk/source/vst/hosting/module_win32.cpp
    ${vst3sdk_SOURCE_DIR}/public.sdk/source/common/memorystream.cpp
)
# UNICODE/_UNICODE: W versions of the Win32 API. NOMINMAX: keep windows.h from defining
# min/max macros (they break std::min/std::max). WIN32_LEAN_AND_MEAN: smaller windows.h.
# _USE_STD_VECTOR_ALGORITHMS=0: makes the STL use its plain algorithm code instead of the
# vectorised __std_* helpers that live in the STL's import/static library; a workaround for
# unresolved __std_* symbols when the STL headers and the linked STL library come from
# different toolset versions (the same setting is used by src/CsoundVST's vsthost_test).
target_compile_definitions(CsoundVisualVstHost PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN _USE_STD_VECTOR_ALGORITHMS=0)
# /utf-8: sources are UTF-8 (no BOM). /W3: the usual MSVC warning level.
target_compile_options(CsoundVisualVstHost PRIVATE /utf-8 /W3)
# sdk_hosting: the SDK's host helpers (+ base/pluginterfaces transitively). shell32/ole32 are
# used by module_win32.cpp (SHGetKnownFolderPath / CoTaskMemFree to find the system VST3
# folders), user32/gdi32 by the editor window and
# the window capture.
target_link_libraries(CsoundVisualVstHost PRIVATE sdk_hosting shell32 ole32 user32 gdi32)
# Single-config generator (Ninja): outputs land directly in build/bin/. The runtime property
# repeats the global setting explicitly for this target.
set_target_properties(CsoundVisualVstHost PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${PROJECT_BINARY_DIR}/bin"
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Console test without .NET: vsthost_native_test <module.vst3> [class id]
# Links against the DLL's import library and is placed next to it so it finds it at run time.
add_executable(vsthost_native_test test/vsthost_native_test.cpp)
target_compile_definitions(vsthost_native_test PRIVATE UNICODE _UNICODE NOMINMAX)
target_link_libraries(vsthost_native_test PRIVATE CsoundVisualVstHost)
set_target_properties(vsthost_native_test PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${PROJECT_BINARY_DIR}/bin"
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
