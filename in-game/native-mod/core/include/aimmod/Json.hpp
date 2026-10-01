#pragma once
// Small strict JSON reader for AimMod's own shipped files (catalog,
// manifest). RFC 8259 values; depth, size and member limits; duplicate
// object keys are rejected. UTF-8 in, UTF-8 out.
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace aimmod::json
{
    struct Value
    {
        enum class Type { Null, Bool, Number, String, Array, Object } type{Type::Null};
        bool boolean{};
        double number{};
        std::string string;
        std::vector<Value> items;                            // Array
        std::vector<std::pair<std::string, Value>> members;  // Object, in file order

        bool isObject() const { return type == Type::Object; }
        bool isArray() const { return type == Type::Array; }
        bool isString() const { return type == Type::String; }
        bool isNumber() const { return type == Type::Number; }
        bool isBool() const { return type == Type::Bool; }
        const Value* find(std::string_view key) const;
    };

    // nullopt for anything malformed (with a reason in `error`).
    std::optional<Value> Parse(std::string_view text, std::string* error = nullptr, std::size_t maxBytes = 1 << 20);
} // namespace aimmod::json
