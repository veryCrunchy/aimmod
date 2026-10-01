cmake_minimum_required(VERSION 3.22)
project(AimModSteam LANGUAGES CXX)

# AimModSteam: Steam lobbies, invites and relay-only P2P for AimMod
# multiplayer (see in-game/docs/multiplayer.md).
#  - aimmod_steam_core: Steam-independent codec/JSON (+ its test runner).
#  - aimmod_steam_bridge: bridge, pipe server and Steam access (no UE4SS).
#  - AimModSteam (AIMMOD_STEAM_BUILD_MOD=ON): the UE4SS C++ mod, built
#    against the local, git-ignored RE-UE4SS checkout (same pin as
#    AimModCore). Build the Game__Shipping__Win64 configuration.
option(AIMMOD_STEAM_BUILD_MOD "Build the UE4SS mod (requires a local RE-UE4SS checkout)" OFF)
set(AIMMOD_UE4SS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../external/RE-UE4SS" CACHE PATH "Local RE-UE4SS checkout (never committed)")

if(AIMMOD_STEAM_BUILD_MOD)
    if(NOT EXISTS "${AIMMOD_UE4SS_DIR}/CMakeLists.txt")
        message(FATAL_ERROR "RE-UE4SS not found at ${AIMMOD_UE4SS_DIR}. See in-game/docs/multiplayer.md.")
    endif()
    add_subdirectory("${AIMMOD_UE4SS_DIR}" RE-UE4SS)
endif()

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_library(aimmod_steam_core STATIC src/Codec.cpp src/Json.cpp src/PoseFile.cpp src/AvatarPath.cpp)
target_include_directories(aimmod_steam_core PUBLIC src)

add_library(aimmod_steam_bridge STATIC src/Bridge.cpp src/Pipe.cpp src/Steam.cpp)
# SteamAbi.hpp is shared with the probe.
target_include_directories(aimmod_steam_bridge PUBLIC src ../steam-probe/src)
target_link_libraries(aimmod_steam_bridge PUBLIC aimmod_steam_core advapi32)

foreach(target aimmod_steam_core aimmod_steam_bridge)
    if(MSVC)
        target_compile_options(${target} PRIVATE /W4 /utf-8 /permissive-)
        target_compile_definitions(${target} PUBLIC NOMINMAX WIN32_LEAN_AND_MEAN UNICODE _UNICODE)
    endif()
endforeach()

add_executable(aimmod_steam_tests tests/BridgeTests.cpp)
target_link_libraries(aimmod_steam_tests PRIVATE aimmod_steam_core)
if(MSVC)
    target_compile_options(aimmod_steam_tests PRIVATE /W4 /utf-8 /permissive-)
endif()

# Development harness (not shipped): the bridge outside the game.
add_executable(aimmod_steam_bridge_harness tools/BridgeHarness.cpp)
target_link_libraries(aimmod_steam_bridge_harness PRIVATE aimmod_steam_bridge)

if(AIMMOD_STEAM_BUILD_MOD)
    add_library(AimModSteam SHARED mod/dllmain.cpp mod/GameBindings.cpp mod/Ghosts.cpp)
    target_include_directories(AimModSteam PRIVATE mod)
    target_link_libraries(AimModSteam PRIVATE aimmod_steam_bridge)
    target_link_libraries(AimModSteam PUBLIC UE4SS)
    # UE4SS loads Mods/<Name>/dlls/main.dll.
    set_target_properties(AimModSteam PROPERTIES OUTPUT_NAME "main")
    if(MSVC)
        target_compile_options(AimModSteam PRIVATE /utf-8)
    endif()
endif()
