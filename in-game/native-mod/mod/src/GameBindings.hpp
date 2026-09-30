#pragma once
// Name resolution and typed, read-only reflected calls. Everything here runs
// on the game thread. A binding is resolved once (per class or world) and its
// signature is verified before first use; a call never passes anything but
// zeroed parameters, a world context object and explicit sentinels.
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <vector>

namespace RC::Unreal
{
    class UObject;
    class UClass;
    class UFunction;
    class UStruct;
    class FProperty;
    class FBoolProperty;
} // namespace RC::Unreal

namespace aimmod::game
{
    using RC::Unreal::UClass;
    using RC::Unreal::UFunction;
    using RC::Unreal::UObject;

    enum class Kind : std::uint8_t
    {
        Other,
        Float,
        Double,
        Int8,
        Int16,
        Int32,
        Int64,
        UInt8,
        UInt16,
        UInt32,
        UInt64,
        Bool,
        Object,
        String,
        Vector,   // FVector (3 x float on UE4)
        Rotator,  // FRotator (3 x float on UE4)
        Timespan, // FTimespan (int64 ticks)
        Array,
    };
    const char* KindName(Kind kind);
    bool IsNumeric(Kind kind);

    struct Param
    {
        Kind kind{Kind::Other};
        std::int32_t offset{};
        std::int32_t size{};
        bool out{};
        bool ret{};
        bool worldContext{};
        RC::Unreal::FBoolProperty* boolProperty{};
        Kind inner{Kind::Other}; // Array element kind
        RC::Unreal::UStruct* structType{}; // plain structs (command inputs)
        std::string name;        // ASCII
    };

    // What a getter must look like before it is ever called.
    enum class Shape
    {
        Number,      // numeric return value
        Bool,        // bool return value
        Object,      // object return value
        String,      // FString return value
        Vector,      // FVector or FRotator return value
        Timespan,    // FTimespan return value
        ObjectArray, // TArray<UObject*> return value
        ValueElse,   // OutValue (numeric) + Result (enum/byte) out parameters
        Observe,     // hook target only; never called
        Command,     // explicit actions: plain value inputs (vector, rotator,
                     // numbers, bool, enum byte, string, object, plain struct)
                     // filled by the caller
    };

    class Getter
    {
    public:
        // Resolve by full path ("/Script/Pkg.Class:Function").
        bool BindPath(const wchar_t* path, Shape shape);
        // Resolve by name in the class chain of `cls`.
        bool BindName(UClass* cls, const wchar_t* name, Shape shape);
        bool Bind(UFunction* function, Shape shape);
        void Reset() { *this = Getter{}; }

        bool ok() const { return m_function != nullptr && m_faults < 3; }
        int faults() const { return m_faults; }
        UFunction* function() const { return m_function; }
        const std::string& error() const { return m_error; }
        std::string Signature() const;
        const std::vector<Param>& params() const { return m_params; }
        int FindParam(const char* name) const;

        // Calls; each returns nullopt/false if the binding is missing or the
        // target is null. `context` fills WorldContextObject parameters.
        std::optional<double> Number(UObject* self, UObject* context = nullptr) const;
        std::optional<bool> Bool(UObject* self, UObject* context = nullptr) const;
        UObject* Object(UObject* self, UObject* context = nullptr) const;
        bool String(UObject* self, std::string& utf8, UObject* context = nullptr) const;
        bool Vector(UObject* self, double out[3], UObject* context = nullptr) const;
        std::optional<double> TimespanSeconds(UObject* self, UObject* context = nullptr) const;
        bool Objects(UObject* self, std::vector<UObject*>& out, std::size_t limit, UObject* context = nullptr) const;
        struct ValueElseResult
        {
            bool called{};
            bool hasValue{};
            double value{};
        };
        ValueElseResult ValueElse(UObject* self, UObject* context = nullptr) const;
        // Command shape: `fill` writes each input parameter (by name/kind).
        bool Call(UObject* self, const std::function<void(std::uint8_t* value, const Param& param)>& fill,
                  const std::function<void(const std::uint8_t* buffer, const std::vector<Param>& params)>& read = nullptr) const;

    private:
        bool Invoke(UObject* self, std::uint8_t* buffer, UObject* context) const;
        void Release(std::uint8_t* buffer) const;

        UFunction* m_function{};
        UClass* m_owner{};
        mutable int m_faults{};
        Shape m_shape{Shape::Observe};
        std::uint16_t m_parmsSize{};
        std::vector<Param> m_params;
        int m_return{-1};
        int m_outValue{-1};
        int m_result{-1};
        std::string m_error;
    };

    // A reflected property read at a fixed offset of its owning class.
    class Field
    {
    public:
        bool Bind(UClass* cls, const wchar_t* name);
        void Reset() { *this = Field{}; }
        bool ok() const { return m_offset >= 0; }
        Kind kind() const { return m_kind; }
        std::optional<double> Number(const UObject* object) const;
        std::optional<bool> Bool(const UObject* object) const;
        UObject* Object(const UObject* object) const;

    private:
        std::int32_t m_offset{-1};
        std::int32_t m_size{};
        Kind m_kind{Kind::Other};
        RC::Unreal::FBoolProperty* m_bool{};
    };

    // Command input helpers. Strings are allocated with the engine allocator
    // and released by Getter::Call after the call.
    void WriteString(std::uint8_t* value, const std::string& utf8);
    bool SetStructField(std::uint8_t* value, RC::Unreal::UStruct* type, const char* field, double number);
    UObject* ReadObject(const std::uint8_t* buffer, const Param& param);

    // Describe a parameter/property.
    Param Describe(RC::Unreal::FProperty* property);
    // Reads a numeric value of `kind` at `data`.
    std::optional<double> ReadNumber(const void* data, Kind kind);

    UClass* FindClass(const wchar_t* path);
    UFunction* FindFunction(const wchar_t* path);
    // Live, non-template instances only.
    bool IsLiveInstance(UObject* object);
    std::string Narrow(const std::wstring& text);
    std::string ObjectName(UObject* object);
    std::string ClassName(UObject* object);
} // namespace aimmod::game
