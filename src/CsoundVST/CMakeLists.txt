#-------------------------------------------------------------------------------
# Csound Visual VST3 plug-in (Windows x64)
#
#   cmake -S src/CsoundVST -B src/CsoundVST/build -G Ninja -DCMAKE_BUILD_TYPE=Release
#   cmake --build src/CsoundVST/build
#
# or simply: powershell -ExecutionPolicy Bypass -File tools\build_vst.ps1
#
# Produces build/VST3/<config>/CsoundVisual.vst3 (a bundle folder) with a private
# copy of Csound 7 under Contents/Resources/Csound7, plus the SDK's `validator`
# and our own `vsthost_test`.
#
# What this file sets up, in order:
#   1. VST3 SDK options (must be in the cache before add_subdirectory reads them).
#   2. The SDK itself (Steinberg vst3sdk, MIT, cloned into external/vst3sdk by
#      tools/build_vst.ps1), configured lean.
#   3. The Csound 7 installation whose binaries are bundled (CSOUND7_DIR).
#   4. A generated header, defaultcsd.h, that embeds resource/default_*.csd as
#      C++ raw string literals (the fallback orchestras of both plug-in classes).
#   5. The plug-in target `CsoundVisual` (one .vst3 module that exports two
#      classes, Instrument and FX, see source/factory.cpp) and a post-build step
#      that copies Csound 7 into the bundle.
#   6. The console test host `vsthost_test` (test/vsthost_test.cpp).
#
# Csound is NOT linked at build time: source/csoundapi.cpp loads csound7_64.dll
# at run time (LoadLibraryExW + GetProcAddress), so only its headers' types and
# the DLL copied into the bundle matter.
#-------------------------------------------------------------------------------
cmake_minimum_required(VERSION 3.25.0)

# --- lean VST3 SDK build: no VSTGUI, no examples (the validator is always built) ---
# These are FORCEd into the cache because the SDK declares them as options with
# other defaults; they must be set before add_subdirectory(vst3sdk) below.
# SMTG_USE_STATIC_CRT makes smtg_enable_vst3_sdk() add /MT (Release) or /MTd
# (Debug) as directory-wide compile options, so the SDK libraries AND our own
# targets use the static C runtime: the plug-in DLL then loads in any DAW without
# a Visual C++ redistributable, and it does not share CRT state (heap, locale,
# errno) with the host process. Note that the SDK only adds the flag for the
# Debug and Release configurations; CMAKE_MSVC_RUNTIME_LIBRARY below covers
# RelWithDebInfo and MinSizeRel too.
set(SMTG_ENABLE_VST3_PLUGIN_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VST3_HOSTING_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VSTGUI_SUPPORT OFF CACHE BOOL "" FORCE)
set(SMTG_CREATE_PLUGIN_LINK OFF CACHE BOOL "" FORCE)     # would need admin rights on Windows
set(SMTG_RUN_VST_VALIDATOR OFF CACHE BOOL "" FORCE)      # run explicitly after bundling Csound
set(SMTG_USE_STATIC_CRT ON CACHE BOOL "" FORCE)          # no VC++ runtime dependency in the DAW

# --- SDK location: a local clone, not a submodule of this repository ---
# tools/build_vst.ps1 clones it here on a fresh checkout; src/VstHost reuses the
# same clone. Fail early with the exact command if it is missing.
set(vst3sdk_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/external/vst3sdk")
if(NOT EXISTS "${vst3sdk_SOURCE_DIR}/CMakeLists.txt")
    message(FATAL_ERROR "VST3 SDK not found. Run:\n  git clone --depth 1 --recursive https://github.com/steinbergmedia/vst3sdk.git src/CsoundVST/external/vst3sdk")
endif()

# --- project ---
# The version has ONE source of truth: the CSV_VERSION_MAJOR/MINOR/PATCH/BUILD
# defines in source/version.h (that header is what the factory reports to hosts
# and what the .rc version resource uses). It is read here, so project(VERSION)
# can no longer drift from it (it used to be a second hand-maintained copy).
# Release: edit version.h only. CMAKE_CONFIGURE_DEPENDS re-runs the configure
# step when the header changes, so PROJECT_VERSION follows automatically.
set(CSV_VERSION_PARTS "")
foreach(part MAJOR MINOR PATCH BUILD)
    file(STRINGS "${CMAKE_CURRENT_SOURCE_DIR}/source/version.h" _csv_line
         REGEX "^#define CSV_VERSION_${part} +[0-9]+")
    if(NOT _csv_line MATCHES "CSV_VERSION_${part} +([0-9]+)")
        message(FATAL_ERROR "CSV_VERSION_${part} not found in source/version.h")
    endif()
    list(APPEND CSV_VERSION_PARTS "${CMAKE_MATCH_1}")
endforeach()
list(JOIN CSV_VERSION_PARTS "." CSV_VERSION)
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
    "${CMAKE_CURRENT_SOURCE_DIR}/source/version.h")

project(CsoundVisualVST
    VERSION ${CSV_VERSION}
    DESCRIPTION "Csound 7 inside a VST3 host"
    LANGUAGES C CXX
)

# Static CRT for EVERY configuration and every target in this tree (the SDK's
# libraries, the plug-in and vsthost_test): /MT, or /MTd for Debug.
# SMTG_USE_STATIC_CRT alone only adds /MT for Release and /MTd for Debug, so a
# RelWithDebInfo or MinSizeRel build silently fell back to CMake's default
# dynamic CRT (/MD) - a plug-in that then needs the VC++ redistributable inside
# the DAW. CMAKE_MSVC_RUNTIME_LIBRARY (honoured since policy CMP0091, which
# cmake_minimum_required 3.25 enables) covers all four; the SDK's own /MT flags
# for Debug/Release agree with it, so there is no conflict. Same setting as
# src/VstHost/CMakeLists.txt. Must be set before the targets are created
# (add_subdirectory below).
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# C++20 for the whole tree (the engine uses std::atomic_ref for lock-free stores
# into Csound's channel memory).
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# --- the VST3 SDK ---
# Pulls in the SDK targets (base, pluginterfaces, sdk, sdk_hosting, validator...)
# built into build/vst3sdk. smtg_enable_vst3_sdk() applies the SDK's global
# compiler settings (including the static CRT chosen above) and makes the
# smtg_add_vst3plugin() helper used below available.
add_subdirectory(${vst3sdk_SOURCE_DIR} ${PROJECT_BINARY_DIR}/vst3sdk)
smtg_enable_vst3_sdk()

# --- where the Csound 7 binaries are copied from ---
# Default: <repo>/@Source/Csound7 (a local Csound 7 build, not part of the
# repository). Override with -DCSOUND7_DIR=<folder containing bin/csound7_64.dll,
# plugins64/ and samples/>. The folder is only read, never modified.
get_filename_component(CSV_PROJECT_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../.." ABSOLUTE)
set(CSOUND7_DIR "${CSV_PROJECT_ROOT}/@Source/Csound7" CACHE PATH "Csound 7 installation to bundle")
if(NOT EXISTS "${CSOUND7_DIR}/bin/csound7_64.dll")
    message(FATAL_ERROR "csound7_64.dll not found under CSOUND7_DIR=${CSOUND7_DIR}")
endif()
# (Checked at configure time so a missing Csound fails before a long build.)

# --- embed the default orchestras as C++ raw string literals ---
# Writes build/generated/defaultcsd.h with kDefaultInstrumentCsd and
# kDefaultFxCsd, which plugin.cpp uses for a new instance and as the fallback
# when a saved state carries no CSD text. The CSDs are pasted verbatim between
# R"CSD( and )CSD", so when editing resource/default_*.csd keep in mind:
#   * the text must not contain the sequence )CSD" (it would end the literal);
#   * file(CONFIGURE ... @ONLY) replaces @VAR@ sequences, so avoid them;
#   * MSVC limits a single string literal to about 16 KB (error C2026), so keep
#     each default orchestra small.
# CMAKE_CONFIGURE_DEPENDS re-runs the configure step (and so regenerates the
# header) whenever either .csd file changes.
set(CSV_GEN_DIR "${PROJECT_BINARY_DIR}/generated")
file(READ "${CMAKE_CURRENT_SOURCE_DIR}/resource/default_instrument.csd" CSV_INSTR_CSD)
file(READ "${CMAKE_CURRENT_SOURCE_DIR}/resource/default_fx.csd" CSV_FX_CSD)
set(CSV_DEFAULT_HEADER "// Generated by CMakeLists.txt from resource/default_*.csd - do not edit.\n#pragma once\nnamespace CsoundVisual {\nstatic const char* const kDefaultInstrumentCsd = R\"CSD(${CSV_INSTR_CSD})CSD\";\nstatic const char* const kDefaultFxCsd = R\"CSD(${CSV_FX_CSD})CSD\";\n}\n")
file(CONFIGURE OUTPUT "${CSV_GEN_DIR}/defaultcsd.h" CONTENT "${CSV_DEFAULT_HEADER}" @ONLY)
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
    "${CMAKE_CURRENT_SOURCE_DIR}/resource/default_instrument.csd"
    "${CMAKE_CURRENT_SOURCE_DIR}/resource/default_fx.csd")

#-------------------------------------------------------------------------------
# The plug-in
#
# smtg_add_vst3plugin() (VST3 SDK) creates a MODULE library named CsoundVisual,
# gives it the .vst3 extension, lays it out as a bundle folder
# (VST3/<config>/CsoundVisual.vst3/Contents/x86_64-win/CsoundVisual.vst3),
# generates Contents/Resources/moduleinfo.json and adds the folder icon.
# vstsinglecomponenteffect.cpp is listed explicitly because the SDK's `sdk`
# library does not contain SingleComponentEffect (the base class of
# CsoundVisualPlugin, see source/plugin.h). resource/csoundvisual.rc is the
# Windows version resource (it includes source/version.h); the generated
# defaultcsd.h is listed so IDEs show it.
#-------------------------------------------------------------------------------
set(target CsoundVisual)
smtg_add_vst3plugin(${target}
    ${vst3sdk_SOURCE_DIR}/public.sdk/source/vst/vstsinglecomponenteffect.cpp
    source/csoundapi.cpp
    source/csoundapi.h
    source/csoundengine.cpp
    source/csoundengine.h
    source/factory.cpp
    source/ipcserver.cpp
    source/ipcserver.h
    source/messagelog.h
    source/minijson.cpp
    source/minijson.h
    source/plugin.cpp
    source/plugin.h
    source/version.h
    resource/csoundvisual.rc
    "${CSV_GEN_DIR}/defaultcsd.h"
)
# source/ for our headers, build/generated for defaultcsd.h.
target_include_directories(${target} PRIVATE source "${CSV_GEN_DIR}")
# UNICODE/_UNICODE: the W versions of the Win32 API. WIN32_LEAN_AND_MEAN: smaller
# windows.h. NOMINMAX: keep windows.h from defining min/max macros, which break
# std::min / std::max.
target_compile_definitions(${target} PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
# /utf-8: sources (and the embedded CSDs) are UTF-8 without BOM. /W3: the usual
# MSVC warning level.
target_compile_options(${target} PRIVATE /utf-8 /W3)
# sdk: the SDK's plug-in helpers (+ base and pluginterfaces transitively).
# user32: the hidden message-only window used for UI-thread marshalling
# (plugin.cpp). shell32/ole32: known-folder lookup of %LOCALAPPDATA% for the
# descriptor files (SHGetKnownFolderPath / CoTaskMemFree in ipcserver.cpp).
# No Csound import library: csound7_64.dll is loaded at run time.
target_link_libraries(${target} PRIVATE sdk shell32 ole32 user32)

# Bundle Csound 7 inside the plug-in: Contents/Resources/Csound7/{bin,plugins64,samples}
# A private copy keeps the plug-in independent of any Csound installed on the
# machine (and of PATH): csoundapi.cpp first looks for csound7_64.dll relative to
# the plug-in DLL's own folder (Contents/x86_64-win/../Resources/Csound7/bin),
# which is exactly where CSV_RES points. plugins64 holds
# the opcode libraries Csound loads at start; samples holds HRTF data and the
# default SoundFont used by some opcodes. Runs after every link of the target.
set(CSV_RES "$<TARGET_FILE_DIR:${target}>/../Resources/Csound7")
add_custom_command(TARGET ${target} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${CSV_RES}/bin"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different "${CSOUND7_DIR}/bin/csound7_64.dll" "${CSV_RES}/bin/"
    COMMAND ${CMAKE_COMMAND} -E copy_directory "${CSOUND7_DIR}/plugins64" "${CSV_RES}/plugins64"
    COMMAND ${CMAKE_COMMAND} -E copy_directory "${CSOUND7_DIR}/samples" "${CSV_RES}/samples"
    COMMENT "[CsoundVisual] Bundling Csound 7 into CsoundVisual.vst3/Contents/Resources/Csound7"
    VERBATIM
)

#-------------------------------------------------------------------------------
# Console test host
#
# vsthost_test loads the built bundle like a DAW would (VST3::Hosting::Module),
# renders audio, checks parameters, state round trips and the IPC pipe (it is a
# pipe client too, hence minijson.cpp). Run by tools/build_vst.ps1 unless
# -SkipTests. It compiles two SDK files directly:
#   module_win32.cpp - Windows module loading (LoadLibrary, bundle layout,
#                      InitDll/ExitDll); not part of sdk_hosting's sources.
#   memorystream.cpp - MemoryStream, the in-memory IBStream used for state blobs.
# It does not link the plug-in: add_dependencies only makes sure the bundle is
# built (and Csound copied into it) before the test executable.
#-------------------------------------------------------------------------------
add_executable(vsthost_test
    test/vsthost_test.cpp
    ${vst3sdk_SOURCE_DIR}/public.sdk/source/vst/hosting/module_win32.cpp
    ${vst3sdk_SOURCE_DIR}/public.sdk/source/common/memorystream.cpp
    source/minijson.cpp
)
target_include_directories(vsthost_test PRIVATE source)
# _USE_STD_VECTOR_ALGORITHMS=0: makes the STL use its plain algorithm code instead
# of the vectorised __std_* helpers that live in the STL library; a workaround for
# unresolved __std_* symbols when the STL headers and the linked STL library come
# from different toolset versions (src/VstHost uses the same setting).
target_compile_definitions(vsthost_test PRIVATE UNICODE _UNICODE NOMINMAX _USE_STD_VECTOR_ALGORITHMS=0)
target_compile_options(vsthost_test PRIVATE /utf-8)
# sdk_hosting: the SDK's host-side helpers (plug-in provider, host application,
# parameter changes, event list...).
target_link_libraries(vsthost_test PRIVATE sdk_hosting shell32 ole32)
add_dependencies(vsthost_test ${target})
