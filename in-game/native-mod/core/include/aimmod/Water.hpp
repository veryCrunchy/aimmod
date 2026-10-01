#pragma once
// Swimmable water for AimMod's own scenarios (DESIGN.md "Water"). KovaaK's
// map-creator Water object is only a mesh: a translucent cube with no water
// physics. In AimMod scenarios AimModCore turns each one into an engine
// water volume (APhysicsVolume, bWaterVolume) that the character's own
// swimming mode reacts to, tuned like Counter-Strike or Quake.
// Engine-independent: the gate, the tuning and the swim input.
#include <string>
#include <string_view>
#include <vector>

namespace aimmod::water
{
    // AimMod's own scenarios only ("AimMod - " ports, generated "AimMod Match - "
    // arenas and "AimMod Probe " scenarios), in freeplay: never a challenge,
    // benchmark or the scenario editor, and not while loading.
    bool Allowed(std::string_view scenario, bool inChallenge, bool inBenchmark, bool inEditor, bool loading);

    enum class Style
    {
        CounterStrike, // Source / GoldSrc water movement
        Quake,         // Quake 3 water movement
    };
    const char* StyleName(Style style);

    // Engine values, in centimetres (map units x MapScale) and seconds.
    struct Tuning
    {
        double maxSwimSpeed{};        // CharacterMovement.MaxSwimSpeed
        double fluidFriction{};       // PhysicsVolume.FluidFriction (the engine applies half of it)
        double terminalVelocity{};    // PhysicsVolume.TerminalVelocity: speed kept when diving in
        double buoyancy{};            // CharacterMovement.Buoyancy: 1 = no gravity under water
        double sinkSpeed{};           // drift down with no movement input
        double outOfWaterZ{};         // CharacterMovement.OutofWaterZ: climbing out at an edge
        double jumpOutOfWaterPitch{}; // CharacterMovement.JumpOutOfWaterPitch: any view pitch
    };
    // runSpeed: the character's run speed (cm/s); mapScale: the scenario's
    // MapScale (Unreal units per source unit). Bad inputs fall back to sane values.
    Tuning TuningFor(Style style, double runSpeed, double mapScale);

    // Vertical swim input (-1..1, a fraction of MaxSwimSpeed) for the next
    // frame: jump swims up at full speed (CS and Quake), no movement input
    // sinks slowly, any movement input holds the depth.
    double VerticalInput(const Tuning& tuning, bool jumpHeld, double horizontalInput);

    // The keys bound to the "Jump" action in KovaaK's Input.ini
    // (ActionMappings=(ActionName="Jump",...,Key=SpaceBar)); SpaceBar when none.
    std::vector<std::string> JumpKeys(std::string_view inputIni);

    // Underwater view tint (linear RGB) for the post-process component.
    struct Tint
    {
        double r{}, g{}, b{};
    };
    Tint UnderwaterTint();
} // namespace aimmod::water
