#pragma once
// Small reflection helpers for one-off engine calls by name (the cosmetics
// preview stage and accessories). Every call runs under an SEH guard; a
// missing function or property makes the helper return false or null.
// Game thread only.
#include "GameBindings.hpp"

#include <cstdint>
#include <functional>
#include <initializer_list>
#include <optional>
#include <string>
#include <vector>

namespace RC::Unreal
{
    class FProperty;
    class UStruct;
} // namespace RC::Unreal

namespace aimmod::reflect
{
    using game::UClass;
    using game::UObject;
    using RC::Unreal::FProperty;
    using RC::Unreal::UStruct;

    FProperty* PropertyOf(UStruct* type, const wchar_t* name);
    std::uint8_t* At(UObject* object, FProperty* p);

    UObject* GetObject(UObject* object, const wchar_t* name);
    bool SetObject(UObject* object, const wchar_t* name, UObject* value);
    bool SetBool(UObject* object, const wchar_t* name, bool value);
    bool SetByte(UObject* object, const wchar_t* name, std::uint8_t value);
    std::optional<std::uint8_t> GetByte(UObject* object, const wchar_t* name);
    bool SetFloat(UObject* object, const wchar_t* name, float value);
    std::optional<float> GetFloat(UObject* object, const wchar_t* name);
    std::vector<UObject*> GetObjects(UObject* object, const wchar_t* name, std::size_t limit);
    std::wstring GetName(UObject* object, const wchar_t* name);

    // TSoftObjectPtr / TSoftClassPtr members: the asset path.
    std::wstring SoftPath(UObject* object, const wchar_t* name);
    std::vector<std::wstring> SoftPaths(UObject* object, const wchar_t* name, std::size_t limit);
    // The game's or engine's own content only ("/Game/", "/Engine/", "/MapCreator/").
    UObject* LoadGameAsset(const std::wstring& path);

    // One reflected call; `fill` writes each input parameter by name and
    // `read` sees every parameter (outputs, return value) afterwards.
    using Fill = std::function<void(const std::wstring& name, FProperty* p, std::uint8_t* value)>;
    using Read = std::function<void(const std::wstring& name, FProperty* p, const std::uint8_t* value)>;
    bool Call(UObject* self, const wchar_t* path, const Fill& fill = {}, UObject** returned = nullptr, const Read& read = {});
    UObject* Default(const wchar_t* path);

    void WriteFString(std::uint8_t* value, const std::wstring& text); // freed by Call
    void WriteFName(std::uint8_t* value, FProperty* p, const std::wstring& text);
    void WriteFloats(std::uint8_t* value, FProperty* p, std::initializer_list<float> numbers);
    bool ReadFloats(const std::uint8_t* value, FProperty* p, float* out, std::size_t count);
    void WriteObject(std::uint8_t* value, UObject* object);
    void WriteBoolParam(std::uint8_t* value, FProperty* p, bool on);
    // TArray<UObject*> parameter of one element; FreeObjectArray after the call.
    void WriteObjectArray(std::uint8_t* value, UObject* object);
    void FreeObjectArray(std::uint8_t* value);

    // Scene component helpers.
    bool Location(UObject* component, float out[3]);
    bool SetWorldLocation(UObject* component, const float at[3]);
    bool SetWorldTransform(UObject* component, const double location[3], const double rotation[3], const double scale[3]);
    bool SetVisible(UObject* component, bool visible, bool propagate);
    // World bounds of a primitive: origin and box extent.
    bool Bounds(UObject* component, float origin[3], float extent[3]);

    // The one validation helper. A pointer is an object only if it is
    // plausible (cosmetics::PlausibleObjectAddress) and GUObjectArray holds
    // exactly it at its own index; read under an SEH guard, so a garbage
    // pointer is rejected instead of faulting. Every helper here checks its
    // object argument with it, and every object it returns has passed it.
    bool InObjectArray(UObject* object);
    // A live object that is safe to call into: in GUObjectArray, not a
    // template, not being destroyed, not pending kill or unreachable, with a class.
    bool Alive(UObject* object);
    // Alive, of `type` (if given) and owned by `owner` (if given: its outer).
    bool Valid(UObject* object, UClass* type = nullptr, UObject* owner = nullptr);

    // The game's material data defaults (opacity, colours) on a mesh
    // component: without them its materials read zeros and dither away.
    bool ApplyMaterialDataDefaults(UObject* meshComponent);
} // namespace aimmod::reflect
