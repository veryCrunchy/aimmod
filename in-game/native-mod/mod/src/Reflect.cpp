#include "Reflect.hpp"

#include <Unreal/CoreUObject/UObject/Class.hpp>
#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/Core/HAL/UnrealMemory.hpp>
#include <Unreal/FProperty.hpp>
#include <Unreal/NameTypes.hpp>
#include <Unreal/Property/FArrayProperty.hpp>
#include <Unreal/Property/FBoolProperty.hpp>
#include <Unreal/Property/FNameProperty.hpp>
#include <Unreal/Property/FObjectProperty.hpp>
#include <Unreal/Property/FStrProperty.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UFunction.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectArray.hpp>
#include <Unreal/UObjectGlobals.hpp>
#include <aimmod/CosmeticsPreview.hpp>

#include <Windows.h>

#include <cstring>

namespace aimmod::reflect
{
    using namespace RC::Unreal;

    namespace
    {
        struct RawArray
        {
            void* data;
            std::int32_t num;
            std::int32_t max;
        };

        bool Guarded(UObject* self, UFunction* function, void* parms)
        {
            __try
            {
                self->ProcessEvent(function, parms);
                return true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return false;
            }
        }

        // TSoftObjectPtr / TSoftClassPtr (UE 4.26: weak pointer, tag, then
        // FSoftObjectPath{FName AssetPathName, FString SubPath}).
        constexpr std::int32_t SoftPtrSize = 0x28, SoftPathOffset = 0x10;
        std::wstring SoftPathAt(const std::uint8_t* value)
        {
            FName name;
            std::memcpy(&name, value + SoftPathOffset, sizeof(name));
            std::wstring path = name.ToString();
            return path == STR("None") ? std::wstring{} : path;
        }
    } // namespace

    FProperty* PropertyOf(UStruct* type, const wchar_t* name)
    {
        if (!type) return nullptr;
        FName fname(name, FNAME_Find);
        if (fname == FName()) return nullptr;
        for (FProperty* p : type->ForEachPropertyInChain())
            if (p->GetFName() == fname) return p;
        return nullptr;
    }
    std::uint8_t* At(UObject* object, FProperty* p) { return reinterpret_cast<std::uint8_t*>(object) + p->GetOffset_Internal(); }

    UObject* GetObject(UObject* object, const wchar_t* name)
    {
        FProperty* p = InObjectArray(object) ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
        if (!p || !CastField<FObjectPropertyBase>(p)) return nullptr;
        UObject* value;
        std::memcpy(&value, At(object, p), sizeof(value));
        return InObjectArray(value) ? value : nullptr;
    }
    bool SetObject(UObject* object, const wchar_t* name, UObject* value)
    {
        FProperty* p = InObjectArray(object) ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
        if (!p || !CastField<FObjectPropertyBase>(p)) return false;
        std::memcpy(At(object, p), &value, sizeof(value));
        return true;
    }
    bool SetBool(UObject* object, const wchar_t* name, bool value)
    {
        auto* p = InObjectArray(object) ? CastField<FBoolProperty>(PropertyOf(object->GetClassPrivate(), name)) : nullptr;
        if (!p) return false;
        p->SetPropertyValueInContainer(object, value);
        return true;
    }
    bool SetByte(UObject* object, const wchar_t* name, std::uint8_t value)
    {
        FProperty* p = InObjectArray(object) ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
        if (!p || p->GetSize() != 1) return false;
        *At(object, p) = value;
        return true;
    }
    std::optional<std::uint8_t> GetByte(UObject* object, const wchar_t* name)
    {
        FProperty* p = InObjectArray(object) ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
        if (!p || p->GetSize() != 1) return std::nullopt;
        return *At(object, p);
    }
    bool SetFloat(UObject* object, const wchar_t* name, float value)
    {
        FProperty* p = InObjectArray(object) ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
        if (!p || p->GetSize() != 4) return false;
        std::memcpy(At(object, p), &value, 4);
        return true;
    }
    std::optional<float> GetFloat(UObject* object, const wchar_t* name)
    {
        FProperty* p = InObjectArray(object) ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
        if (!p || p->GetSize() != 4) return std::nullopt;
        float value;
        std::memcpy(&value, At(object, p), 4);
        return value;
    }
    std::vector<UObject*> GetObjects(UObject* object, const wchar_t* name, std::size_t limit)
    {
        std::vector<UObject*> out;
        auto* p = InObjectArray(object) ? CastField<FArrayProperty>(PropertyOf(object->GetClassPrivate(), name)) : nullptr;
        if (!p || !CastField<FObjectPropertyBase>(p->GetInner())) return out;
        RawArray raw;
        std::memcpy(&raw, At(object, p), sizeof(raw));
        if (!raw.data || raw.num < 0) return out;
        for (std::int32_t i = 0; i < raw.num && i < 4096 && out.size() < limit; ++i)
            if (UObject* o = static_cast<UObject**>(raw.data)[i]; InObjectArray(o)) out.push_back(o);
        return out;
    }
    std::wstring GetName(UObject* object, const wchar_t* name)
    {
        FProperty* p = InObjectArray(object) ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
        if (!p || !CastField<FNameProperty>(p)) return {};
        FName value;
        std::memcpy(&value, At(object, p), sizeof(value));
        return value.ToString();
    }

    std::wstring SoftPath(UObject* object, const wchar_t* name)
    {
        FProperty* p = InObjectArray(object) ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
        if (!p || p->GetSize() != SoftPtrSize) return {};
        return SoftPathAt(At(object, p));
    }
    std::vector<std::wstring> SoftPaths(UObject* object, const wchar_t* name, std::size_t limit)
    {
        std::vector<std::wstring> out;
        auto* p = InObjectArray(object) ? CastField<FArrayProperty>(PropertyOf(object->GetClassPrivate(), name)) : nullptr;
        if (!p || p->GetInner()->GetSize() != SoftPtrSize) return out;
        RawArray raw;
        std::memcpy(&raw, At(object, p), sizeof(raw));
        if (!raw.data || raw.num < 0) return out;
        for (std::int32_t i = 0; i < raw.num && out.size() < limit; ++i) out.push_back(SoftPathAt(static_cast<const std::uint8_t*>(raw.data) + i * SoftPtrSize));
        return out;
    }
    UObject* LoadGameAsset(const std::wstring& path)
    {
        if ((path.rfind(STR("/Game/"), 0) != 0 && path.rfind(STR("/Engine/"), 0) != 0) || path.find(STR("..")) != std::wstring::npos) return nullptr;
        return game::FindOrLoadAsset(path);
    }

    bool Call(UObject* self, const wchar_t* path, const Fill& fill, UObject** returned, const Read& read)
    {
        auto* fn = InObjectArray(self) ? UObjectGlobals::StaticFindObject<UFunction*>(nullptr, nullptr, path) : nullptr;
        if (!fn || fn->GetParmsSize() > 1024) return false;
        alignas(16) std::uint8_t buffer[1024];
        std::memset(buffer, 0, fn->GetParmsSize());
        std::vector<std::uint8_t*> strings;
        FProperty* ret = nullptr;
        for (FProperty* p : fn->ForEachProperty())
        {
            if (!p->HasAnyPropertyFlags(CPF_Parm)) continue;
            if (p->HasAnyPropertyFlags(CPF_ReturnParm)) { ret = p; continue; }
            std::uint8_t* value = buffer + p->GetOffset_Internal();
            if (fill) fill(p->GetName(), p, value);
            if (CastField<FStrProperty>(p)) strings.push_back(value);
        }
        const bool ok = Guarded(self, fn, buffer);
        if (ok && read)
            for (FProperty* p : fn->ForEachProperty())
                if (p->HasAnyPropertyFlags(CPF_Parm)) read(p->GetName(), p, buffer + p->GetOffset_Internal());
        for (std::uint8_t* s : strings)
        {
            RawArray raw;
            std::memcpy(&raw, s, sizeof(raw));
            if (raw.data) FMemory::Free(raw.data);
        }
        if (ok && returned && ret && CastField<FObjectPropertyBase>(ret))
        {
            std::memcpy(returned, buffer + ret->GetOffset_Internal(), sizeof(UObject*));
            if (!InObjectArray(*returned)) *returned = nullptr;
        }
        return ok;
    }
    UObject* Default(const wchar_t* path) { return UObjectGlobals::StaticFindObject<UObject*>(nullptr, nullptr, path); }

    void WriteFString(std::uint8_t* value, const std::wstring& text)
    {
        const auto length = static_cast<std::int32_t>(text.size());
        auto* chars = static_cast<wchar_t*>(FMemory::Malloc(static_cast<SIZE_T>(length + 1) * sizeof(wchar_t)));
        if (!chars) return;
        std::memcpy(chars, text.c_str(), static_cast<std::size_t>(length + 1) * sizeof(wchar_t));
        RawArray raw{chars, length + 1, length + 1};
        std::memcpy(value, &raw, sizeof(raw));
    }
    void WriteFName(std::uint8_t* value, FProperty* p, const std::wstring& text)
    {
        FName name(text.c_str(), FNAME_Add);
        if (p->GetSize() == sizeof(name)) std::memcpy(value, &name, sizeof(name));
    }
    void WriteFloats(std::uint8_t* value, FProperty* p, std::initializer_list<float> numbers)
    {
        if (static_cast<std::size_t>(p->GetSize()) < numbers.size() * sizeof(float)) return;
        std::size_t i = 0;
        for (float n : numbers) std::memcpy(value + sizeof(float) * i++, &n, sizeof(float));
    }
    bool ReadFloats(const std::uint8_t* value, FProperty* p, float* out, std::size_t count)
    {
        if (static_cast<std::size_t>(p->GetSize()) < count * sizeof(float)) return false;
        std::memcpy(out, value, count * sizeof(float));
        return true;
    }
    void WriteObject(std::uint8_t* value, UObject* object) { std::memcpy(value, &object, sizeof(object)); }
    void WriteBoolParam(std::uint8_t* value, FProperty* p, bool on)
    {
        if (auto* b = CastField<FBoolProperty>(p)) b->SetPropertyValue(value, on);
    }
    void WriteObjectArray(std::uint8_t* value, UObject* object)
    {
        auto* data = static_cast<UObject**>(FMemory::Malloc(sizeof(UObject*)));
        if (!data) return;
        data[0] = object;
        RawArray raw{data, 1, 1};
        std::memcpy(value, &raw, sizeof(raw));
    }
    void FreeObjectArray(std::uint8_t* value)
    {
        RawArray raw;
        std::memcpy(&raw, value, sizeof(raw));
        if (raw.data) FMemory::Free(raw.data);
    }

    bool Location(UObject* component, float out[3])
    {
        bool ok = false;
        Call(component, STR("/Script/Engine.SceneComponent:K2_GetComponentLocation"), {}, nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
            if (n == STR("ReturnValue")) ok = ReadFloats(v, p, out, 3);
        });
        return ok;
    }
    bool SetWorldLocation(UObject* component, const float at[3])
    {
        return Call(component, STR("/Script/Engine.SceneComponent:K2_SetWorldLocation"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("NewLocation")) WriteFloats(v, p, {at[0], at[1], at[2]});
            else if (n == STR("bTeleport")) WriteBoolParam(v, p, true);
        });
    }
    bool SetWorldTransform(UObject* component, const double location[3], const double rotation[3], const double scale[3])
    {
        const bool placed = Call(component, STR("/Script/Engine.SceneComponent:K2_SetWorldLocationAndRotation"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("NewLocation")) WriteFloats(v, p, {static_cast<float>(location[0]), static_cast<float>(location[1]), static_cast<float>(location[2])});
            else if (n == STR("NewRotation")) WriteFloats(v, p, {static_cast<float>(rotation[0]), static_cast<float>(rotation[1]), static_cast<float>(rotation[2])});
            else if (n == STR("bTeleport")) WriteBoolParam(v, p, true);
        });
        const bool scaled = Call(component, STR("/Script/Engine.SceneComponent:SetWorldScale3D"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("NewScale")) WriteFloats(v, p, {static_cast<float>(scale[0]), static_cast<float>(scale[1]), static_cast<float>(scale[2])});
        });
        return placed && scaled;
    }
    bool SetVisible(UObject* component, bool visible, bool propagate)
    {
        const bool a = Call(component, STR("/Script/Engine.SceneComponent:SetVisibility"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("bNewVisibility")) WriteBoolParam(v, p, visible);
            else if (n == STR("bPropagateToChildren")) WriteBoolParam(v, p, propagate);
        });
        const bool b = Call(component, STR("/Script/Engine.SceneComponent:SetHiddenInGame"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("NewHidden")) WriteBoolParam(v, p, !visible);
            else if (n == STR("bPropagateToChildren")) WriteBoolParam(v, p, propagate);
        });
        return a && b;
    }
    bool Bounds(UObject* component, float origin[3], float extent[3])
    {
        bool o = false, e = false;
        Call(Default(STR("/Script/Engine.Default__KismetSystemLibrary")), STR("/Script/Engine.KismetSystemLibrary:GetComponentBounds"),
             [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                 if (n == STR("Component")) WriteObject(v, component);
             },
             nullptr,
             [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
                 if (n == STR("Origin")) o = ReadFloats(v, p, origin, 3);
                 else if (n == STR("BoxExtent")) e = ReadFloats(v, p, extent, 3);
             });
        return o && e;
    }

    namespace
    {
        bool ArrayHolds(UObject* object)
        {
            __try
            {
                const std::int32_t index = object->GetInternalIndex();
                if (index < 0 || index >= FUObjectArray::GetNumElements()) return false;
                FUObjectItem* item = FUObjectArray::IndexToObject(index);
                return item && item->GetUObject() == object;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return false;
            }
        }
    } // namespace

    bool InObjectArray(UObject* object)
    {
        return object && aimmod::PlausibleObjectAddress(reinterpret_cast<std::uintptr_t>(object)) && ArrayHolds(object);
    }

    bool Valid(UObject* object, UClass* type, UObject* owner)
    {
        if (!Alive(object)) return false;
        if (type && (!InObjectArray(type) || !object->IsA(type))) return false;
        if (owner && object->GetOuterPrivate() != owner) return false;
        return true;
    }

    bool Alive(UObject* object)
    {
        if (!InObjectArray(object) || !game::IsLiveInstance(object)) return false;
        if (object->HasAnyFlags(static_cast<EObjectFlags>(RF_BeginDestroyed | RF_FinishDestroyed))) return false;
        if (object->HasAnyInternalFlags(static_cast<EInternalObjectFlags>(static_cast<std::int32_t>(EInternalObjectFlags::PendingKill) |
                                                                          static_cast<std::int32_t>(EInternalObjectFlags::Unreachable))))
            return false;
        UClass* type = object->GetClassPrivate();
        return type && !type->IsUnreachable();
    }

    bool ApplyMaterialDataDefaults(UObject* meshComponent)
    {
        if (!meshComponent) return false;
        // The game's own defaults for every material data slot ...
        std::uint8_t* array = nullptr;
        const bool defaults = Call(Default(STR("/Script/GameSkillsTrainer.Default__SkinFunctionLibrary")),
                                   STR("/Script/GameSkillsTrainer.SkinFunctionLibrary:ApplyAllCustomPrimitiveDataDefaultsBatch"),
                                   [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                                       if (n == STR("It")) WriteObjectArray(v, meshComponent), array = v;
                                   },
                                   nullptr, [&](const std::wstring& n, FProperty*, const std::uint8_t* v) {
                                       if (n == STR("It")) FreeObjectArray(const_cast<std::uint8_t*>(v));
                                   });
        if (!defaults && array) FreeObjectArray(array); // the call did not run: free here
        // ... and full opacity, in case the defaults leave the model faded out.
        std::int32_t opacity = -1;
        Call(Default(STR("/Script/GameSkillsTrainer.Default__MaterialPrimitiveDataIndices")), STR("/Script/GameSkillsTrainer.MaterialPrimitiveDataIndices:Opacity"), {},
             nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
                 if (n == STR("ReturnValue") && p->GetSize() == 4) std::memcpy(&opacity, v, 4);
             });
        bool opaque = false;
        if (opacity >= 0 && opacity < 64)
            opaque = Call(meshComponent, STR("/Script/Engine.PrimitiveComponent:SetCustomPrimitiveDataFloat"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("DataIndex")) std::memcpy(v, &opacity, 4);
                else if (n == STR("Value")) WriteFloats(v, p, {1.0f});
            });
        return defaults || opaque;
    }
} // namespace aimmod::reflect
