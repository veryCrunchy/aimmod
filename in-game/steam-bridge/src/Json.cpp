#include "Json.hpp"

#include <cerrno>
#include <cmath>
#include <cstdio>
#include <cstdlib>

namespace bridge::json
{
    namespace
    {
        class Parser
        {
        public:
            Parser(std::string_view text, const Limits& limits) : m_text(text), m_limits(limits) {}

            bool Run(Value& out)
            {
                Skip();
                if (!ParseValue(out, 0)) return false;
                Skip();
                return m_pos == m_text.size();
            }

        private:
            void Skip()
            {
                while (m_pos < m_text.size() && (m_text[m_pos] == ' ' || m_text[m_pos] == '\t' || m_text[m_pos] == '\n' || m_text[m_pos] == '\r'))
                    ++m_pos;
            }
            bool Literal(std::string_view word)
            {
                if (m_text.substr(m_pos, word.size()) != word) return false;
                m_pos += word.size();
                return true;
            }
            bool ParseValue(Value& out, int depth)
            {
                if (depth > m_limits.maxDepth || m_pos >= m_text.size()) return false;
                const char c = m_text[m_pos];
                if (c == '{') return ParseObject(out, depth);
                if (c == '[') return ParseArray(out, depth);
                if (c == '"')
                {
                    out.type = Value::Type::String;
                    return ParseString(out.string);
                }
                if (c == 't') { out.type = Value::Type::Bool; out.boolean = true; return Literal("true"); }
                if (c == 'f') { out.type = Value::Type::Bool; out.boolean = false; return Literal("false"); }
                if (c == 'n') { out.type = Value::Type::Null; return Literal("null"); }
                return ParseNumber(out);
            }
            bool ParseObject(Value& out, int depth)
            {
                out.type = Value::Type::Object;
                ++m_pos;
                Skip();
                if (m_pos < m_text.size() && m_text[m_pos] == '}') { ++m_pos; return true; }
                while (true)
                {
                    Skip();
                    std::string key;
                    if (m_pos >= m_text.size() || m_text[m_pos] != '"' || !ParseString(key)) return false;
                    Skip();
                    if (m_pos >= m_text.size() || m_text[m_pos] != ':') return false;
                    ++m_pos;
                    Skip();
                    Value child;
                    if (!ParseValue(child, depth + 1)) return false;
                    if (out.object.count(key) || out.object.size() >= m_limits.maxMembers) return false; // no duplicates
                    out.object.emplace(std::move(key), std::move(child));
                    Skip();
                    if (m_pos >= m_text.size()) return false;
                    if (m_text[m_pos] == ',') { ++m_pos; continue; }
                    if (m_text[m_pos] == '}') { ++m_pos; return true; }
                    return false;
                }
            }
            bool ParseArray(Value& out, int depth)
            {
                out.type = Value::Type::Array;
                ++m_pos;
                Skip();
                if (m_pos < m_text.size() && m_text[m_pos] == ']') { ++m_pos; return true; }
                while (true)
                {
                    Skip();
                    Value child;
                    if (!ParseValue(child, depth + 1)) return false;
                    if (out.array.size() >= m_limits.maxMembers) return false;
                    out.array.push_back(std::move(child));
                    Skip();
                    if (m_pos >= m_text.size()) return false;
                    if (m_text[m_pos] == ',') { ++m_pos; continue; }
                    if (m_text[m_pos] == ']') { ++m_pos; return true; }
                    return false;
                }
            }
            static void Utf8(std::string& out, std::uint32_t cp)
            {
                if (cp < 0x80) out.push_back(static_cast<char>(cp));
                else if (cp < 0x800)
                {
                    out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
                    out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
                }
                else if (cp < 0x10000)
                {
                    out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
                    out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
                    out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
                }
                else
                {
                    out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
                    out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
                    out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
                    out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
                }
            }
            bool Hex4(std::uint32_t& out)
            {
                if (m_pos + 4 > m_text.size()) return false;
                out = 0;
                for (int i = 0; i < 4; ++i)
                {
                    const char c = m_text[m_pos++];
                    out <<= 4;
                    if (c >= '0' && c <= '9') out |= static_cast<std::uint32_t>(c - '0');
                    else if (c >= 'a' && c <= 'f') out |= static_cast<std::uint32_t>(c - 'a' + 10);
                    else if (c >= 'A' && c <= 'F') out |= static_cast<std::uint32_t>(c - 'A' + 10);
                    else return false;
                }
                return true;
            }
            bool ParseString(std::string& out)
            {
                ++m_pos; // opening quote
                while (m_pos < m_text.size())
                {
                    const unsigned char c = static_cast<unsigned char>(m_text[m_pos]);
                    if (out.size() > m_limits.maxStringBytes) return false;
                    if (c == '"') { ++m_pos; return true; }
                    if (c < 0x20) return false;
                    if (c != '\\')
                    {
                        out.push_back(static_cast<char>(c));
                        ++m_pos;
                        continue;
                    }
                    if (++m_pos >= m_text.size()) return false;
                    const char e = m_text[m_pos++];
                    switch (e)
                    {
                    case '"': out.push_back('"'); break;
                    case '\\': out.push_back('\\'); break;
                    case '/': out.push_back('/'); break;
                    case 'b': out.push_back('\b'); break;
                    case 'f': out.push_back('\f'); break;
                    case 'n': out.push_back('\n'); break;
                    case 'r': out.push_back('\r'); break;
                    case 't': out.push_back('\t'); break;
                    case 'u':
                    {
                        std::uint32_t cp = 0;
                        if (!Hex4(cp)) return false;
                        if (cp >= 0xD800 && cp <= 0xDBFF)
                        {
                            std::uint32_t low = 0;
                            if (m_pos + 2 > m_text.size() || m_text[m_pos] != '\\' || m_text[m_pos + 1] != 'u') return false;
                            m_pos += 2;
                            if (!Hex4(low) || low < 0xDC00 || low > 0xDFFF) return false;
                            cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
                        }
                        else if (cp >= 0xDC00 && cp <= 0xDFFF) return false;
                        Utf8(out, cp);
                        break;
                    }
                    default: return false;
                    }
                }
                return false;
            }
            bool ParseNumber(Value& out)
            {
                const std::size_t start = m_pos;
                if (m_pos < m_text.size() && m_text[m_pos] == '-') ++m_pos;
                if (m_pos >= m_text.size() || !(m_text[m_pos] >= '0' && m_text[m_pos] <= '9')) return false;
                if (m_text[m_pos] == '0') ++m_pos;
                else while (m_pos < m_text.size() && m_text[m_pos] >= '0' && m_text[m_pos] <= '9') ++m_pos;
                if (m_pos < m_text.size() && m_text[m_pos] == '.')
                {
                    ++m_pos;
                    if (m_pos >= m_text.size() || !(m_text[m_pos] >= '0' && m_text[m_pos] <= '9')) return false;
                    while (m_pos < m_text.size() && m_text[m_pos] >= '0' && m_text[m_pos] <= '9') ++m_pos;
                }
                if (m_pos < m_text.size() && (m_text[m_pos] == 'e' || m_text[m_pos] == 'E'))
                {
                    ++m_pos;
                    if (m_pos < m_text.size() && (m_text[m_pos] == '+' || m_text[m_pos] == '-')) ++m_pos;
                    if (m_pos >= m_text.size() || !(m_text[m_pos] >= '0' && m_text[m_pos] <= '9')) return false;
                    while (m_pos < m_text.size() && m_text[m_pos] >= '0' && m_text[m_pos] <= '9') ++m_pos;
                }
                const std::string token(m_text.substr(start, m_pos - start));
                if (token.size() > 32) return false;
                char* end = nullptr;
                const double value = std::strtod(token.c_str(), &end);
                if (!end || *end != '\0' || !std::isfinite(value)) return false;
                out.type = Value::Type::Number;
                out.number = value;
                out.string = token; // keeps the exact digits for 64-bit integers
                return true;
            }

            std::string_view m_text;
            const Limits& m_limits;
            std::size_t m_pos = 0;
        };
    } // namespace

    const Value* Value::Get(std::string_view key) const
    {
        if (type != Type::Object) return nullptr;
        const auto it = object.find(key);
        return it == object.end() ? nullptr : &it->second;
    }

    std::optional<std::string> Value::Str(std::string_view key, std::size_t maxLength) const
    {
        const Value* v = Get(key);
        if (!v || v->type != Type::String || v->string.size() > maxLength) return std::nullopt;
        return v->string;
    }

    std::optional<double> Value::Num(std::string_view key) const
    {
        const Value* v = Get(key);
        if (!v || v->type != Type::Number) return std::nullopt;
        return v->number;
    }

    std::optional<std::int64_t> Value::Int(std::string_view key) const
    {
        const Value* v = Get(key);
        if (!v || v->type != Type::Number) return std::nullopt;
        if (v->string.find_first_of(".eE") != std::string::npos) return std::nullopt;
        char* end = nullptr;
        errno = 0;
        const long long value = std::strtoll(v->string.c_str(), &end, 10);
        if (errno != 0 || !end || *end != '\0') return std::nullopt;
        return value;
    }

    std::optional<bool> Value::Bool(std::string_view key) const
    {
        const Value* v = Get(key);
        if (!v || v->type != Type::Bool) return std::nullopt;
        return v->boolean;
    }

    std::optional<Value> Parse(std::string_view text, const Limits& limits)
    {
        if (text.empty() || text.size() > limits.maxBytes) return std::nullopt;
        Value value;
        Parser parser(text, limits);
        if (!parser.Run(value)) return std::nullopt;
        return value;
    }

    void Quote(std::string& out, std::string_view text)
    {
        out.push_back('"');
        for (const char ch : text)
        {
            const unsigned char c = static_cast<unsigned char>(ch);
            switch (c)
            {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20 || c == 0x7f)
                {
                    char buf[8];
                    std::snprintf(buf, sizeof(buf), "\\u%04x", c);
                    out += buf;
                }
                else out.push_back(ch); // UTF-8 passes through
            }
        }
        out.push_back('"');
    }

    void Object::Key(std::string_view key)
    {
        if (!m_first) m_body.push_back(',');
        m_first = false;
        Quote(m_body, key);
        m_body.push_back(':');
    }
    Object& Object::Str(std::string_view key, std::string_view value)
    {
        Key(key);
        Quote(m_body, value);
        return *this;
    }
    Object& Object::Num(std::string_view key, double value)
    {
        Key(key);
        char buf[40];
        if (!std::isfinite(value)) value = 0;
        std::snprintf(buf, sizeof(buf), "%.6g", value);
        m_body += buf;
        return *this;
    }
    Object& Object::Int(std::string_view key, std::int64_t value)
    {
        Key(key);
        m_body += std::to_string(value);
        return *this;
    }
    Object& Object::Bool(std::string_view key, bool value)
    {
        Key(key);
        m_body += value ? "true" : "false";
        return *this;
    }
    Object& Object::Null(std::string_view key)
    {
        Key(key);
        m_body += "null";
        return *this;
    }
    Object& Object::Raw(std::string_view key, std::string_view encoded)
    {
        Key(key);
        m_body += encoded;
        return *this;
    }

    std::string Array(const std::vector<std::string>& items)
    {
        std::string out = "[";
        for (std::size_t i = 0; i < items.size(); ++i)
        {
            if (i) out.push_back(',');
            out += items[i];
        }
        out.push_back(']');
        return out;
    }

    std::vector<std::string> Chunks(const std::vector<std::string>& items, std::size_t maxBytes, std::size_t& skipped)
    {
        skipped = 0;
        std::vector<std::string> out;
        std::string current = "[";
        for (const auto& item : items)
        {
            if (item.size() + 2 > maxBytes)
            {
                ++skipped;
                continue;
            }
            const std::size_t needed = current.size() + (current.size() > 1 ? 1 : 0) + item.size() + 1;
            if (needed > maxBytes && current.size() > 1)
            {
                current.push_back(']');
                out.push_back(std::move(current));
                current = "[";
            }
            if (current.size() > 1) current.push_back(',');
            current += item;
        }
        current.push_back(']');
        out.push_back(std::move(current));
        return out;
    }
} // namespace bridge::json
