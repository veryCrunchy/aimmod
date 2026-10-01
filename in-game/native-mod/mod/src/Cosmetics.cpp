#include "Cosmetics.hpp"

#include "Accessory.hpp"
#include "Reflect.hpp"

#include "Log.hpp"
#include "Output.hpp"
#include "World.hpp"

#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/FAssetData.hpp>
#include <Unreal/UAssetRegistry.hpp>
#include <Unreal/UAssetRegistryHelpers.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>

#include <algorithm>
#include <cstring>
#include <ctime>

namespace aimmod
{
    using namespace game;

    namespace
    {
        constexpr int MaxSlots = 16;
        constexpr std::size_t MaxCharacters = 64;
        constexpr int MaxRedress = 5;
        constexpr std::uint8_t NoCollision = 0;   // ECollisionEnabled::NoCollision
        constexpr std::uint8_t SnapToTarget = 2;  // EAttachmentRule::SnapToTarget

        void WriteFloat(std::uint8_t* value, double v)
        {
            const float f = static_cast<float>(v);
            std::memcpy(value, &f, 4);
        }
        void WriteBool(std::uint8_t* value, const Param& p, bool v)
        {
            if (p.boolProperty) p.boolProperty->SetPropertyValue(value, v);
        }
        void WriteObject(std::uint8_t* value, UObject* object) { std::memcpy(value, &object, sizeof(object)); }
        void WriteVector(std::uint8_t* value, const double v[3])
        {
            const float f[3] = {static_cast<float>(v[0]), static_cast<float>(v[1]), static_cast<float>(v[2])};
            std::memcpy(value, f, 12);
        }
        UObject* ReturnObject(const std::uint8_t* buffer, const std::vector<Param>& params)
        {
            for (const Param& p : params)
                if (p.ret) return ReadObject(buffer, p);
            return nullptr;
        }
        // Every object read from game memory goes through the one validation helper.
        bool Live(UObject* o) { return reflect::Alive(o); }
    } // namespace

    void Cosmetics::Bind()
    {
        m_benchmark.BindPath(STR("/Script/GameSkillsTrainer.ScenarioManager:IsCurrentlyInBenchmark"), Shape::Bool);
        m_editor.BindPath(STR("/Script/GameSkillsTrainer.ScenarioManager:IsInScenarioEditor"), Shape::Bool);
        m_numMaterials.BindPath(STR("/Script/Engine.PrimitiveComponent:GetNumMaterials"), Shape::Number);
        m_getMaterial.BindPath(STR("/Script/Engine.PrimitiveComponent:GetMaterial"), Shape::Command);
        m_setMaterial.BindPath(STR("/Script/Engine.PrimitiveComponent:SetMaterial"), Shape::Command);
        m_createMid.BindPath(STR("/Script/Engine.KismetMaterialLibrary:CreateDynamicMaterialInstance"), Shape::Command);
        m_setVector.BindPath(STR("/Script/Engine.MaterialInstanceDynamic:SetVectorParameterValue"), Shape::Command);
        m_setScalar.BindPath(STR("/Script/Engine.MaterialInstanceDynamic:SetScalarParameterValue"), Shape::Command);
        m_setTexture.BindPath(STR("/Script/Engine.MaterialInstanceDynamic:SetTextureParameterValue"), Shape::Command);
        m_addComponent.BindPath(STR("/Script/Engine.Actor:AddComponentByClass"), Shape::Command);
        m_setStaticMesh.BindPath(STR("/Script/Engine.StaticMeshComponent:SetStaticMesh"), Shape::Command);
        m_setCollision.BindPath(STR("/Script/Engine.PrimitiveComponent:SetCollisionEnabled"), Shape::Command);
        m_attach.BindPath(STR("/Script/Engine.SceneComponent:K2_AttachToComponent"), Shape::Command);
        m_relative.BindPath(STR("/Script/Engine.SceneComponent:K2_SetRelativeLocationAndRotation"), Shape::Command);
        m_scale.BindPath(STR("/Script/Engine.SceneComponent:SetRelativeScale3D"), Shape::Command);
        m_weaponMesh.BindPath(STR("/Script/GameSkillsTrainer.FPSPlayer_WeaponComponentActor:GetSelectWeaponMesh"), Shape::Object);
        m_armsMesh.BindPath(STR("/Script/GameSkillsTrainer.FPSPlayer_WeaponComponentActor:GetFPSPlayerSkeletalMeshComponent"), Shape::Object);
        m_weaponModel.BindPath(STR("/Script/GameSkillsTrainer.FPSPlayer_WeaponComponentActor:GetSelectedWeaponModelName"), Shape::Name);
        m_shotOrigin.BindPath(STR("/Script/GameSkillsTrainer.FPSPlayer_WeaponComponentActor:GetShotOrigin"), Shape::Object);

        UClass* actor = FindClass(STR("/Script/Engine.Actor"));
        UClass* character = FindClass(STR("/Script/Engine.Character"));
        m_metaCharacter = FindClass(STR("/Script/GameSkillsTrainer.MetaCharacter"));
        m_materialInstance = FindClass(STR("/Script/Engine.MaterialInstance"));
        m_material = FindClass(STR("/Script/Engine.Material"));
        m_skeletalMesh = FindClass(STR("/Script/Engine.SkeletalMeshComponent"));
        m_staticMeshComponent = FindClass(STR("/Script/Engine.StaticMeshComponent"));
        m_modelAssetClass = FindClass(STR("/Script/GameSkillsTrainer.MetaCharacterModelDataAsset"));
        m_skinAssetClass = FindClass(STR("/Script/GameSkillsTrainer.MetaCharacterSkinDataAsset"));
        m_modelClass = FindClass(STR("/Script/GameSkillsTrainer.MetaCharacterModel"));
        m_skinClass = FindClass(STR("/Script/GameSkillsTrainer.MetaCharacterSkin"));
        m_materialLibrary = RC::Unreal::UObjectGlobals::StaticFindObject<UObject*>(nullptr, nullptr, STR("/Script/Engine.Default__KismetMaterialLibrary"));

        m_tags.Bind(actor, "Tags");
        m_mesh.Bind(character, "Mesh");
        m_profileModel.Bind(m_metaCharacter, "mCharacterProfileNative.CharacterModel");
        m_profileSkin.Bind(m_metaCharacter, "mCharacterProfileNative.CharacterSkin");
        m_viewModel.Bind(m_metaCharacter, "ViewModel_Native");
        m_miParent.Bind(m_materialInstance, "Parent");
        const char* arrays[3] = {"ScalarParameterValues", "VectorParameterValues", "TextureParameterValues"};
        for (int k = 0; k < 3; ++k)
        {
            m_miArrays[k].Bind(m_materialInstance, arrays[k]);
            m_miNames[k].Bind(m_miArrays[k].elementStruct(), "ParameterInfo.Name");
            m_materialEntries[k].Bind(m_material, "CachedExpressionData.Parameters.RuntimeEntries[" + std::to_string(k) + "].ParameterInfos");
        }
        m_infoName.Bind(m_materialEntries[1].elementStruct(), "Name");
        m_packModels.Bind(FindClass(STR("/Script/GameSkillsTrainer.CharacterModelPackDataAsset")), "Models");
        m_packSkins.Bind(FindClass(STR("/Script/GameSkillsTrainer.CharacterSkinPackDataAsset")), "Skins");
        m_modelAsset.Bind(m_modelAssetClass, "CharacterModel");
        m_skinAsset.Bind(m_skinAssetClass, "CharacterSkin");
        m_modelName.Bind(m_modelClass, "Name");
        m_skinName.Bind(m_skinClass, "Name");

        std::string missing;
        auto need = [&](bool ok, const char* name) {
            if (!ok) missing += std::string(missing.empty() ? "" : ", ") + name;
        };
        need(m_benchmark.ok() && m_editor.ok(), "ScenarioManager benchmark/editor state");
        need(m_numMaterials.ok() && m_getMaterial.ok() && m_setMaterial.ok(), "PrimitiveComponent materials");
        need(m_createMid.ok() && m_materialLibrary != nullptr, "CreateDynamicMaterialInstance");
        need(m_setVector.ok() && m_setScalar.ok(), "MaterialInstanceDynamic parameters");
        need(m_tags.ok() && m_tags.kind() == Kind::Array, "Actor.Tags");
        need(m_mesh.ok() && m_skeletalMesh, "Character.Mesh");
        need(m_profileModel.ok() && m_profileSkin.ok(), "mCharacterProfileNative model/skin");
        need(m_miParent.ok() && m_miNames[0].ok() && m_miNames[1].ok() && m_materialInstance && m_material, "material parameter lists");
        need(m_packModels.ok() && m_packSkins.ok() && m_modelAsset.ok() && m_skinAsset.ok() && m_modelName.ok() && m_skinName.ok(), "Default look packs");
        m_params.Bind();
        m_ready = missing.empty();
        m_accessories = m_ready && m_addComponent.ok() && m_setStaticMesh.ok() && m_setCollision.ok() && m_attach.ok() && m_relative.ok() && m_scale.ok() &&
                        m_staticMeshComponent;
        if (!m_ready) Log("cosmetics: disabled; missing " + missing);
        else
            Log(std::string("cosmetics: bindings ready") + (m_accessories ? "" : " (no accessories)") +
                (m_viewModel.ok() && m_weaponMesh.ok() && m_armsMesh.ok() ? "" : " (no own weapon finishes)") +
                (m_materialEntries[1].ok() && m_infoName.ok() ? "" : " (base material parameters unreadable)"));
    }

    void Cosmetics::Once(const std::string& key, const std::string& line)
    {
        if (m_logged.size() < 512 && m_logged.insert(key).second) Log("cosmetics: " + line);
    }

    // ------------------------------------------------------------ game state

    cosmetics::ScopeState Cosmetics::ReadState()
    {
        cosmetics::ScopeState state;
        UObject* manager = m_scene.Manager();
        if (!manager) return state;
        UObject* scenario = m_b.currentScenario.Object(manager);
        if (!scenario) return state;
        std::string name;
        if (m_b.scenarioName.String(scenario, name)) state.scenario = name;
        state.inChallenge = cosmetics::CombineChallenge(m_b.isInChallenge.Bool(manager), m_b.scenarioInChallenge.Bool(scenario));
        state.benchmark = m_benchmark.Bool(manager);
        state.editor = m_editor.Bool(manager);
        state.loading = m_b.isScenarioLoading.Bool(manager);
        return state;
    }

    const std::set<std::string>* Cosmetics::FreeModels() { return m_freeModels.get(); }
    const std::set<std::string>* Cosmetics::FreeSkins() { return m_freeSkins.get(); }

    // ------------------------------------------------------------- materials

    UObject* Cosmetics::Material(UObject* component, int index)
    {
        UObject* out = nullptr;
        m_getMaterial.Call(
            component, [&](std::uint8_t* value, const Param& p) { if (p.kind == Kind::Int32) std::memcpy(value, &index, 4); },
            [&](const std::uint8_t* buffer, const std::vector<Param>& params) { out = ReturnObject(buffer, params); });
        return out;
    }

    bool Cosmetics::SetMaterial(UObject* component, int index, UObject* material)
    {
        return m_setMaterial.Call(component, [&](std::uint8_t* value, const Param& p) {
            if (p.kind == Kind::Int32) std::memcpy(value, &index, 4);
            else if (p.kind == Kind::Object) WriteObject(value, material);
        });
    }

    // CosmeticsReflect.parameters: instance -> parent -> ... -> base material.
    void Cosmetics::ParameterNames(UObject* material, std::set<std::string>& vectors, std::set<std::string>& scalars, std::set<std::string>& textures)
    {
        m_params.Names(material, vectors, scalars, textures);
    }

    bool Cosmetics::Fits(UObject* material, const cosmetics::Item& item)
    {
        std::set<std::string> vectors, scalars, textures;
        ParameterNames(material, vectors, scalars, textures);
        auto missing = [&](const std::string& name) {
            Once(item.id + "|" + ObjectName(material), item.id + " does not fit " + ObjectName(material) + " (no parameter " + name + ")");
            return false;
        };
        for (const auto& [name, c] : item.vector)
            if (!vectors.contains(name)) return missing(name);
        for (const auto& [name, v] : item.scalar)
            if (!scalars.contains(name)) return missing(name);
        for (const auto& [name, path] : item.textures)
            if (!textures.contains(name)) return missing(name);
        return true;
    }

    // Assets come only from verified AimMod paks (resolution checked the pak).
    UObject* Cosmetics::LoadAsset(const std::string& path)
    {
        if (auto it = m_assets.find(path); it != m_assets.end())
            if (UObject* o = it->second.Get()) return o;
        if (m_failedAssets.contains(path) || path.rfind("/Game/AimModCosmetics/", 0) != 0) return nullptr;
        UObject* loaded = nullptr;
        auto* registry = static_cast<RC::Unreal::UAssetRegistry*>(RC::Unreal::UAssetRegistryHelpers::GetAssetRegistry().ObjectPointer);
        if (registry)
        {
            const std::wstring wide(path.begin(), path.end());
            RC::Unreal::FAssetData data = registry->GetAssetByObjectPath(RC::Unreal::FName(wide.c_str(), RC::Unreal::FNAME_Add));
            if (data.PackageName().GetComparisonIndex() || data.ObjectPath().GetComparisonIndex()) loaded = RC::Unreal::UAssetRegistryHelpers::GetAsset(data);
        }
        if (!loaded)
        {
            m_failedAssets.insert(path);
            Log("cosmetics: asset not found in the AimMod pak: " + path);
            return nullptr;
        }
        m_assets[path] = loaded;
        return loaded;
    }

    void Cosmetics::Dress(const Want& want)
    {
        UObject* component = want.component;
        const cosmetics::Item& item = *want.item;
        if (!Live(component) || !Live(want.owner)) return;
        if (m_redress[component] > MaxRedress) return;
        const int count = static_cast<int>(std::min<double>(m_numMaterials.Number(component).value_or(0), MaxSlots));
        for (int index = 0; index < count; ++index)
        {
            UObject* material = Material(component, index);
            if (!Live(material) || m_ours.contains(material)) continue;
            // The game replaced our instance on this slot (its own colour logic):
            // forget it, dress again, and stop after a few rounds.
            auto previous = std::find_if(m_dressed.begin(), m_dressed.end(), [&](const Dressed& d) { return d.key == component && d.index == index; });
            if (previous != m_dressed.end())
            {
                m_ours.erase(previous->mid.Get());
                m_dressed.erase(previous);
                if (++m_redress[component] > MaxRedress)
                {
                    Once("redress|" + item.id, "the game keeps resetting the materials " + item.id + " dresses; left to the game");
                    return;
                }
            }
            // Tints and finishes keep the skin: they recolour only the paint and
            // accent parameters this slot's own material has (MapColours).
            UObject* parent = material;
            std::vector<std::pair<std::string, cosmetics::Color>> colours;
            bool baseScheme = false;
            if (item.textures.empty())
            {
                std::set<std::string> vectors, scalars, textureNames;
                m_params.Names(material, vectors, scalars, textureNames);
                colours = cosmetics::MapColours(item, vectors);
                baseScheme = vectors.contains("MetalPaint") && vectors.contains("TriangularPaint");
                if (colours.empty())
                {
                    Once("nothing|" + item.id + "|" + ObjectName(material), item.id + ": nothing to recolour on " + ObjectName(material));
                    continue;
                }
            }
            else if (!Fits(material, item)) continue;
            std::vector<std::pair<std::string, UObject*>> textures;
            bool texturesOk = true;
            for (const auto& [name, path] : item.textures)
            {
                UObject* texture = LoadAsset(path);
                texturesOk &= texture != nullptr;
                textures.push_back({name, texture});
            }
            if (!texturesOk || (!textures.empty() && !m_setTexture.ok())) continue;
            UObject* mid = nullptr;
            m_createMid.Call(
                m_materialLibrary,
                [&](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Object) WriteObject(value, p.worldContext ? want.owner : parent);
                    // OptionalName None: the engine picks a unique name; CreationFlags 0.
                },
                [&](const std::uint8_t* buffer, const std::vector<Param>& params) { mid = ReturnObject(buffer, params); });
            if (!Live(mid)) continue;
            for (const auto& [name, c] : item.textures.empty() ? colours : item.vector)
                m_setVector.Call(mid, [&](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Name) WriteName(value, p, name);
                    else if (p.structType)
                    {
                        SetStructField(value, p.structType, "R", c.r);
                        SetStructField(value, p.structType, "G", c.g);
                        SetStructField(value, p.structType, "B", c.b);
                        SetStructField(value, p.structType, "A", c.a);
                    }
                });
            // Finish scalars (roughness, metallic) only on the base paint material: on a skin they would change its look.
            if (baseScheme || !item.textures.empty())
                for (const auto& [name, v] : item.scalar)
                    m_setScalar.Call(mid, [&](std::uint8_t* value, const Param& p) {
                        if (p.kind == Kind::Name) WriteName(value, p, name);
                        else if (p.kind == Kind::Float) WriteFloat(value, v);
                    });
            for (const auto& [name, texture] : textures)
                m_setTexture.Call(mid, [&](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Name) WriteName(value, p, name);
                    else if (p.kind == Kind::Object) WriteObject(value, texture);
                });
            if (!SetMaterial(component, index, mid)) continue;
            m_ours.insert(mid);
            Dressed d;
            d.component = component;
            d.original = material;
            d.mid = mid;
            d.key = component;
            d.index = index;
            d.item = item.id;
            d.scope = want.scope;
            m_dressed.push_back(std::move(d));
            Once("dressed|" + item.id + "|" + ObjectName(material), "applied " + item.id + " on " + ObjectName(material));
        }
    }

    void Cosmetics::Restore(const std::vector<Want>* keep, std::optional<Scope> scope)
    {
        std::vector<Dressed> kept;
        for (Dressed& d : m_dressed)
        {
            const bool wanted = keep && std::any_of(keep->begin(), keep->end(), [&](const Want& w) { return w.component == d.key && w.item->id == d.item; });
            if ((scope && d.scope != *scope) || wanted)
            {
                kept.push_back(std::move(d));
                continue;
            }
            UObject* component = d.component.Get();
            UObject* mid = d.mid.Get();
            UObject* original = d.original.Get();
            // Only on a live component of a live owner: a transition destroys the rest.
            if (reflect::Alive(component) && reflect::Alive(component->GetOuterPrivate()) && reflect::Alive(mid) && reflect::Alive(original) &&
                Material(component, d.index) == mid)
                SetMaterial(component, d.index, original);
            m_ours.erase(mid);
        }
        m_dressed = std::move(kept);
    }

    // ----------------------------------------------------------- accessories

    void Cosmetics::Attach(const WantAccessory& want)
    {
        const cosmetics::Item& item = *want.item;
        const std::string key = item.id + "|" + std::to_string(reinterpret_cast<std::uintptr_t>(want.actor));
        if (m_failedAssets.contains(key)) return;
        if (item.fit)
        {
            // From the game's and engine's own meshes, fitted to this rig.
            std::string why;
            const auto inputs = m_output.cosmetics();
            const mesh::Mesh* shape = nullptr;
            if (!item.shape.empty() && inputs.library)
                if (auto it = inputs.library->meshes.find(item.shape); it != inputs.library->meshes.end()) shape = it->second.get();
            UObject* component = AttachFitAccessory(want.actor, want.mesh, item, shape, why);
            if (!component)
            {
                m_failedAssets.insert(key);
                Once("fitfail|" + item.id, item.id + " not attached: " + why);
                return;
            }
            Worn w;
            w.actor = want.actor;
            w.component = component;
            w.key = want.actor;
            w.item = item.id;
            m_worn.push_back(std::move(w));
            Once("worn|" + item.id, "attached " + item.id);
            return;
        }
        if (!want.attachment) return;
        UObject* mesh = LoadAsset(item.mesh);
        UObject* material = item.material.empty() ? nullptr : LoadAsset(item.material);
        if (!mesh || (!item.material.empty() && !material))
        {
            m_failedAssets.insert(key);
            return;
        }
        UObject* component = nullptr;
        m_addComponent.Call(
            want.actor,
            [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Object) WriteObject(value, m_staticMeshComponent);
                else if (p.kind == Kind::Bool) WriteBool(value, p, p.name == "bManualAttachment");
                else if (p.structType)
                {
                    // Identity transform; the attachment sets the real one.
                    SetStructPath(value, p.structType, "Rotation.W", 1);
                    SetStructPath(value, p.structType, "Scale3D.X", 1);
                    SetStructPath(value, p.structType, "Scale3D.Y", 1);
                    SetStructPath(value, p.structType, "Scale3D.Z", 1);
                }
            },
            [&](const std::uint8_t* buffer, const std::vector<Param>& params) { component = ReturnObject(buffer, params); });
        if (!Live(component) || !component->IsA(m_staticMeshComponent))
        {
            m_failedAssets.insert(key);
            Once("addfail|" + item.id, "could not add the " + item.id + " component");
            return;
        }
        // Collision off before the mesh exists on it: never part of any trace.
        m_setCollision.Call(component, [](std::uint8_t* value, const Param& p) { if (p.kind == Kind::UInt8) *value = NoCollision; });
        m_setStaticMesh.Call(component, [&](std::uint8_t* value, const Param& p) { if (p.kind == Kind::Object) WriteObject(value, mesh); });
        if (material) SetMaterial(component, 0, material);
        const cosmetics::Attachment& a = *want.attachment;
        bool attached = false;
        m_attach.Call(
            component,
            [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Object) WriteObject(value, want.mesh);
                else if (p.kind == Kind::Name) WriteName(value, p, a.bone);
                else if (p.kind == Kind::UInt8) *value = SnapToTarget;
            },
            [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                for (const Param& p : params)
                    if (p.ret && p.boolProperty) attached = p.boolProperty->GetPropertyValue(const_cast<std::uint8_t*>(buffer) + p.offset);
            });
        m_relative.Call(component, [&](std::uint8_t* value, const Param& p) {
            if (p.kind == Kind::Vector && p.size == 12) WriteVector(value, a.location);
            else if (p.kind == Kind::Rotator && p.size == 12) WriteVector(value, a.rotation); // pitch, yaw, roll
            else if (p.kind == Kind::Bool) WriteBool(value, p, p.name == "bTeleport");
        });
        m_scale.Call(component, [&](std::uint8_t* value, const Param& p) { if (p.kind == Kind::Vector && p.size == 12) WriteVector(value, a.scale); });
        Worn w;
        w.actor = want.actor;
        w.component = component;
        w.key = want.actor;
        w.item = item.id;
        m_worn.push_back(std::move(w));
        Once("worn|" + item.id, "attached " + item.id + (attached ? "" : " (attachment not confirmed)"));
    }

    void Cosmetics::HideAccessories(const std::vector<WantAccessory>* keep)
    {
        std::vector<Worn> kept;
        for (Worn& w : m_worn)
        {
            const bool wanted = keep && std::any_of(keep->begin(), keep->end(), [&](const WantAccessory& a) { return a.actor == w.key && a.item->id == w.item; });
            UObject* component = w.component.Get();
            if (!reflect::Alive(component) || !reflect::Alive(w.actor.Get())) continue; // gone with its avatar
            // Not worn any more: hidden and kept (never destroyed at runtime); the level cleans it up.
            if (!wanted && !w.hidden)
            {
                SetAccessoryVisible(component, false);
                w.hidden = true;
            }
            kept.push_back(std::move(w));
        }
        m_worn = std::move(kept);
    }

    // ------------------------------------------------------------- selection

    void Cosmetics::Avatars(const cosmetics::Looks& looks, const cosmetics::ResolveOptions& options, const cosmetics::Index& index, UObject* local,
                            std::vector<Want>& wants, std::vector<WantAccessory>& accessories)
    {
        if (looks.peers.empty()) return;
        std::vector<UObject*> actors;
        UObject* state = m_scene.GameState();
        if (!state || !m_b.characters.Objects(state, actors, MaxCharacters)) return;
        for (UObject* actor : actors)
        {
            if (!Live(actor) || actor == local || !actor->IsA(m_metaCharacter)) continue;
            // Only AimMod avatars: the bridge tags each with AimMod.Peer.<SteamID64>.
            std::vector<std::string> tags;
            if (!m_tags.Names(actor, tags, 32)) continue;
            std::optional<std::string> peer;
            int peerTags = 0;
            for (const std::string& tag : tags)
                if (auto id = cosmetics::PeerFromTag(tag))
                {
                    peer = id;
                    ++peerTags;
                }
            if (peerTags != 1) continue;
            const cosmetics::PeerLook* look = looks.Find(*peer);
            if (!look) continue;
            // Free looks only: never over a viewer-equipped DLC look.
            std::string model, skin;
            if (!m_profileModel.String(actor, model) || !m_profileSkin.String(actor, skin)) continue;
            if (!cosmetics::IsFreeLook(model, skin, FreeModels(), FreeSkins()))
            {
                Once("paid|" + model + "|" + skin, "avatar look " + model + "/" + skin + " is not a free Default look; left untouched");
                continue;
            }
            UObject* mesh = m_mesh.Object(actor);
            if (!Live(mesh) || !mesh->IsA(m_skeletalMesh)) continue;
            cosmetics::Plan plan = cosmetics::PlanAvatar(index, look->items, options, model);
            for (const std::string& reason : plan.skipped) Once("skip|" + reason, reason);
            if (plan.body) wants.push_back({mesh, actor, plan.body, Scope::Avatar});
            if (m_accessories)
                for (const cosmetics::Item* item : {plan.head, plan.neck, plan.spine})
                    if (item) accessories.push_back({actor, mesh, item, item->AttachmentFor(model)});
        }
    }

    void Cosmetics::Local(const cosmetics::Looks& looks, const cosmetics::ResolveOptions& options, const cosmetics::Index& index, UObject* local,
                          std::vector<Want>& wants)
    {
        if (looks.self.empty() || !m_viewModel.ok()) return;
        UObject* view = m_viewModel.Object(local);
        if (!Live(view)) return;
        std::string weapon;
        if (m_weaponModel.ok()) m_weaponModel.Name(view, weapon);
        cosmetics::Plan plan = cosmetics::PlanLocal(index, looks.self, options, weapon);
        for (const std::string& reason : plan.skipped) Once("skip|" + reason, reason);
        UObject* shotOrigin = m_shotOrigin.ok() ? m_shotOrigin.Object(view) : nullptr;
        auto add = [&](UObject* component, const cosmetics::Item* item) {
            // ShotOrigin is gameplay: never dressed.
            if (!item || !Live(component) || component == shotOrigin || ObjectName(component) == "ShotOrigin") return;
            wants.push_back({component, view, item, Scope::Local});
        };
        add(m_weaponMesh.Object(view), plan.weapon);
        add(m_armsMesh.Object(view), plan.arms);
    }

    // ------------------------------------------------------------------ tick

    void Cosmetics::Tick(double now)
    {
        if (!m_ready || now < m_next) return;
        m_next = now + 1.0;
        const Output::CosmeticsInputs inputs = m_output.cosmetics();
        cosmetics::ScopeState state = ReadState();
        state.marker = inputs.marker;
        state.now = static_cast<double>(std::time(nullptr));
        cosmetics::Decision d = cosmetics::Decide(state);
        if (d.avatars || d.localPlayer)
        {
            if (!inputs.library || inputs.library->index.empty()) d = {false, false, "no verified catalog"};
            else if (!inputs.looks) d = {false, false, "no looks from the service"};
        }
        if (d.reason != m_reason)
        {
            m_reason = d.reason;
            Log("cosmetics: scope: " + d.reason);
        }
        // While a level loads (or the state is unknown) no engine call is made:
        // the transition may be destroying the avatars and their components.
        // What died is forgotten; what survives is restored on the next settled
        // tick (the gate is closed then), so nothing outlives a match.
        if (state.loading != std::optional<bool>(false))
        {
            std::erase_if(m_dressed, [&](const Dressed& dressed) {
                if (reflect::Alive(dressed.component.Get())) return false;
                m_ours.erase(dressed.mid.Get());
                return true;
            });
            std::erase_if(m_worn, [](const Worn& w) { return !reflect::Alive(w.component.Get()) || !reflect::Alive(w.actor.Get()); });
            return;
        }
        // Forget what the game destroyed (avatars leave, levels change).
        std::erase_if(m_dressed, [&](const Dressed& dressed) {
            if (dressed.component.Get()) return false;
            m_ours.erase(dressed.mid.Get());
            return true;
        });
        std::erase_if(m_worn, [](const Worn& w) { return !reflect::Alive(w.component.Get()) || !reflect::Alive(w.actor.Get()); });
        if (!d.avatars)
        {
            Restore(nullptr, Scope::Avatar);
            HideAccessories(nullptr);
        }
        if (!d.localPlayer) Restore(nullptr, Scope::Local);
        if (!d.avatars && !d.localPlayer)
        {
            m_redress.clear();
            return;
        }
        UObject* player = m_scene.Player();
        UObject* local = player ? m_b.myCharacter.Object(player) : nullptr;
        if (!Live(local)) return; // fail closed
        if (now >= m_nextPackCheck && (!m_freeModels || m_freeModels->empty()))
        {
            m_nextPackCheck = now + 30;
            // The game loads its Default packs only for the character menu: load
            // them here when they are not resident (the game's own assets, fixed paths).
            const auto packs = std::wstring(STR("/Game/FirstPersonBP/Blueprints/Bodies/Characters/"));
            UObject* modelPack = FindOrLoadAsset(packs + STR("CharacterModelPacks/Default_CharacterModelPack.Default_CharacterModelPack"));
            UObject* skinPack = FindOrLoadAsset(packs + STR("CharacterSkinPacks/Default_CharacterSkinPack.Default_CharacterSkinPack"));
            auto models = std::make_unique<std::set<std::string>>();
            auto skins = std::make_unique<std::set<std::string>>();
            std::vector<UObject*> assets;
            if (modelPack && m_packModels.Objects(modelPack, assets, 64))
                for (UObject* asset : assets)
                    if (Live(asset) && asset->IsA(m_modelAssetClass))
                        if (UObject* model = m_modelAsset.Object(asset); model && model->IsA(m_modelClass))
                            if (std::string n; m_modelName.Name(model, n) && !n.empty() && n != "None") models->insert(n);
            if (skinPack && m_packSkins.Objects(skinPack, assets, 128))
                for (UObject* asset : assets)
                    if (Live(asset) && asset->IsA(m_skinAssetClass))
                        if (UObject* skin = m_skinAsset.Object(asset); skin && skin->IsA(m_skinClass))
                            if (std::string n; m_skinName.Name(skin, n) && !n.empty() && n != "None") skins->insert(n);
            if (!models->empty())
            {
                Log("cosmetics: free looks: " + std::to_string(models->size()) + " model(s), " + std::to_string(skins->size()) + " skin(s)");
                m_freeModels = std::move(models);
                m_freeSkins = std::move(skins);
            }
        }
        cosmetics::ResolveOptions options;
        options.allowDrafts = inputs.allowDrafts;
        options.verifiedPaks = inputs.library->verifiedPaks;
        options.verifiedMeshes = inputs.library->verifiedMeshes;
        std::vector<Want> wants;
        std::vector<WantAccessory> accessories;
        if (d.avatars) Avatars(*inputs.looks, options, inputs.library->index, local, wants, accessories);
        if (d.localPlayer) Local(*inputs.looks, options, inputs.library->index, local, wants);
        Restore(&wants, std::nullopt);
        HideAccessories(&accessories);
        for (const Want& want : wants) Dress(want);
        for (const WantAccessory& a : accessories)
        {
            auto worn = std::find_if(m_worn.begin(), m_worn.end(), [&](const Worn& w) { return w.key == a.actor && w.item == a.item->id; });
            if (worn == m_worn.end()) Attach(a);
            else if (worn->hidden)
            {
                SetAccessoryVisible(worn->component.Get(), true); // reused
                worn->hidden = false;
            }
        }
    }
} // namespace aimmod
