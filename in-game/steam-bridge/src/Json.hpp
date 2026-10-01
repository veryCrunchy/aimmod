#pragma once
// Minimal strict JSON for the service contract: parse with size and depth
// limits, and build output with proper escaping. No exceptions escape Parse.

#include <cstdint>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace bridge::json
{
    struct Value
    {
        enum class Type { Null, Bool, Number, String, Array, Object };
        Type type = Type::Null;
        bool boolean = false;
        double number = 0;
        std::string string;
        std::vector<Value> array;
        std::map<std::string, Value, std::less<>> object;

        bool IsObject() const { return type == Type::Object; }
        const Value* Get(std::string_view key) const;
        std::optional<std::string> Str(std::string_view key, std::size_t maxLength) const;
        std::optional<double> Num(std::string_view key) const;
        std::optional<std::int64_t> Int(std::string_view key) const; // exact integers only
        std::optional<bool> Bool(std::string_view key) const;
    };

    struct Limits
    {
        std::size_t maxBytes = 64 * 1024;
        int maxDepth = 8;
        std::size_t maxStringBytes = 32 * 1024;
        std::size_t maxMembers = 256;
    };

    // Returns nullopt on any syntax error, trailing data or limit breach.
    std::optional<Value> Parse(std::string_view text, const Limits& limits = {});

    // Appends a quoted, escaped JSON string (valid UTF-8 in, ASCII-safe out).
    void Quote(std::string& out, std::string_view text);

    // Small builder for event objects: {"k":v,...}.
    class Object
    {
    public:
        Object& Str(std::string_view key, std::string_view value);
        Object& Num(std::string_view key, double value);
        Object& Int(std::string_view key, std::int64_t value);
        Object& Bool(std::string_view key, bool value);
        Object& Null(std::string_view key);
        Object& Raw(std::string_view key, std::string_view json); // already-encoded JSON value
        std::string Done() const { return m_body + "}"; }

    private:
        void Key(std::string_view key);
        std::string m_body = "{";
        bool m_first = true;
    };

    // Builds a JSON array from already-encoded values.
    std::string Array(const std::vector<std::string>& items);

    // Splits already-encoded values into JSON arrays of at most maxBytes each, in order.
    // A value that can't fit an array on its own is left out and counted in `skipped`.
    // Always returns at least one (possibly empty) array.
    std::vector<std::string> Chunks(const std::vector<std::string>& items, std::size_t maxBytes, std::size_t& skipped);
} // namespace bridge::json
