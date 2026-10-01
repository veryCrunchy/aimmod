#include <aimmod/Json.hpp>

#include <charconv>
#include <cmath>

namespace aimmod::json
{
    const Value* Value::find(std::string_view key) const
    {
        for (const auto& [k, v] : members)
            if (k == key) return &v;
        return nullptr;
    }

    namespace
    {
        constexpr int MaxDepth = 32;
        constexpr std::size_t MaxElements = 100000;

        struct Reader
        {
            std::string_view text;
            std::size_t at{};
            std::size_t elements{};
            std::string error;

            bool Fail(const char* why)
            {
                if (error.empty()) error = std::string(why) + " at byte " + std::to_string(at);
                return false;
            }
            void Space()
            {
                while (at < text.size() && (text[at] == ' ' || text[at] == '\t' || text[at] == '\n' || text[at] == '\r')) ++at;
            }
            bool Literal(std::string_view word)
            {
                if (text.substr(at, word.size()) != word) return Fail("invalid literal");
                at += word.size();
                return true;
            }
            static void AppendUtf8(std::string& out, std::uint32_t c)
            {
                if (c < 0x80) out += static_cast<char>(c);
                else if (c < 0x800)
                {
                    out += static_cast<char>(0xC0 | (c >> 6));
                    out += static_cast<char>(0x80 | (c & 0x3F));
                }
                else if (c < 0x10000)
                {
                    out += static_cast<char>(0xE0 | (c >> 12));
                    out += static_cast<char>(0x80 | ((c >> 6) & 0x3F));
                    out += static_cast<char>(0x80 | (c & 0x3F));
                }
                else
                {
                    out += static_cast<char>(0xF0 | (c >> 18));
                    out += static_cast<char>(0x80 | ((c >> 12) & 0x3F));
                    out += static_cast<char>(0x80 | ((c >> 6) & 0x3F));
                    out += static_cast<char>(0x80 | (c & 0x3F));
                }
            }
            bool Hex4(std::uint32_t& out)
            {
                if (at + 4 > text.size()) return Fail("short escape");
                out = 0;
                for (int i = 0; i < 4; ++i)
                {
                    const char c = text[at++];
                    out <<= 4;
                    if (c >= '0' && c <= '9') out |= static_cast<std::uint32_t>(c - '0');
                    else if (c >= 'a' && c <= 'f') out |= static_cast<std::uint32_t>(c - 'a' + 10);
                    else if (c >= 'A' && c <= 'F') out |= static_cast<std::uint32_t>(c - 'A' + 10);
                    else return Fail("bad escape");
                }
                return true;
            }
            bool String(std::string& out)
            {
                ++at; // opening quote
                out.clear();
                while (at < text.size())
                {
                    const unsigned char c = static_cast<unsigned char>(text[at]);
                    if (c == '"')
                    {
                        ++at;
                        return true;
                    }
                    if (c < 0x20) return Fail("control character in string");
                    if (c != '\\')
                    {
                        out += static_cast<char>(c);
                        ++at;
                        continue;
                    }
                    if (++at >= text.size()) break;
                    const char e = text[at++];
                    switch (e)
                    {
                    case '"': out += '"'; break;
                    case '\\': out += '\\'; break;
                    case '/': out += '/'; break;
                    case 'b': out += '\b'; break;
                    case 'f': out += '\f'; break;
                    case 'n': out += '\n'; break;
                    case 'r': out += '\r'; break;
                    case 't': out += '\t'; break;
                    case 'u':
                    {
                        std::uint32_t c1;
                        if (!Hex4(c1)) return false;
                        if (c1 >= 0xD800 && c1 <= 0xDBFF)
                        {
                            std::uint32_t c2;
                            if (text.substr(at, 2) != "\\u") return Fail("lone surrogate");
                            at += 2;
                            if (!Hex4(c2) || c2 < 0xDC00 || c2 > 0xDFFF) return Fail("bad surrogate pair");
                            c1 = 0x10000 + ((c1 - 0xD800) << 10) + (c2 - 0xDC00);
                        }
                        else if (c1 >= 0xDC00 && c1 <= 0xDFFF) return Fail("lone surrogate");
                        AppendUtf8(out, c1);
                        break;
                    }
                    default: return Fail("bad escape");
                    }
                }
                return Fail("unterminated string");
            }
            bool Number(double& out)
            {
                const std::size_t start = at;
                if (at < text.size() && text[at] == '-') ++at;
                if (at >= text.size()) return Fail("bad number");
                if (text[at] == '0') ++at;
                else if (text[at] >= '1' && text[at] <= '9')
                    while (at < text.size() && text[at] >= '0' && text[at] <= '9') ++at;
                else return Fail("bad number");
                if (at < text.size() && text[at] == '.')
                {
                    ++at;
                    if (at >= text.size() || text[at] < '0' || text[at] > '9') return Fail("bad number");
                    while (at < text.size() && text[at] >= '0' && text[at] <= '9') ++at;
                }
                if (at < text.size() && (text[at] == 'e' || text[at] == 'E'))
                {
                    ++at;
                    if (at < text.size() && (text[at] == '+' || text[at] == '-')) ++at;
                    if (at >= text.size() || text[at] < '0' || text[at] > '9') return Fail("bad number");
                    while (at < text.size() && text[at] >= '0' && text[at] <= '9') ++at;
                }
                auto r = std::from_chars(text.data() + start, text.data() + at, out);
                if (r.ec != std::errc() || !std::isfinite(out)) return Fail("bad number");
                return true;
            }
            bool Parse(Value& v, int depth)
            {
                if (depth > MaxDepth) return Fail("too deep");
                if (++elements > MaxElements) return Fail("too many elements");
                Space();
                if (at >= text.size()) return Fail("unexpected end");
                const char c = text[at];
                if (c == '{')
                {
                    v.type = Value::Type::Object;
                    ++at;
                    Space();
                    if (at < text.size() && text[at] == '}')
                    {
                        ++at;
                        return true;
                    }
                    for (;;)
                    {
                        Space();
                        if (at >= text.size() || text[at] != '"') return Fail("expected key");
                        std::string key;
                        if (!String(key)) return false;
                        if (v.find(key)) return Fail("duplicate key");
                        Space();
                        if (at >= text.size() || text[at] != ':') return Fail("expected ':'");
                        ++at;
                        Value member;
                        if (!Parse(member, depth + 1)) return false;
                        v.members.emplace_back(std::move(key), std::move(member));
                        Space();
                        if (at < text.size() && text[at] == ',')
                        {
                            ++at;
                            continue;
                        }
                        if (at < text.size() && text[at] == '}')
                        {
                            ++at;
                            return true;
                        }
                        return Fail("expected ',' or '}'");
                    }
                }
                if (c == '[')
                {
                    v.type = Value::Type::Array;
                    ++at;
                    Space();
                    if (at < text.size() && text[at] == ']')
                    {
                        ++at;
                        return true;
                    }
                    for (;;)
                    {
                        Value item;
                        if (!Parse(item, depth + 1)) return false;
                        v.items.push_back(std::move(item));
                        Space();
                        if (at < text.size() && text[at] == ',')
                        {
                            ++at;
                            continue;
                        }
                        if (at < text.size() && text[at] == ']')
                        {
                            ++at;
                            return true;
                        }
                        return Fail("expected ',' or ']'");
                    }
                }
                if (c == '"')
                {
                    v.type = Value::Type::String;
                    return String(v.string);
                }
                if (c == 't')
                {
                    v.type = Value::Type::Bool;
                    v.boolean = true;
                    return Literal("true");
                }
                if (c == 'f')
                {
                    v.type = Value::Type::Bool;
                    return Literal("false");
                }
                if (c == 'n')
                {
                    v.type = Value::Type::Null;
                    return Literal("null");
                }
                v.type = Value::Type::Number;
                return Number(v.number);
            }
        };
    } // namespace

    std::optional<Value> Parse(std::string_view text, std::string* error, std::size_t maxBytes)
    {
        if (text.size() > maxBytes)
        {
            if (error) *error = "file too large";
            return std::nullopt;
        }
        if (text.size() >= 3 && text.substr(0, 3) == "\xEF\xBB\xBF") text.remove_prefix(3);
        Reader r{text};
        Value v;
        bool ok = r.Parse(v, 0);
        if (ok)
        {
            r.Space();
            if (r.at != text.size()) ok = r.Fail("trailing data");
        }
        if (!ok)
        {
            if (error) *error = r.error;
            return std::nullopt;
        }
        return v;
    }
} // namespace aimmod::json
