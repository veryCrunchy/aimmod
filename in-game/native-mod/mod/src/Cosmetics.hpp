#pragma once
// Cosmetics applier (in-game/docs/cosmetics.md, DESIGN.md "Cosmetics").
// Game thread, once per second:
//  - reads the game state and the service's session marker, and opens the
//    scope gate (cosmetics::Decide) only in AimMod match/spectate sessions;
//  - dresses AimMod avatars (actors tagged AimMod.Peer.<SteamID64>) on free
//    looks: catalog parameters on new dynamic material instances parented on
//    the game's materials, and accessories as AimMod's own collision-free
//    static mesh components on a bone;
//  - in matches, applies the player's own weapon and arms finishes;
//  - restores every replaced material and destroys every accessory when the
//    gate closes or a look changes.
// It never changes the game's meshes, collision, ShotOrigin, scenario bots
// or paid (DLC) looks, and loads assets only from verified AimMod paks.
#include "GameBindings.hpp"
#include "MaterialParams.hpp"

#include <aimmod/Cosmetics.hpp>

#include <Unreal/FWeakObjectPtr.hpp>

#include <map>
#include <memory>
#include <set>
#include <string>
#include <vector>

namespace aimmod
{
    class Output;
    namespace game
    {
        struct Bindings;
        struct Scene;
    } // namespace game

    class Cosmetics
    {
    public:
        Cosmetics(game::Bindings& bindings, game::Scene& scene, Output& output) : m_b(bindings), m_scene(scene), m_output(output) {}
        void Bind();
        bool ready() const { return m_ready; }
        void Tick(double now);

    private:
        enum class Scope { Avatar, Local };
        struct Dressed
        {
            RC::Unreal::FWeakObjectPtr component, original, mid;
            game::UObject* key{}; // component identity
            int index{};
            std::string item;
            Scope scope{};
        };
        struct Worn
        {
            bool hidden{}; // hidden, kept for reuse (never destroyed at runtime)
            RC::Unreal::FWeakObjectPtr actor, component;
            game::UObject* key{}; // actor identity
            std::string item;
        };
        struct Want
        {
            game::UObject* component{};
            game::UObject* owner{};
            const cosmetics::Item* item{};
            Scope scope{};
        };
        struct WantAccessory
        {
            game::UObject* actor{};
            game::UObject* mesh{};
            const cosmetics::Item* item{};
            const cosmetics::Attachment* attachment{};
        };

        void Restore(const std::vector<Want>* keep, std::optional<Scope> scope);
        void HideAccessories(const std::vector<WantAccessory>* keep);
        void Dress(const Want& want);
        void Attach(const WantAccessory& want);
        bool Fits(game::UObject* material, const cosmetics::Item& item);
        void ParameterNames(game::UObject* material, std::set<std::string>& vectors, std::set<std::string>& scalars, std::set<std::string>& textures);
        game::UObject* LoadAsset(const std::string& path);
        game::UObject* Material(game::UObject* component, int index);
        bool SetMaterial(game::UObject* component, int index, game::UObject* material);
        const std::set<std::string>* FreeModels();
        const std::set<std::string>* FreeSkins();
        cosmetics::ScopeState ReadState();
        void Avatars(const cosmetics::Looks& looks, const cosmetics::ResolveOptions& options, const cosmetics::Index& index, game::UObject* local,
                     std::vector<Want>& wants, std::vector<WantAccessory>& accessories);
        void Local(const cosmetics::Looks& looks, const cosmetics::ResolveOptions& options, const cosmetics::Index& index, game::UObject* local,
                   std::vector<Want>& wants);
        void Once(const std::string& key, const std::string& line);

        game::Bindings& m_b;
        game::Scene& m_scene;
        Output& m_output;
        bool m_ready{}, m_accessories{};
        double m_next{};
        std::string m_reason;

        // Game state and looks.
        game::Getter m_benchmark, m_editor;
        game::Getter m_numMaterials, m_getMaterial, m_setMaterial, m_createMid, m_setVector, m_setScalar, m_setTexture;
        game::Getter m_addComponent, m_setStaticMesh, m_setCollision, m_attach, m_relative, m_scale;
        game::Getter m_weaponMesh, m_armsMesh, m_weaponModel, m_shotOrigin;
        game::Path m_tags, m_mesh, m_profileModel, m_profileSkin, m_viewModel;
        // Parameter names: instance overrides [scalar, vector, texture] and the
        // base material's cached runtime entries (same order).
        game::Path m_miParent, m_miArrays[3], m_miNames[3], m_materialEntries[3], m_infoName;
        MaterialParams m_params;
        game::Path m_packModels, m_packSkins, m_modelAsset, m_skinAsset, m_modelName, m_skinName;
        game::UClass *m_materialInstance{}, *m_material{}, *m_skeletalMesh{}, *m_staticMeshComponent{};
        game::UClass *m_modelAssetClass{}, *m_skinAssetClass{}, *m_modelClass{}, *m_skinClass{}, *m_metaCharacter{};
        game::UObject* m_materialLibrary{};
        std::unique_ptr<std::set<std::string>> m_freeModels, m_freeSkins;
        double m_nextPackCheck{};

        std::vector<Dressed> m_dressed;
        std::vector<Worn> m_worn;
        std::set<game::UObject*> m_ours; // our dynamic instances
        std::map<std::string, RC::Unreal::FWeakObjectPtr> m_assets;
        std::set<std::string> m_failedAssets, m_logged;
        std::map<game::UObject*, int> m_redress; // component -> times the game reset our material
    };
} // namespace aimmod
