#-------------------------------------------------------------------------------
# CsoundVisualLink.dll: Ableton Link for Csound Visual (Windows x64)
#
#   powershell -ExecutionPolicy Bypass -File tools\build_link.ps1
#
# or by hand:
#   cmake -S src/AbletonLink -B src/AbletonLink/build -G "Visual Studio 17 2022" -A x64
#   cmake --build src/AbletonLink/build --config Release
#
# Builds:
#   CsoundVisualLink.dll  - Ableton Link 3.1.5 + its official C extension (abl_link),
#                           exported as a plain C API (see CsoundVisualLink.def).
#                           Static CRT: no Visual C++ runtime needed next to the app.
#   link_peer_test.exe    - a second Link peer for tests (prints the session timeline
#                           on a common clock grid so two processes can be compared).
#
# Sources are NOT fetched by CMake: tools\build_link.ps1 downloads the tagged Link
# release and the asio-standalone commit Link references into external\.
#
# Licence: Ableton Link is GPL-2.0-or-later (see external/link/LICENSE.md). Csound
# Visual itself is GPL-3.0, which "or later" makes compatible: the app may be
# distributed with this DLL under the GPL-3.0 (a proprietary build would need a
# separate licence from Ableton).
#
# Why a separate DLL with a C API: Link is a header-only C++ library built on asio.
# Ableton's official abl_link extension wraps it in a plain C API (opaque handles,
# callbacks, int64 microsecond times) that .NET can call through function pointers
# (src/CsoundVisual/Integration/AbletonLinkNative.cs, used only by LinkSession.cs).
# The DLL is optional: the app loads it with NativeLibrary.TryLoad and simply disables
# Link when it is missing.
#-------------------------------------------------------------------------------
cmake_minimum_required(VERSION 3.20)
# The project version mirrors the Link release that is built.
project(CsoundVisualLink VERSION 3.1.5 LANGUAGES C CXX)

# Source locations (overridable on the command line, e.g. -DLINK_DIR=...). The defaults are
# where tools/build_link.ps1 extracts the downloads. Fail early with a helpful message.
set(LINK_DIR "${CMAKE_CURRENT_SOURCE_DIR}/external/link" CACHE PATH "Ableton Link source tree")
set(ASIO_DIR "${CMAKE_CURRENT_SOURCE_DIR}/external/asio" CACHE PATH "asio-standalone source tree")
if(NOT EXISTS "${LINK_DIR}/include/ableton/Link.hpp")
    message(FATAL_ERROR "Ableton Link not found in ${LINK_DIR}. Run tools\\build_link.ps1 (it downloads it).")
endif()
if(NOT EXISTS "${ASIO_DIR}/asio/include/asio.hpp")
    message(FATAL_ERROR "asio-standalone not found in ${ASIO_DIR}. Run tools\\build_link.ps1 (it downloads it).")
endif()

# Link 3.x requires C++17.
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT (/MT): the DLL only depends on Windows system libraries.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# --- the Link library itself, header-only, configured like ConfigureAbletonLink.cmake ---
# An INTERFACE target carries no code, only usage requirements (include paths, defines,
# libraries) that every target linking it inherits. We replicate what Link's own
# AbletonLinkConfig.cmake / ConfigureAbletonLink.cmake would set, without using Link's CMake
# (which expects git submodules):
#   LINK_PLATFORM_WINDOWS   selects Link's Windows platform layer (clock, network); this is
#                           what ConfigureAbletonLink.cmake sets on WIN32.
#   _WIN32_WINNT            minimum Windows API level asio compiles against.
#   _SCL/_CRT_SECURE_NO_WARNINGS silence MSVC "unsafe function" warnings in asio/Link.
#   ws2_32 (Winsock: asio's UDP sockets) and iphlpapi (enumerating network interfaces for
#   multicast peer discovery) are Link's system dependencies on Windows (Link also pulls
#   them with #pragma comment; listing them here makes the dependency explicit). winmm is
#   not used by Link itself; it provides timeBeginPeriod for link_peer_test.
add_library(AbletonLinkHeaders INTERFACE)
target_include_directories(AbletonLinkHeaders INTERFACE
    "${LINK_DIR}/include"
    "${ASIO_DIR}/asio/include")
target_compile_definitions(AbletonLinkHeaders INTERFACE
    LINK_PLATFORM_WINDOWS=1
    _WIN32_WINNT=0x0601          # asio: Windows 7+ APIs
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _SCL_SECURE_NO_WARNINGS
    _CRT_SECURE_NO_WARNINGS)
target_link_libraries(AbletonLinkHeaders INTERFACE ws2_32 iphlpapi winmm)

# --- the C API DLL ---
# The only C++ source is Ableton's abl_link.cpp (unmodified); the .def file lists the
# functions to export, because abl_link.h declares them without __declspec(dllexport).
# /bigobj: asio + Link templates exceed the default object-file section limit.
# /EHsc: standard C++ exception handling (Link and asio use exceptions internally).
# abl_link.h is a PUBLIC include so link_peer_test (which links this DLL) sees it.
add_library(CsoundVisualLink SHARED
    "${LINK_DIR}/extensions/abl_link/src/abl_link.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/CsoundVisualLink.def")
target_include_directories(CsoundVisualLink PUBLIC "${LINK_DIR}/extensions/abl_link/include")
target_link_libraries(CsoundVisualLink PRIVATE AbletonLinkHeaders)
target_compile_options(CsoundVisualLink PRIVATE /W3 /bigobj /EHsc)

# --- second peer for tests ---
# A plain C11 program using only abl_link.h, linked against the DLL's import library. Used by
# tools/build_link.ps1's two-peer smoke test and by the app's Link self-test as the "other"
# Link participant (see test/link_peer_test.c for its command line and output format).
add_executable(link_peer_test "${CMAKE_CURRENT_SOURCE_DIR}/test/link_peer_test.c")
target_link_libraries(link_peer_test PRIVATE CsoundVisualLink)
set_target_properties(link_peer_test PROPERTIES C_STANDARD 11)

# Everything lands in build/bin/<config>/.
# (The Visual Studio generator is multi-config, hence the per-config directories; the exe and
# the DLL end up side by side, so link_peer_test.exe finds CsoundVisualLink.dll at run time.)
set_target_properties(CsoundVisualLink link_peer_test PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/bin/Release"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_BINARY_DIR}/bin/Debug")
