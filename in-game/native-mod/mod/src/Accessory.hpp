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
    // Returns the new component (destroy it with RemoveAccessory), or null with `why`.
    game::UObject* AttachFitAccessory(game::UObject* actor, game::UObject* skeletalMesh, const cosmetics::Item& item, std::string& why);
    void RemoveAccessory(game::UObject* component);
} // namespace aimmod
