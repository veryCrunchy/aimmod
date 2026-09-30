cmake_minimum_required(VERSION 3.22)
project(AimModSteamProbe LANGUAGES CXX)

# Steam networking feasibility probe (see in-game/docs/multiplayer.md).
#  - aimmod_steam_probe_harness: standalone exe, no UE4SS needed.
#  - AimModSteamProbe (AIMMOD_PROBE_BUILD_MOD=ON): separate UE4SS C++ mod,
#    built against a local, git-ignored RE-UE4SS checkout (same pin as
#    AimModCore). Build the Game__Shipping__Win64 configuration.
#  - AIMMOD_PROBE_STAGE2 (OFF): compiles the lobby/loopback second stage.
#    Even when compiled, it only runs with stage2_loopback=1 in config.txt.
option(AIMMOD_PROBE_BUILD_MOD "Build the UE4SS probe mod (requires a local RE-UE4SS checkout)" OFF)
option(AIMMOD_PROBE_STAGE2 "Compile the write stage (private lobby + loopback)" OFF)
set(AIMMOD_UE4SS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../external/RE-UE4SS" CACHE PATH "Local RE-UE4SS checkout (never committed)")

if(AIMMOD_PROBE_BUILD_MOD)
    if(NOT EXISTS "${AIMMOD_UE4SS_DIR}/CMakeLists.txt")
        message(FATAL_ERROR "RE-UE4SS not found at ${AIMMOD_UE4SS_DIR}. See in-game/docs/multiplayer.md (Probe).")
    endif()
    add_subdirectory("${AIMMOD_UE4SS_DIR}" RE-UE4SS)
endif()

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_library(aimmod_steam_probe STATIC src/Probe.cpp src/Stage2Loopback.cpp)
target_include_directories(aimmod_steam_probe PUBLIC src)
target_link_libraries(aimmod_steam_probe PUBLIC version)
if(MSVC)
    target_compile_options(aimmod_steam_probe PRIVATE /W4 /utf-8 /permissive-)
    target_compile_definitions(aimmod_steam_probe PUBLIC NOMINMAX WIN32_LEAN_AND_MEAN UNICODE _UNICODE)
endif()
if(AIMMOD_PROBE_STAGE2)
    target_compile_definitions(aimmod_steam_probe PRIVATE AIMMOD_PROBE_STAGE2=1)
endif()

add_executable(aimmod_steam_probe_harness harness/main.cpp)
target_link_libraries(aimmod_steam_probe_harness PRIVATE aimmod_steam_probe)
if(MSVC)
    target_compile_options(aimmod_steam_probe_harness PRIVATE /W4 /utf-8 /permissive-)
endif()

if(AIMMOD_PROBE_BUILD_MOD)
    add_library(AimModSteamProbe SHARED mod/dllmain.cpp)
    target_link_libraries(AimModSteamProbe PRIVATE aimmod_steam_probe)
    target_link_libraries(AimModSteamProbe PUBLIC UE4SS)
    # UE4SS loads Mods/<Name>/dlls/main.dll.
    set_target_properties(AimModSteamProbe PROPERTIES OUTPUT_NAME "main")
    if(MSVC)
        target_compile_options(AimModSteamProbe PRIVATE /utf-8)
    endif()
endif()
