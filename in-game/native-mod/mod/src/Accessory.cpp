#include "Accessory.hpp"

#include "Log.hpp"
#include "MaterialParams.hpp"
#include "Reflect.hpp"

#include <Unreal/Core/HAL/UnrealMemory.hpp>
#include <Unreal/FProperty.hpp>
#include <Unreal/NameTypes.hpp>
#include <Unreal/Property/FArrayProperty.hpp>
#include <Unreal/Property/FStructProperty.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstring>
#include <optional>
#include <set>

namespace aimmod
{
    using namespace reflect;
    using RC::Unreal::FName;

    namespace
    {
        constexpr std::uint8_t NoCollision = 0, KeepWorld = 1; // ECollisionEnabled, EAttachmentRule

        std::string Lower(std::string s)
        {
            for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
            return s;
        }
        // The first bone whose name contains `keyword` (case-insensitive),
        // skipping fingers, twist and helper bones.
        std::optional<std::wstring> FindBone(UObject* mesh, const std::string& keyword)
        {
            std::int32_t count = 0;
            Call(mesh, STR("/Script/Engine.SkinnedMeshComponent:GetNumBones"), {}, nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
                if (n == STR("ReturnValue") && p->GetSize() == 4) std::memcpy(&count, v, 4);
            });
            const std::string want = Lower(keyword);
            for (std::int32_t i = 0; i < std::min(count, 512); ++i)
            {
                std::wstring name;
                Call(mesh, STR("/Script/Engine.SkinnedMeshComponent:GetBoneName"),
                     [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                         if (n == STR("BoneIndex")) std::memcpy(v, &i, 4);
                     },
                     nullptr,
                     [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
                         if (n == STR("ReturnValue") && p->GetSize() == sizeof(FName))
                         {
                             FName value;
                             std::memcpy(&value, v, sizeof(value));
                             name = value.ToString();
                         }
                     });
                const std::string lower = Lower(game::Narrow(name));
                if (lower.empty() || lower == "none" || lower.find(want) == std::string::npos) continue;
                if (lower.find("finger") != std::string::npos || lower.find("twist") != std::string::npos || lower.find("_end") != std::string::npos) continue;
                return name;
            }
            return std::nullopt;
        }
        bool SocketLocation(UObject* mesh, const std::wstring& bone, double out[3])
        {
            bool ok = false;
            Call(mesh, STR("/Script/Engine.SceneComponent:GetSocketLocation"),
                 [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                     if (n == STR("InSocketName")) WriteFName(v, p, bone);
                 },
                 nullptr,
                 [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
                     float f[3];
                     if (n == STR("ReturnValue") && ReadFloats(v, p, f, 3)) out[0] = f[0], out[1] = f[1], out[2] = f[2], ok = true;
                 });
            return ok;
        }
        struct RawArray
        {
            void* data;
            std::int32_t num, max;
        };
        // A TArray parameter filled from `bytes` (engine allocator); freed by the caller.
        void* WriteArray(std::uint8_t* value, const void* bytes, std::size_t count, std::size_t elementSize)
        {
            if (count == 0) return nullptr;
            void* data = RC::Unreal::FMemory::Malloc(count * elementSize);
            if (!data) return nullptr;
            std::memcpy(data, bytes, count * elementSize);
            RawArray raw{data, static_cast<std::int32_t>(count), static_cast<std::int32_t>(count)};
            std::memcpy(value, &raw, sizeof(raw));
            return data;
        }

        // One mesh section on a ProceduralMeshComponent, no collision. Each
        // triangle is added in both windings, so the piece shows from any side
        // whatever the material's culling.
        bool BuildSection(UObject* component, const mesh::Mesh& m)
        {
            std::vector<std::int32_t> triangles;
            triangles.reserve(m.indices.size() * 2);
            for (std::size_t i = 0; i + 2 < m.indices.size(); i += 3)
            {
                const auto a = static_cast<std::int32_t>(m.indices[i]), b = static_cast<std::int32_t>(m.indices[i + 1]), c = static_cast<std::int32_t>(m.indices[i + 2]);
                triangles.insert(triangles.end(), {a, b, c, a, c, b});
            }
            std::vector<float> colours;
            colours.reserve(m.colours.size());
            for (std::uint8_t c : m.colours) colours.push_back(c / 255.0f);
            // Tangents (FProcMeshTangent: TangentX, bFlipTangentY): without them the
            // material's normal map shades the piece near black.
            std::vector<std::uint8_t> tangents(m.normals.size() * 16, 0);
            for (std::size_t i = 0; i < m.normals.size(); ++i)
            {
                const mesh::Vec3& n = m.normals[i];
                float t[3] = {-n.y, n.x, 0}; // up x normal: along the ring
                float l = std::sqrt(t[0] * t[0] + t[1] * t[1]);
                if (l < 1e-3f) t[0] = 1, t[1] = 0, l = 1;
                t[0] /= l, t[1] /= l;
                std::memcpy(&tangents[i * 16], t, 12);
            }
            std::vector<void*> owned;
            const bool ok = Call(component, STR("/Script/ProceduralMeshComponent.ProceduralMeshComponent:CreateMeshSection_LinearColor"),
                                 [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                                     void* data = nullptr;
                                     if (n == STR("Vertices")) data = WriteArray(v, m.positions.data(), m.positions.size(), 12);
                                     else if (n == STR("Triangles")) data = WriteArray(v, triangles.data(), triangles.size(), 4);
                                     else if (n == STR("Normals")) data = WriteArray(v, m.normals.data(), m.normals.size(), 12);
                                     else if (n == STR("UV0")) data = WriteArray(v, m.uvs.data(), m.uvs.size() / 2, 8);
                                     else if (n == STR("VertexColors")) data = WriteArray(v, colours.data(), colours.size() / 4, 16);
                                     else if (n == STR("Tangents") && RC::Unreal::CastField<RC::Unreal::FArrayProperty>(p) && RC::Unreal::CastField<RC::Unreal::FArrayProperty>(p)->GetInner()->GetSize() == 16)
                                         data = WriteArray(v, tangents.data(), m.normals.size(), 16);
                                     else if (n == STR("bCreateCollision")) WriteBoolParam(v, p, false);
                                     if (data) owned.push_back(data);
                                 });
            for (void* data : owned) RC::Unreal::FMemory::Free(data);
            return ok;
        }
    } // namespace

    cosmetics::HeadPoints ModelHead(UObject* skeletalMesh, const std::string& model)
    {
        UObject* pack = LoadGameAsset(STR("/Game/FirstPersonBP/Blueprints/Bodies/Characters/CharacterModelPacks/Default_CharacterModelPack.Default_CharacterModelPack"));
        const std::wstring want = Widen(model);
        for (UObject* asset : GetObjects(pack, STR("Models"), 64))
        {
            UObject* data = GetObject(asset, STR("CharacterModel"));
            if (!Alive(data) || GetName(data, STR("Name")) != want) continue;
            float feet[3];
            const auto height = GetFloat(data, STR("MeshFullHeight")), diameter = GetFloat(data, STR("MeshHeadDiameter"));
            if (!height || !diameter || !Location(skeletalMesh, feet)) break;
            return cosmetics::HeadGeometry(feet[2], *height, *diameter);
        }
        return {};
    }

    UObject* AttachFitAccessory(UObject* actor, UObject* skeletalMesh, const cosmetics::Item& item, const mesh::Mesh* shape, const std::string& model, std::string& why)
    {
        if (!item.fit) return (why = "not a fit accessory", nullptr);
        if (!Alive(actor) || !Alive(skeletalMesh)) return (why = "the character is not valid", nullptr);
        const bool runtime = !item.shape.empty();
        if (runtime && (!shape || !shape->Valid())) return (why = "its mesh file is missing or did not match the manifest", nullptr);
        if ((!runtime && !cosmetics::IsGameAccessoryAsset(item.mesh, false)) || !cosmetics::IsGameAccessoryAsset(item.material, true)) return (why = "asset not allowed", nullptr);
        cosmetics::Fit fit = *item.fit;
        UObject* meshAsset = runtime ? nullptr : LoadGameAsset(Widen(item.mesh));
        UObject* material = LoadGameAsset(Widen(item.material));
        if ((!runtime && !Alive(meshAsset)) || !Alive(material)) return (why = "mesh or material not found", nullptr);
        static MaterialParams params;
        static bool bound = params.Bind();
        (void)bound;
        // The material's parameters, once per item: what the piece can be tinted with.
        static std::set<std::string> described;
        if (described.size() < 64 && described.insert(item.id).second) Log("cosmetics: " + item.id + " material " + params.Describe(material));

        // Anchor and the character's own frame.
        const auto bone = FindBone(skeletalMesh, fit.bone);
        if (!bone) return (why = "no bone like " + fit.bone, nullptr);
        double anchor[3];
        if (!SocketLocation(skeletalMesh, *bone, anchor)) return (why = "bone position unavailable", nullptr);
        // The model's own head (its data: full height and head diameter): where
        // the head anchors sit, and the scale for pieces made for a 22 cm head.
        const cosmetics::HeadPoints head = ModelHead(skeletalMesh, model);
        if (fit.anchor != "bone" && !head.valid) return (why = "no head data for " + model, nullptr);
        if (head.valid)
            for (int i = 0; i < 3; ++i) fit.size[i] *= head.scale, fit.offset[i] *= head.scale;
        if (fit.anchor == "top") anchor[2] = head.top;
        else if (fit.anchor == "crown") anchor[2] = head.centre;
        else if (fit.anchor == "chin") anchor[2] = head.chin;
        // Forward from the shoulders: right = left to right shoulder, forward = right x up.
        double forward[3] = {1, 0, 0};
        CharacterForward(skeletalMesh, forward);

        UClass* meshClass = RC::Unreal::UObjectGlobals::StaticFindObject<UClass*>(
            nullptr, nullptr, runtime ? STR("/Script/ProceduralMeshComponent.ProceduralMeshComponent") : STR("/Script/Engine.StaticMeshComponent"));
        UObject* component = nullptr;
        Call(actor, STR("/Script/Engine.Actor:AddComponentByClass"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("Class")) WriteObject(v, meshClass);
            else if (n == STR("bManualAttachment")) WriteBoolParam(v, p, true);
            else if (n == STR("RelativeTransform"))
                if (auto* s = RC::Unreal::CastField<RC::Unreal::FStructProperty>(p))
                    for (const char* field : {"Rotation.W", "Scale3D.X", "Scale3D.Y", "Scale3D.Z"}) game::SetStructPath(v, s->GetStruct(), field, 1);
        }, &component);
        if (!InObjectArray(meshClass) || !Valid(component, meshClass, actor)) return (why = "could not add the component", nullptr);
        // Never part of any trace, never a shadow.
        Call(component, STR("/Script/Engine.PrimitiveComponent:SetCollisionEnabled"), [](const std::wstring&, FProperty*, std::uint8_t* v) { *v = NoCollision; });
        Call(component, STR("/Script/Engine.PrimitiveComponent:SetCastShadow"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, false); });
        float localMin[3]{}, localMax[3]{};
        if (runtime)
        {
            if (!BuildSection(component, *shape)) return SetAccessoryVisible(component, false), (why = "the mesh section could not be built", nullptr);
            double mn[3], mx[3];
            shape->Bounds(mn, mx);
            for (int k = 0; k < 3; ++k) localMin[k] = static_cast<float>(mn[k]), localMax[k] = static_cast<float>(mx[k]);
        }
        else
        {
            Call(component, STR("/Script/Engine.StaticMeshComponent:SetStaticMesh"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("NewMesh")) WriteObject(v, meshAsset);
            });
            Call(component, STR("/Script/Engine.StaticMeshComponent:GetLocalBounds"), {}, nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
                if (n == STR("Min")) ReadFloats(v, p, localMin, 3);
                else if (n == STR("Max")) ReadFloats(v, p, localMax, 3);
            });
        }

        // The item's colours on a dynamic instance of the curated material, on every slot.
        UObject* mid = nullptr;
        Call(Default(STR("/Script/Engine.Default__KismetMaterialLibrary")), STR("/Script/Engine.KismetMaterialLibrary:CreateDynamicMaterialInstance"),
             [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                 if (n == STR("WorldContextObject")) WriteObject(v, actor);
                 else if (n == STR("Parent")) WriteObject(v, material);
             },
             &mid);
        if (mid)
        {
            // Every colour slot of the material takes the piece's colour: the
            // brush's UVs land on arbitrary mask regions of the character material.
            std::vector<std::pair<std::string, cosmetics::Color>> colours(item.vector.begin(), item.vector.end());
            const cosmetics::Colours c = cosmetics::ItemColours(item);
            // Common colour parameter names of flat game and engine materials too
            // (no-ops where the material lacks them).
            for (const char* name : {"Color", "BodyColor", "HeadColor", "AccentColor", "BaseColor", "Base Color", "Tint", "TintColor", "GizmoColor", "EmissiveColor", "Emissive"})
                if (std::none_of(colours.begin(), colours.end(), [&](const auto& v) { return v.first == name; })) colours.push_back({name, c.main});
            for (const auto& [name, col] : colours)
                Call(mid, STR("/Script/Engine.MaterialInstanceDynamic:SetVectorParameterValue"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                    if (n == STR("ParameterName")) WriteFName(v, p, Widen(name));
                    else if (n == STR("Value")) WriteFloats(v, p, {static_cast<float>(col.r), static_cast<float>(col.g), static_cast<float>(col.b), static_cast<float>(col.a)});
                });
            for (const auto& [name, x] : item.scalar)
                Call(mid, STR("/Script/Engine.MaterialInstanceDynamic:SetScalarParameterValue"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                    if (n == STR("ParameterName")) WriteFName(v, p, Widen(name));
                    else if (n == STR("Value")) WriteFloats(v, p, {static_cast<float>(x)});
                });
        }
        std::int32_t slots = 1;
        Call(component, STR("/Script/Engine.PrimitiveComponent:GetNumMaterials"), {}, nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
            if (n == STR("ReturnValue") && p->GetSize() == 4) std::memcpy(&slots, v, 4);
        });
        for (std::int32_t slot = 0; slot < std::clamp(slots, 1, 8); ++slot)
            Call(component, STR("/Script/Engine.PrimitiveComponent:SetMaterial"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("ElementIndex")) std::memcpy(v, &slot, sizeof(slot));
                else if (n == STR("Material")) WriteObject(v, mid ? mid : material);
            });
        ApplyMaterialDataDefaults(component);

        const double minD[3] = {localMin[0], localMin[1], localMin[2]}, maxD[3] = {localMax[0], localMax[1], localMax[2]};
        const auto placement = cosmetics::PlaceAccessory(fit, minD, maxD, anchor, forward);
        if (!placement)
        {
            SetAccessoryVisible(component, false); // never destroyed at runtime
            return (why = "mesh has no bounds", nullptr);
        }
        SetWorldTransform(component, placement->location, placement->rotation, placement->scale);
        // Keep the world placement and follow the bone from now on.
        Call(component, STR("/Script/Engine.SceneComponent:K2_AttachToComponent"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("Parent")) WriteObject(v, skeletalMesh);
            else if (n == STR("SocketName")) WriteFName(v, p, *bone);
            else if (n == STR("LocationRule") || n == STR("RotationRule") || n == STR("ScaleRule")) *v = KeepWorld;
        });
        SetVisible(component, true, false);
        return component;
    }

    bool CharacterForward(UObject* skeletalMesh, double forward[3])
    {
        double left[3], right[3];
        const auto l = FindBone(skeletalMesh, "Arm_L"), r = FindBone(skeletalMesh, "Arm_R");
        if (!l || !r || !SocketLocation(skeletalMesh, *l, left) || !SocketLocation(skeletalMesh, *r, right)) return false;
        const double rx = right[0] - left[0], ry = right[1] - left[1];
        if (std::hypot(rx, ry) <= 1) return false;
        forward[0] = ry, forward[1] = -rx, forward[2] = 0;
        return true;
    }

    void SetAccessoryVisible(UObject* component, bool visible)
    {
        // Only a live component of a live owner.
        if (!Alive(component) || !Alive(component->GetOuterPrivate())) return;
        SetVisible(component, visible, false);
    }
} // namespace aimmod
