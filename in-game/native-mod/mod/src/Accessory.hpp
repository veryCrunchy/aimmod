#pragma once
// Accessories built from the game's and engine's own meshes (catalog items
// with a "fit", in-game/docs/cosmetics.md "Accessories"): AimMod's own
// collision-free static mesh component on the character's skeletal mesh,
// tinted through the material's probed parameters, sized and placed by
// cosmetics::PlaceAccessory and attached to the fit's bone so it follows the
// animation. Used by the match applier (avatars) and the Cosmetics preview.
// Game thread only.
#include "GameBindings.hpp"

#include <aimmod/Cosmetics.hpp>

#include <string>

namespace aimmod
{
    // Returns the new component, or null with `why`. Accessories are never
    // destroyed at runtime: hide them (SetAccessoryVisible) and reuse them;
    // their owner's level cleans them up.
    game::UObject* AttachFitAccessory(game::UObject* actor, game::UObject* skeletalMesh, const cosmetics::Item& item, std::string& why);
    void SetAccessoryVisible(game::UObject* component, bool visible);
    // The character's forward (world, horizontal) from its shoulder bones:
    // right = left to right shoulder, forward = right x up. False if the rig has none.
    bool CharacterForward(game::UObject* skeletalMesh, double forward[3]);
} // namespace aimmod
