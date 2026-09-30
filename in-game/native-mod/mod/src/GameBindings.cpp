#include "GameBindings.hpp"

#include <Unreal/CoreUObject/UObject/Class.hpp>
#include <Unreal/CoreUObject/UObject/FStrProperty.hpp>
#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/Core/HAL/UnrealMemory.hpp>
#include <Unreal/FProperty.hpp>
#include <Unreal/Property/FEnumProperty.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UFunction.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>
#include <Unreal/UScriptStruct.hpp>

#include <Windows.h>

#include <cstring>

namespace aimmod::game
{
    using namespace RC::Unreal;

    namespace
    {
        constexpr std::size_t MaxParms = 1024;

        struct RawArray
        {
            void* data;
            std::int32_t num;
            std::int32_t max;
        };

        // Structured exception guard around the engine call only. A fault in a
        // getter after a game update disables that getter instead of crashing.
        bool GuardedProcessEvent(UObject* self, UFunction* function, void* parms)
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

        bool EqualsIgnoreCase(const std::string& a, const char* b)
        {
            return _stricmp(a.c_str(), b) == 0;
        }
    } // namespace

    std::string Narrow(const std::wstring& text)
    {
        if (text.empty()) return {};
        int size = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
        std::string out(static_cast<std::size_t>(size > 0 ? size : 0), '\0');
        if (size > 0) WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), out.data(), size, nullptr, nullptr);
        return out;
    }

    std::string ObjectName(UObject* object)
    {
        return object ? Narrow(object->GetName()) : std::string("None");
    }

    std::string ClassName(UObject* object)
    {
        return object && object->GetClassPrivate() ? Narrow(object->GetClassPrivate()->GetName()) : std::string("None");
    }

    const char* KindName(Kind kind)
    {
        switch (kind)
        {
        case Kind::Float: return "float";
        case Kind::Double: return "double";
        case Kind::Int8: return "int8";
        case Kind::Int16: return "int16";
        case Kind::Int32: return "int32";
        case Kind::Int64: return "int64";
        case Kind::UInt8: return "uint8";
        case Kind::UInt16: return "uint16";
        case Kind::UInt32: return "uint32";
        case Kind::UInt64: return "uint64";
        case Kind::Bool: return "bool";
        case Kind::Object: return "object";
        case Kind::String: return "string";
        case Kind::Vector: return "vector";
        case Kind::Rotator: return "rotator";
        case Kind::Timespan: return "timespan";
        case Kind::Array: return "array";
        default: return "other";
        }
    }

    bool IsNumeric(Kind kind)
    {
        switch (kind)
        {
        case Kind::Float:
        case Kind::Double:
        case Kind::Int8:
        case Kind::Int16:
        case Kind::Int32:
        case Kind::Int64:
        case Kind::UInt8:
        case Kind::UInt16:
        case Kind::UInt32:
        case Kind::UInt64: return true;
        default: return false;
        }
    }

    std::optional<double> ReadNumber(const void* data, Kind kind)
    {
        switch (kind)
        {
        case Kind::Float: { float v; std::memcpy(&v, data, 4); return v; }
        case Kind::Double: { double v; std::memcpy(&v, data, 8); return v; }
        case Kind::Int8: { std::int8_t v; std::memcpy(&v, data, 1); return v; }
        case Kind::Int16: { std::int16_t v; std::memcpy(&v, data, 2); return v; }
        case Kind::Int32: { std::int32_t v; std::memcpy(&v, data, 4); return v; }
        case Kind::Int64: { std::int64_t v; std::memcpy(&v, data, 8); return static_cast<double>(v); }
        case Kind::UInt8: { std::uint8_t v; std::memcpy(&v, data, 1); return v; }
        case Kind::UInt16: { std::uint16_t v; std::memcpy(&v, data, 2); return v; }
        case Kind::UInt32: { std::uint32_t v; std::memcpy(&v, data, 4); return v; }
        case Kind::UInt64: { std::uint64_t v; std::memcpy(&v, data, 8); return static_cast<double>(v); }
        default: return std::nullopt;
        }
    }

    static Kind KindOf(FProperty* p, std::int32_t size)
    {
        if (CastField<FFloatProperty>(p)) return Kind::Float;
        if (CastField<FDoubleProperty>(p)) return Kind::Double;
        if (CastField<FIntProperty>(p)) return Kind::Int32;
        if (CastField<FInt64Property>(p)) return Kind::Int64;
        if (CastField<FInt16Property>(p)) return Kind::Int16;
        if (CastField<FUInt32Property>(p)) return Kind::UInt32;
        if (CastField<FByteProperty>(p)) return Kind::UInt8;
        if (CastField<FBoolProperty>(p)) return Kind::Bool;
        if (CastField<FEnumProperty>(p)) return size == 1 ? Kind::UInt8 : size == 2 ? Kind::UInt16 : size == 4 ? Kind::UInt32 : Kind::UInt64;
        if (CastField<FObjectPropertyBase>(p)) return Kind::Object;
        if (CastField<FStrProperty>(p)) return Kind::String;
        if (CastField<FArrayProperty>(p)) return Kind::Array;
        if (auto* s = CastField<FStructProperty>(p))
        {
            UScriptStruct* type = s->GetStruct();
            if (!type) return Kind::Other;
            FName name = type->GetNamePrivate();
            if (name == FName(STR("Vector"), FNAME_Find)) return Kind::Vector;
            if (name == FName(STR("Rotator"), FNAME_Find)) return Kind::Rotator;
            if (name == FName(STR("Timespan"), FNAME_Find)) return Kind::Timespan;
        }
        return Kind::Other;
    }

    Param Describe(FProperty* p)
    {
        Param out;
        out.name = Narrow(p->GetName());
        out.offset = p->GetOffset_Internal();
        out.size = p->GetSize();
        out.kind = KindOf(p, out.size);
        out.ret = p->HasAnyPropertyFlags(CPF_ReturnParm);
        out.out = !out.ret && p->HasAnyPropertyFlags(CPF_OutParm) && !p->HasAnyPropertyFlags(CPF_ConstParm);
        out.worldContext = out.kind == Kind::Object && out.name.find("WorldContext") != std::string::npos;
        if (out.kind == Kind::Bool) out.boolProperty = CastField<FBoolProperty>(p);
        if (out.kind == Kind::Array)
        {
            if (auto* array = CastField<FArrayProperty>(p); array && array->GetInner())
            {
                FProperty* inner = array->GetInner();
                out.inner = KindOf(inner, inner->GetSize());
            }
        }
        return out;
    }

    UClass* FindClass(const wchar_t* path)
    {
        return UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, path);
    }

    UFunction* FindFunction(const wchar_t* path)
    {
        return UObjectGlobals::StaticFindObject<UFunction*>(nullptr, nullptr, path);
    }

    bool IsLiveInstance(UObject* object)
    {
        return object && !object->HasAnyFlags(static_cast<EObjectFlags>(RF_ClassDefaultObject | RF_ArchetypeObject)) && !object->IsUnreachable();
    }

    bool Getter::BindPath(const wchar_t* path, Shape shape)
    {
        UFunction* function = FindFunction(path);
        if (!function)
        {
            Reset();
            m_error = "missing";
            return false;
        }
        return Bind(function, shape);
    }

    bool Getter::BindName(UClass* cls, const wchar_t* name, Shape shape)
    {
        Reset();
        if (!cls)
        {
            m_error = "class missing";
            return false;
        }
        FName fname(name, FNAME_Find);
        if (fname == FName())
        {
            m_error = "missing";
            return false;
        }
        for (UStruct* type = cls; type; type = type->GetSuperStruct())
        {
            for (UField* child = type->GetChildren(); child; child = child->GetNext())
            {
                if (child->GetNamePrivate() == fname && child->IsA<UFunction>()) return Bind(static_cast<UFunction*>(child), shape);
            }
        }
        m_error = "missing";
        return false;
    }

    bool Getter::Bind(UFunction* function, Shape shape)
    {
        Reset();
        m_shape = shape;
        if (!function)
        {
            m_error = "missing";
            return false;
        }
        std::vector<Param> params;
        for (FProperty* p : function->ForEachProperty())
        {
            if (!p->HasAnyPropertyFlags(CPF_Parm)) continue;
            params.push_back(Describe(p));
        }
        const std::uint16_t parmsSize = function->GetParmsSize();
        int ret = -1, outValue = -1, result = -1;
        for (int i = 0; i < static_cast<int>(params.size()); ++i)
        {
            const Param& p = params[static_cast<std::size_t>(i)];
            if (p.offset < 0 || p.offset + p.size > parmsSize)
            {
                m_error = "parameter outside frame";
                return false;
            }
            if (p.ret) ret = i;
            else if (p.out && EqualsIgnoreCase(p.name, "OutValue")) outValue = i;
            else if (p.out && EqualsIgnoreCase(p.name, "Result")) result = i;
        }
        m_params = std::move(params);
        m_parmsSize = parmsSize;
        m_return = ret;
        m_outValue = outValue;
        m_result = result;
        if (shape != Shape::Observe)
        {
            if (parmsSize > MaxParms)
            {
                m_error = "frame too large";
                m_params.clear();
                return false;
            }
            // Only world context objects and plain integers (zero) are passed in.
            for (const Param& p : m_params)
            {
                if (p.ret || p.out) continue;
                if (p.worldContext || p.kind == Kind::Int32) continue;
                m_error = "unsupported input " + p.name + ":" + KindName(p.kind);
                m_params.clear();
                return false;
            }
            auto returns = [&](bool ok) {
                if (!ok) m_error = "unexpected signature " + Signature();
                return ok;
            };
            const Param* r = ret >= 0 ? &m_params[static_cast<std::size_t>(ret)] : nullptr;
            bool ok = false;
            switch (shape)
            {
            case Shape::Number: ok = returns(r && IsNumeric(r->kind)); break;
            case Shape::Bool: ok = returns(r && r->kind == Kind::Bool && r->boolProperty); break;
            case Shape::Object: ok = returns(r && r->kind == Kind::Object && r->size == sizeof(void*)); break;
            case Shape::String: ok = returns(r && r->kind == Kind::String && r->size == sizeof(RawArray)); break;
            case Shape::Vector: ok = returns(r && (r->kind == Kind::Vector || r->kind == Kind::Rotator) && (r->size == 12 || r->size == 24)); break;
            case Shape::Timespan: ok = returns(r && r->kind == Kind::Timespan && r->size == 8); break;
            case Shape::ObjectArray: ok = returns(r && r->kind == Kind::Array && r->inner == Kind::Object && r->size == sizeof(RawArray)); break;
            case Shape::ValueElse:
                ok = returns(outValue >= 0 && result >= 0 && IsNumeric(m_params[static_cast<std::size_t>(outValue)].kind) &&
                             m_params[static_cast<std::size_t>(result)].kind == Kind::UInt8);
                break;
            default: ok = true; break;
            }
            if (!ok)
            {
                m_params.clear();
                return false;
            }
        }
        m_function = function;
        UObject* outer = function->GetOuterPrivate();
        m_owner = outer && outer->IsA<UClass>() ? static_cast<UClass*>(outer) : nullptr;
        return true;
    }

    int Getter::FindParam(const char* name) const
    {
        for (int i = 0; i < static_cast<int>(m_params.size()); ++i)
            if (EqualsIgnoreCase(m_params[static_cast<std::size_t>(i)].name, name)) return i;
        return -1;
    }

    std::string Getter::Signature() const
    {
        std::string out = m_function ? Narrow(m_function->GetName()) : std::string("?");
        out += '(';
        bool first = true;
        std::string returns = "void";
        for (const Param& p : m_params)
        {
            if (p.ret)
            {
                returns = KindName(p.kind);
                continue;
            }
            if (!first) out += ", ";
            first = false;
            if (p.out) out += "out ";
            out += KindName(p.kind);
            out += ' ';
            out += p.name;
        }
        out += ") -> " + returns;
        return out;
    }

    bool Getter::Invoke(UObject* self, std::uint8_t* buffer, UObject* context) const
    {
        if (!ok() || !self || m_shape == Shape::Observe) return false;
        // Never call a function on an object of another class.
        if (m_owner && !self->IsA(m_owner)) return false;
        std::memset(buffer, 0, m_parmsSize);
        for (const Param& p : m_params)
        {
            if (!p.worldContext || p.ret || p.out) continue;
            UObject* value = context ? context : self;
            std::memcpy(buffer + p.offset, &value, sizeof(value));
        }
        if (GuardedProcessEvent(self, m_function, buffer)) return true;
        ++m_faults; // disabled after repeated faults
        return false;
    }

    void Getter::Release(std::uint8_t* buffer) const
    {
        for (const Param& p : m_params)
        {
            if (!(p.ret || p.out) || (p.kind != Kind::String && p.kind != Kind::Array)) continue;
            RawArray raw;
            std::memcpy(&raw, buffer + p.offset, sizeof(raw));
            if (raw.data) FMemory::Free(raw.data);
        }
    }

    std::optional<double> Getter::Number(UObject* self, UObject* context) const
    {
        alignas(16) std::uint8_t buffer[MaxParms];
        if (m_shape != Shape::Number || !Invoke(self, buffer, context)) return std::nullopt;
        const Param& r = m_params[static_cast<std::size_t>(m_return)];
        auto value = ReadNumber(buffer + r.offset, r.kind);
        Release(buffer);
        return value;
    }

    std::optional<bool> Getter::Bool(UObject* self, UObject* context) const
    {
        alignas(16) std::uint8_t buffer[MaxParms];
        if (m_shape != Shape::Bool || !Invoke(self, buffer, context)) return std::nullopt;
        const Param& r = m_params[static_cast<std::size_t>(m_return)];
        bool value = r.boolProperty->GetPropertyValue(buffer + r.offset);
        Release(buffer);
        return value;
    }

    UObject* Getter::Object(UObject* self, UObject* context) const
    {
        alignas(16) std::uint8_t buffer[MaxParms];
        if (m_shape != Shape::Object || !Invoke(self, buffer, context)) return nullptr;
        UObject* value;
        std::memcpy(&value, buffer + m_params[static_cast<std::size_t>(m_return)].offset, sizeof(value));
        Release(buffer);
        return value;
    }

    bool Getter::String(UObject* self, std::string& utf8, UObject* context) const
    {
        alignas(16) std::uint8_t buffer[MaxParms];
        if (m_shape != Shape::String || !Invoke(self, buffer, context)) return false;
        RawArray raw;
        std::memcpy(&raw, buffer + m_params[static_cast<std::size_t>(m_return)].offset, sizeof(raw));
        bool ok = false;
        if (raw.data && raw.num > 0 && raw.num <= 4096)
        {
            const auto* chars = static_cast<const wchar_t*>(raw.data);
            std::size_t length = static_cast<std::size_t>(raw.num);
            while (length > 0 && chars[length - 1] == L'\0') --length;
            utf8 = Narrow(std::wstring(chars, length));
            ok = true;
        }
        else if (raw.num == 0)
        {
            utf8.clear();
            ok = true;
        }
        Release(buffer);
        return ok;
    }

    bool Getter::Vector(UObject* self, double out[3], UObject* context) const
    {
        alignas(16) std::uint8_t buffer[MaxParms];
        if (m_shape != Shape::Vector || !Invoke(self, buffer, context)) return false;
        const Param& r = m_params[static_cast<std::size_t>(m_return)];
        if (r.size == 12)
        {
            float v[3];
            std::memcpy(v, buffer + r.offset, sizeof(v));
            for (int i = 0; i < 3; ++i) out[i] = v[i];
        }
        else std::memcpy(out, buffer + r.offset, 24);
        return true;
    }

    std::optional<double> Getter::TimespanSeconds(UObject* self, UObject* context) const
    {
        alignas(16) std::uint8_t buffer[MaxParms];
        if (m_shape != Shape::Timespan || !Invoke(self, buffer, context)) return std::nullopt;
        std::int64_t ticks;
        std::memcpy(&ticks, buffer + m_params[static_cast<std::size_t>(m_return)].offset, sizeof(ticks));
        return static_cast<double>(ticks) / 1e7;
    }

    bool Getter::Objects(UObject* self, std::vector<UObject*>& out, std::size_t limit, UObject* context) const
    {
        alignas(16) std::uint8_t buffer[MaxParms];
        out.clear();
        if (m_shape != Shape::ObjectArray || !Invoke(self, buffer, context)) return false;
        RawArray raw;
        std::memcpy(&raw, buffer + m_params[static_cast<std::size_t>(m_return)].offset, sizeof(raw));
        bool ok = raw.num >= 0 && (raw.num == 0 || raw.data);
        if (ok)
        {
            const auto* items = static_cast<UObject* const*>(raw.data);
            for (std::int32_t i = 0; i < raw.num && out.size() < limit; ++i)
                if (items[i]) out.push_back(items[i]);
        }
        Release(buffer);
        return ok;
    }

    Getter::ValueElseResult Getter::ValueElse(UObject* self, UObject* context) const
    {
        alignas(16) std::uint8_t buffer[MaxParms];
        ValueElseResult r;
        if (m_shape != Shape::ValueElse || !Invoke(self, buffer, context)) return r;
        r.called = true;
        std::uint8_t result;
        std::memcpy(&result, buffer + m_params[static_cast<std::size_t>(m_result)].offset, 1);
        // EValueElseResult::HasValue == 0. Else (1) carries no value.
        if (result == 0)
        {
            const Param& v = m_params[static_cast<std::size_t>(m_outValue)];
            if (auto n = ReadNumber(buffer + v.offset, v.kind))
            {
                r.hasValue = true;
                r.value = *n;
            }
        }
        Release(buffer);
        return r;
    }

    bool Field::Bind(UClass* cls, const wchar_t* name)
    {
        Reset();
        if (!cls) return false;
        FName fname(name, FNAME_Find);
        if (fname == FName()) return false;
        for (FProperty* p : cls->ForEachPropertyInChain())
        {
            if (p->GetFName() != fname) continue;
            Param d = Describe(p);
            m_offset = d.offset;
            m_size = d.size;
            m_kind = d.kind;
            m_bool = d.boolProperty;
            return true;
        }
        return false;
    }

    std::optional<double> Field::Number(const UObject* object) const
    {
        if (!object || m_offset < 0 || !IsNumeric(m_kind)) return std::nullopt;
        return ReadNumber(reinterpret_cast<const std::uint8_t*>(object) + m_offset, m_kind);
    }

    std::optional<bool> Field::Bool(const UObject* object) const
    {
        if (!object || m_offset < 0 || !m_bool) return std::nullopt;
        return m_bool->GetPropertyValue(reinterpret_cast<const std::uint8_t*>(object) + m_offset);
    }

    UObject* Field::Object(const UObject* object) const
    {
        if (!object || m_offset < 0 || m_kind != Kind::Object || m_size != sizeof(void*)) return nullptr;
        UObject* value;
        std::memcpy(&value, reinterpret_cast<const std::uint8_t*>(object) + m_offset, sizeof(value));
        return value;
    }
} // namespace aimmod::game
