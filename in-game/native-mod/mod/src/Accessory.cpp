#include "Accessory.hpp"

#include "Log.hpp"
#include "Reflect.hpp"

#include <Unreal/FProperty.hpp>
#include <Unreal/NameTypes.hpp>
#include <Unreal/Property/FStructProperty.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstring>
#include <optional>

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
    } // namespace

    UObject* AttachFitAccessory(UObject* actor, UObject* skeletalMesh, const cosmetics::Item& item, std::string& why)
    {
        if (!item.fit || !actor || !skeletalMesh) return (why = "not a fit accessory", nullptr);
        if (!cosmetics::IsGameAccessoryAsset(item.mesh, false) || !cosmetics::IsGameAccessoryAsset(item.material, true)) return (why = "asset not allowed", nullptr);
        const cosmetics::Fit& fit = *item.fit;
        UObject* meshAsset = LoadGameAsset(Widen(item.mesh));
        UObject* material = LoadGameAsset(Widen(item.material));
        if (!meshAsset || !material) return (why = "mesh or material not found", nullptr);

        // Anchor and the character's own frame.
        const auto bone = FindBone(skeletalMesh, fit.bone);
        if (!bone) return (why = "no bone like " + fit.bone, nullptr);
        double anchor[3];
        if (!SocketLocation(skeletalMesh, *bone, anchor)) return (why = "bone position unavailable", nullptr);
        float origin[3], extent[3];
        if (fit.anchor != "bone")
        {
            if (!Bounds(skeletalMesh, origin, extent)) return (why = "character bounds unavailable", nullptr);
            const double top = origin[2] + extent[2];
            anchor[2] = fit.anchor == "top" ? top : (anchor[2] + top) / 2;
        }
        // Forward from the shoulders: right = left to right shoulder, forward = right x up.
        double forward[3] = {1, 0, 0};
        double left[3], right[3];
        const auto l = FindBone(skeletalMesh, "Arm_L"), r = FindBone(skeletalMesh, "Arm_R");
        if (l && r && SocketLocation(skeletalMesh, *l, left) && SocketLocation(skeletalMesh, *r, right))
        {
            const double rx = right[0] - left[0], ry = right[1] - left[1];
            if (std::hypot(rx, ry) > 1) forward[0] = ry, forward[1] = -rx;
        }

        UClass* meshClass = RC::Unreal::UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, STR("/Script/Engine.StaticMeshComponent"));
        UObject* component = nullptr;
        Call(actor, STR("/Script/Engine.Actor:AddComponentByClass"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("Class")) WriteObject(v, meshClass);
            else if (n == STR("bManualAttachment")) WriteBoolParam(v, p, true);
            else if (n == STR("RelativeTransform"))
                if (auto* s = RC::Unreal::CastField<RC::Unreal::FStructProperty>(p))
                    for (const char* field : {"Rotation.W", "Scale3D.X", "Scale3D.Y", "Scale3D.Z"}) game::SetStructPath(v, s->GetStruct(), field, 1);
        }, &component);
        if (!component || !meshClass || !component->IsA(meshClass)) return (why = "could not add the component", nullptr);
        // Never part of any trace, never a shadow.
        Call(component, STR("/Script/Engine.PrimitiveComponent:SetCollisionEnabled"), [](const std::wstring&, FProperty*, std::uint8_t* v) { *v = NoCollision; });
        Call(component, STR("/Script/Engine.PrimitiveComponent:SetCastShadow"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, false); });
        Call(component, STR("/Script/Engine.StaticMeshComponent:SetStaticMesh"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("NewMesh")) WriteObject(v, meshAsset);
        });
        float localMin[3]{}, localMax[3]{};
        Call(component, STR("/Script/Engine.StaticMeshComponent:GetLocalBounds"), {}, nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
            if (n == STR("Min")) ReadFloats(v, p, localMin, 3);
            else if (n == STR("Max")) ReadFloats(v, p, localMax, 3);
        });

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
            for (const auto& [name, c] : item.vector)
                Call(mid, STR("/Script/Engine.MaterialInstanceDynamic:SetVectorParameterValue"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                    if (n == STR("ParameterName")) WriteFName(v, p, Widen(name));
                    else if (n == STR("Value")) WriteFloats(v, p, {static_cast<float>(c.r), static_cast<float>(c.g), static_cast<float>(c.b), static_cast<float>(c.a)});
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
            RemoveAccessory(component);
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

    void RemoveAccessory(UObject* component)
    {
        // Only a live component of a live owner; during a level transition the level destroys it.
        if (!Alive(component) || !Alive(component->GetOuterPrivate())) return;
        Call(component, STR("/Script/Engine.ActorComponent:K2_DestroyComponent"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("Object")) WriteObject(v, component);
        });
    }
} // namespace aimmod
