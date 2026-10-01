#pragma once
// Material parameter names and slot defaults, for fitting catalog items to
// whatever materials a look uses (game thread).
#include "GameBindings.hpp"

#include <set>
#include <string>

namespace aimmod
{
    class MaterialParams
    {
    public:
        // Binds the reflected members; false if parameter lists are unreadable.
        bool Bind();
        bool ok() const { return m_ok; }
        bool baseReadable() const { return m_materialEntries[1].ok() && m_infoName.ok(); }
        // Instance overrides along the parent chain, then the base material's runtime entries.
        void Names(game::UObject* material, std::set<std::string>& vectors, std::set<std::string>& scalars, std::set<std::string>& textures) const;
        // True when `material` has every named vector and scalar parameter.
        bool Has(game::UObject* material, const std::set<std::string>& vectors, const std::set<std::string>& scalars) const;
        // A compact description for the log: "<name> (vectors a,b,c; scalars d,e)".
        std::string Describe(game::UObject* material) const;
        // The skeletal mesh asset's own material for `slot` (what the slot shows
        // without a skin's override), or null.
        game::UObject* MeshDefault(game::UObject* meshComponent, int slot) const;

    private:
        bool m_ok{};
        game::Path m_miParent, m_miArrays[3], m_miNames[3], m_materialEntries[3], m_infoName;
        game::Path m_meshAsset, m_meshMaterials, m_slotMaterial;
        game::UClass *m_materialInstance{}, *m_material{}, *m_skinnedMesh{};
    };
} // namespace aimmod
