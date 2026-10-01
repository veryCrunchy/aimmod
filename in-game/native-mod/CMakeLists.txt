cmake_minimum_required(VERSION 3.22)
project(AimModNative LANGUAGES CXX)

# Two build shapes:
#  - AIMMOD_BUILD_MOD=ON (default): builds the AimModCore UE4SS mod against a
#    local, git-ignored RE-UE4SS checkout pinned to the UE4SS build the game
#    runs. Configure with the Visual Studio generator and build the
#    Game__Shipping__Win64 configuration.
#  - AIMMOD_BUILD_MOD=OFF: builds only the engine-independent core library and
#    its test runner (no UE4SS source needed).
option(AIMMOD_BUILD_MOD "Build the UE4SS mod (requires a local RE-UE4SS checkout)" ON)
option(AIMMOD_BUILD_TESTS "Build the core test runner" ON)
set(AIMMOD_UE4SS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../external/RE-UE4SS" CACHE PATH "Local RE-UE4SS checkout (never committed)")

if(AIMMOD_BUILD_MOD)
    if(NOT EXISTS "${AIMMOD_UE4SS_DIR}/CMakeLists.txt")
        message(FATAL_ERROR "RE-UE4SS not found at ${AIMMOD_UE4SS_DIR}. See in-game/native-mod/DESIGN.md (Building).")
    endif()
    add_subdirectory("${AIMMOD_UE4SS_DIR}" RE-UE4SS)
endif()

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_library(aimmod_core STATIC
    core/src/Formats.cpp
    core/src/Lifecycle.cpp
    core/src/ReplayWriter.cpp
    core/src/ReplayV2.cpp
    core/src/GameStats.cpp
    core/src/PlaybackFrame.cpp
    core/src/GameCommand.cpp
    core/src/Supervisor.cpp
    core/src/Settings.cpp
    core/src/MatchPlay.cpp
    core/src/Json.cpp
    core/src/Cosmetics.cpp
    core/src/CosmeticsPreview.cpp
)
target_include_directories(aimmod_core PUBLIC core/include)
if(MSVC)
    target_compile_options(aimmod_core PRIVATE /W4 /utf-8 /permissive-)
    target_compile_definitions(aimmod_core PUBLIC NOMINMAX WIN32_LEAN_AND_MEAN)
endif()

if(AIMMOD_BUILD_TESTS)
    add_executable(aimmod_replay_tool tests/ReplayTool.cpp)
    target_link_libraries(aimmod_replay_tool PRIVATE aimmod_core)
    add_executable(aimmod_core_tests tests/CoreTests.cpp)
    target_link_libraries(aimmod_core_tests PRIVATE aimmod_core)
    target_compile_definitions(aimmod_core_tests PRIVATE AIMMOD_SOURCE_DIR="${CMAKE_CURRENT_SOURCE_DIR}")
    if(MSVC)
        target_compile_options(aimmod_core_tests PRIVATE /W4 /utf-8 /permissive-)
    endif()
endif()

if(AIMMOD_BUILD_MOD)
    add_library(AimModCore SHARED
        mod/src/dllmain.cpp
        mod/src/GameBindings.cpp
        mod/src/Observer.cpp
        mod/src/ReplaySampler.cpp
        mod/src/World.cpp
        mod/src/Presenter.cpp
        mod/src/GameControl.cpp
        mod/src/Output.cpp
        mod/src/ServiceHost.cpp
        mod/src/MatchPlay.cpp
        mod/src/Cosmetics.cpp
        mod/src/CosmeticsPreview.cpp
    )
    target_include_directories(AimModCore PRIVATE mod/src)
    target_link_libraries(AimModCore PRIVATE aimmod_core windowscodecs ole32)
    target_link_libraries(AimModCore PUBLIC UE4SS)
    # UE4SS loads Mods/<Name>/dlls/main.dll.
    set_target_properties(AimModCore PROPERTIES OUTPUT_NAME "main")
    if(MSVC)
        target_compile_options(AimModCore PRIVATE /utf-8)
    endif()
endif()
